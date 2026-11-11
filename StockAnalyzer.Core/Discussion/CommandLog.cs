using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Discussion;

/// <summary>One tool call as the command log keeps it: names and sizes, never a value (spec 5.11).</summary>
public sealed record CommandLogEntry(long N, DateTimeOffset Utc, string Tool, IReadOnlyList<string> ArgKeys, int ArgBytes, string Outcome, long Milliseconds, string RequestId);

/// <summary>
/// The per-process record of tool calls made while the discussion option is on. It can only be given argument NAMES and the total argument size, so a token, a message
/// body or a result cannot reach it. Core owns it (and its optional file) because the MCP server assembly may not write files.
/// </summary>
public interface ICommandLog : IDisposable
{
    /// <summary>Records one finished call. Never throws: a failure of the optional file turns the file off and is reported once.</summary>
    void Record(string tool, IReadOnlyList<string> argumentKeys, int argumentBytes, string outcome, long milliseconds, string requestId);

    /// <summary>The newest <paramref name="count"/> entries, oldest first.</summary>
    IReadOnlyList<CommandLogEntry> Tail(int count);
}

/// <summary>
/// A bounded ring of the newest calls, and optionally a rolling file below the temp folder (spec 5.11). The file holds one JSON object per line, UTF-8 without BOM,
/// LF, flushed after every record. At most <see cref="DiscussionSettings.CommandLogGenerations"/> files exist (the current one included), each at most
/// <see cref="DiscussionSettings.CommandLogMaxFileBytes"/> bytes. Files older than <see cref="DiscussionSettings.CommandLogMaxAgeHours"/> are deleted before the first write,
/// and this process deletes its own files when it ends normally.
/// </summary>
public sealed class CommandLog : ICommandLog
{
    private readonly object _sync = new();
    private readonly DiscussionSettings _settings;
    private readonly TimeProvider _time;
    private readonly string _folder;
    private readonly string _currentPath;
    private readonly Action<string>? _report;
    private readonly CommandLogEntry?[] _ring;
    private long _next = 1;
    private FileStream? _stream;
    private long _length;
    private bool _fileMode;
    private bool _prepared;
    private bool _disposed;

    /// <param name="settings">The validated settings (capacity, file switch, file limits).</param>
    /// <param name="time">The clock. Null uses the system clock.</param>
    /// <param name="folder">Where the files go. Null uses <see cref="DiscussionTempFolder.Root"/>; only tests choose another.</param>
    /// <param name="report">Receives the one line written when the file is turned off after an I/O failure (the host writes it to standard error).</param>
    /// <param name="processId">Only tests: the process id used in the file name.</param>
    public CommandLog(DiscussionSettings settings, TimeProvider? time = null, string? folder = null, Action<string>? report = null, int? processId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _folder = folder ?? DiscussionTempFolder.Root;
        _report = report;
        _ring = new CommandLogEntry?[settings.CommandLogCapacity];
        _fileMode = settings.CommandLogToFile;
        string name = $"{DiscussionLimits.CommandLogFilePrefix}{processId ?? Environment.ProcessId}-{_time.GetUtcNow():yyyyMMddHHmmss}{DiscussionLimits.CommandLogFileExtension}";
        _currentPath = Path.Combine(_folder, name);
    }

    /// <summary>True while the file is being written (the setting is on and no I/O failure has turned it off).</summary>
    public bool FileModeActive
    {
        get
        {
            lock (_sync)
            {
                return _fileMode;
            }
        }
    }

    public void Record(string tool, IReadOnlyList<string> argumentKeys, int argumentBytes, string outcome, long milliseconds, string requestId)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(argumentKeys);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(requestId);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            long n = _next++;
            var entry = new CommandLogEntry(
                n,
                _time.GetUtcNow(),
                Cut(tool),
                argumentKeys.Take(DiscussionLimits.CommandLogMaxArgKeys).Select(Cut).ToArray(),
                argumentBytes,
                Cut(outcome),
                milliseconds,
                Cut(requestId));
            _ring[(int)((n - 1) % _ring.Length)] = entry;
            if (_fileMode)
            {
                WriteToFile(entry);
            }
        }
    }

    public IReadOnlyList<CommandLogEntry> Tail(int count)
    {
        lock (_sync)
        {
            long last = _next - 1;
            long take = Math.Min(count, Math.Min(last, _ring.Length));
            var result = new List<CommandLogEntry>((int)Math.Max(0, take));
            for (long n = last - take + 1; n <= last; n++)
            {
                result.Add(_ring[(int)((n - 1) % _ring.Length)]!);
            }

            return result;
        }
    }

    /// <summary>Closes the file and deletes this process's own command files (the current one and its rotated generations). Files of other processes are left alone.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseStream();
            if (_prepared)
            {
                foreach (string path in OwnFiles())
                {
                    BestEffortFile.TryDelete(path);
                }
            }
        }
    }

    // ---- File handling (all under the lock) -----------------------------------------------------------------------------------------------

    private static string Cut(string text) => DiscussionText.Truncate(text, DiscussionLimits.CommandLogTextMaxChars);

    private IEnumerable<string> OwnFiles()
    {
        yield return _currentPath;
        for (int generation = 1; generation < _settings.CommandLogGenerations; generation++)
        {
            yield return Generation(generation);
        }
    }

    private string Generation(int number) => $"{_currentPath}.{number}";

    private void WriteToFile(CommandLogEntry entry)
    {
        try
        {
            if (!_prepared)
            {
                Prepare();
            }

            byte[] line = Serialize(entry);
            if (_length > 0 && _length + line.Length > _settings.CommandLogMaxFileBytes)
            {
                Rotate();
            }

            _stream!.Write(line, 0, line.Length);
            _stream.Flush();
            _length += line.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _fileMode = false;
            CloseStream();
            _report?.Invoke($"The discussion command log file was turned off after an I/O failure ({error.GetType().Name}); calls are still kept in memory.");
        }
    }

    /// <summary>Start-up clean-up of old files, then the folder and the first file. Runs before the first write.</summary>
    private void Prepare()
    {
        Directory.CreateDirectory(_folder);
        DateTime oldest = _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(_settings.CommandLogMaxAgeHours);
        foreach (string path in Directory.EnumerateFiles(_folder, $"{DiscussionLimits.CommandLogFilePrefix}*{DiscussionLimits.CommandLogFileExtension}*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < oldest)
                {
                    File.Delete(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Another process may hold the file; the clean-up is best effort.
            }
        }

        OpenCurrent();
        _prepared = true;
    }

    private void OpenCurrent()
    {
        _stream = new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _length = 0;
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>Drops the oldest generation, shifts the others up, makes the current file generation 1 and starts a new current file.</summary>
    private void Rotate()
    {
        CloseStream();
        int generations = _settings.CommandLogGenerations;
        if (generations > 1)
        {
            BestEffortFile.TryDelete(Generation(generations - 1));
            for (int number = generations - 2; number >= 1; number--)
            {
                if (File.Exists(Generation(number)))
                {
                    File.Move(Generation(number), Generation(number + 1));
                }
            }

            File.Move(_currentPath, Generation(1));
        }
        else
        {
            File.Delete(_currentPath);
        }

        OpenCurrent();
    }

    private static byte[] Serialize(CommandLogEntry entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("n", entry.N);
            writer.WriteString("utc", DiscussionNames.FormatUtc(entry.Utc));
            writer.WriteString("tool", entry.Tool);
            writer.WriteStartArray("argKeys");
            foreach (string key in entry.ArgKeys)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteNumber("argBytes", entry.ArgBytes);
            writer.WriteString("outcome", entry.Outcome);
            writer.WriteNumber("ms", entry.Milliseconds);
            writer.WriteString("requestId", entry.RequestId);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }
}
