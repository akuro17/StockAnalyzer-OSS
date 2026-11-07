#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T6 of the discussion board: the command log of spec 5.11 (ring, rolling file, clean-up, failure handling, bounded records).</summary>
public sealed class CommandLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dhub-commandlog-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _reports = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private CommandLog Create(Action<DiscussionSettings>? configure = null, ManualTimeProvider? time = null, int processId = 4242)
    {
        var settings = new DiscussionSettings();
        configure?.Invoke(settings);
        return new CommandLog(settings, time ?? new ManualTimeProvider(), _folder, _reports.Add, processId);
    }

    private static void Add(ICommandLog log, int number, string tool = "sa_discussion_post")
        => log.Record(tool, new[] { "sessionId", "content" }, 100 + number, "ok", number, number.ToString());

    /// <summary>The lines of a file the log may still hold open for writing.</summary>
    private static string[] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd().TrimEnd('\n').Split('\n');
    }

    private string[] Files() => Directory.Exists(_folder) ? Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()! : Array.Empty<string>();

    // ---- Ring -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRingKeepsTheNewestEntriesInOrderAndNumbersThemWithoutGaps()
    {
        using CommandLog log = Create(s => s.CommandLogCapacity = 100);
        for (int i = 1; i <= 250; i++)
        {
            Add(log, i);
        }

        IReadOnlyList<CommandLogEntry> tail = log.Tail(1000);

        Assert.Equal(100, tail.Count);
        Assert.Equal(Enumerable.Range(151, 100).Select(n => (long)n), tail.Select(e => e.N));
        Assert.Equal(3, log.Tail(3).Count);
        Assert.Equal(new long[] { 248, 249, 250 }, log.Tail(3).Select(e => e.N));
        Assert.Empty(log.Tail(0));
    }

    [Fact]
    public void AnEntryHoldsTheNamesTheSizeTheOutcomeAndTheTime()
    {
        var time = new ManualTimeProvider();
        using CommandLog log = Create(time: time);

        log.Record("sa_discussion_read", new[] { "sessionId", "participantToken" }, 412, "LIMIT_EXCEEDED", 7, "12");

        CommandLogEntry entry = Assert.Single(log.Tail(5));
        Assert.Equal((1L, "sa_discussion_read", 412, "LIMIT_EXCEEDED", 7L, "12"), (entry.N, entry.Tool, entry.ArgBytes, entry.Outcome, entry.Milliseconds, entry.RequestId));
        Assert.Equal(new[] { "sessionId", "participantToken" }, entry.ArgKeys);
        Assert.Equal(time.GetUtcNow(), entry.Utc);
    }

    [Fact]
    public void ARecordIsBoundedWhateverAClientSends()
    {
        using CommandLog log = Create();
        string[] manyKeys = Enumerable.Range(0, 100).Select(i => "key" + i).ToArray();

        log.Record(new string('t', 500), manyKeys, 9, new string('o', 500), 1, new string('i', 500) + "\U0001F4C8");
        log.Record("t", new[] { new string('k', 63) + "\U0001F4C8" }, 9, "ok", 1, "1");

        CommandLogEntry big = log.Tail(2)[0];
        Assert.Equal(64, big.Tool.Length);
        Assert.Equal(32, big.ArgKeys.Count);
        Assert.Equal(64, big.Outcome.Length);
        Assert.Equal(64, big.RequestId.Length);
        Assert.Equal(new string('k', 63), log.Tail(1)[0].ArgKeys[0]); // cut before the first half of a surrogate pair
    }

    // ---- File off by default ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithTheFileSwitchOffNothingIsCreatedOnDisk()
    {
        using (CommandLog log = Create())
        {
            Add(log, 1);
            Assert.False(log.FileModeActive);
        }

        Assert.False(Directory.Exists(_folder));
    }

    // ---- File format ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFileHoldsOneJsonObjectPerLineInUtf8WithoutBomWithLfAndIsFlushedPerRecord()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 7, 3, 5, 0, TimeSpan.Zero));
        using CommandLog log = Create(s => s.CommandLogToFile = true, time);

        log.Record("sa_discussion_post", new[] { "sessionId", "content" }, 412, "ok", 3, "7");
        log.Record("sa_x", new[] { "line\nbreak", "日本" }, 1, "ok", 0, "\"id\"");

        string path = Assert.Single(Directory.GetFiles(_folder));
        Assert.Equal("commands-4242-20261107030500.jsonl", Path.GetFileName(path));
        // Read while the log still holds the file open: every record has been flushed.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        Assert.NotEqual(0xEF, bytes[0]);
        string text = new UTF8Encoding(false).GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        string[] lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal(
            """{"n":1,"utc":"2026-11-07T03:05:00.0000000Z","tool":"sa_discussion_post","argKeys":["sessionId","content"],"argBytes":412,"outcome":"ok","ms":3,"requestId":"7"}""",
            lines[0]);
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal("line\nbreak", second.RootElement.GetProperty("argKeys")[0].GetString());
        Assert.Equal("\"id\"", second.RootElement.GetProperty("requestId").GetString());
    }

    // ---- Rotation ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFileRotatesAtTheSizeLimitAndKeepsExactlyTheConfiguredNumberOfFiles()
    {
        using CommandLog log = Create(s => { s.CommandLogToFile = true; s.CommandLogMaxFileBytes = 65536; s.CommandLogGenerations = 3; });

        for (int i = 1; i <= 2000; i++)
        {
            Add(log, i);
        }

        string[] files = Files();
        Assert.Equal(new[] { "commands-4242-20261107030000.jsonl", "commands-4242-20261107030000.jsonl.1", "commands-4242-20261107030000.jsonl.2" }, files);
        foreach (string file in Directory.GetFiles(_folder))
        {
            Assert.True(new FileInfo(file).Length <= 65536, file);
        }

        // The newest records are in the current file, older ones in .1, the oldest kept in .2; together they are contiguous.
        long LastN(string name) => JsonDocument.Parse(Lines(Path.Combine(_folder, name)).Last()).RootElement.GetProperty("n").GetInt64();
        long FirstN(string name) => JsonDocument.Parse(Lines(Path.Combine(_folder, name)).First()).RootElement.GetProperty("n").GetInt64();
        Assert.Equal(2000, LastN(files[0]));
        Assert.Equal(LastN(files[1]) + 1, FirstN(files[0]));
        Assert.Equal(LastN(files[2]) + 1, FirstN(files[1]));
        Assert.True(FirstN(files[2]) > 1); // the oldest generations were dropped
    }

    [Fact]
    public void WithOneGenerationTheCurrentFileIsSimplyStartedAgain()
    {
        using CommandLog log = Create(s => { s.CommandLogToFile = true; s.CommandLogMaxFileBytes = 65536; s.CommandLogGenerations = 1; });

        for (int i = 1; i <= 2000; i++)
        {
            Add(log, i);
        }

        string only = Assert.Single(Files());
        Assert.Equal("commands-4242-20261107030000.jsonl", only);
        Assert.True(new FileInfo(Path.Combine(_folder, only)).Length <= 65536);
        Assert.Equal(2000, JsonDocument.Parse(Lines(Path.Combine(_folder, only)).Last()).RootElement.GetProperty("n").GetInt64());
    }

    // ---- Start-up clean-up and shutdown --------------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeTheFirstWriteFilesOlderThanTheAgeLimitAreDeletedAndOthersKept()
    {
        Directory.CreateDirectory(_folder);
        string oldFile = Path.Combine(_folder, "commands-1-20250101000000.jsonl");
        string oldRotated = Path.Combine(_folder, "commands-1-20250101000000.jsonl.2");
        string youngFile = Path.Combine(_folder, "commands-2-20261107020000.jsonl");
        string foreign = Path.Combine(_folder, "notes.txt");
        var now = new ManualTimeProvider();
        foreach (string path in new[] { oldFile, oldRotated, youngFile, foreign })
        {
            File.WriteAllText(path, "x");
        }

        File.SetLastWriteTimeUtc(oldFile, now.GetUtcNow().UtcDateTime.AddHours(-25));
        File.SetLastWriteTimeUtc(oldRotated, now.GetUtcNow().UtcDateTime.AddHours(-25));
        File.SetLastWriteTimeUtc(youngFile, now.GetUtcNow().UtcDateTime.AddHours(-23));
        File.SetLastWriteTimeUtc(foreign, now.GetUtcNow().UtcDateTime.AddHours(-500));

        using CommandLog log = Create(s => s.CommandLogToFile = true, now);
        Assert.True(File.Exists(oldFile)); // nothing is touched before the first write
        Add(log, 1);

        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(oldRotated));
        Assert.True(File.Exists(youngFile));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void DisposingDeletesOnlyThisProcessesOwnFiles()
    {
        CommandLog mine = Create(s => { s.CommandLogToFile = true; s.CommandLogMaxFileBytes = 65536; s.CommandLogGenerations = 3; }, processId: 1);
        using CommandLog other = Create(s => s.CommandLogToFile = true, processId: 2);
        Add(other, 1);
        for (int i = 1; i <= 2000; i++)
        {
            Add(mine, i);
        }

        Assert.Equal(4, Files().Length);
        mine.Dispose();

        Assert.Equal(new[] { "commands-2-20261107030000.jsonl" }, Files());
        Add(mine, 3); // a disposed log ignores records
        Assert.Equal(new[] { "commands-2-20261107030000.jsonl" }, Files());
    }

    // ---- Failure handling ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnIoFailureTurnsTheFileOffReportsOnceAndNeverStopsTheRing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder, "a file where the folder should be");
        try
        {
            using CommandLog log = Create(s => s.CommandLogToFile = true);

            Add(log, 1);
            Add(log, 2);
            Add(log, 3);

            Assert.False(log.FileModeActive);
            Assert.Equal(new long[] { 1, 2, 3 }, log.Tail(10).Select(e => e.N));
            string report = Assert.Single(_reports);
            Assert.Contains("turned off", report, StringComparison.Ordinal);
            Assert.DoesNotContain(_folder, report, StringComparison.Ordinal); // no path in the report
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Fact]
    public void AFileThatCannotBeRotatedBecauseSomeoneHoldsItTurnsTheFileOff()
    {
        using CommandLog log = Create(s => { s.CommandLogToFile = true; s.CommandLogMaxFileBytes = 65536; s.CommandLogGenerations = 2; });
        Add(log, 1);
        string current = Assert.Single(Directory.GetFiles(_folder));
        // Another handle without delete sharing keeps the current file from being renamed on Windows; on other systems the rotation succeeds and the test only checks the ring.
        using var blocker = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        for (int i = 2; i <= 2000; i++)
        {
            Add(log, i);
        }

        Assert.Equal(2000, log.Tail(1)[0].N);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(log.FileModeActive);
            Assert.Single(_reports);
        }
    }

    [Fact]
    public void ThereIsNoWayToGiveTheLogAValueOnlyNamesAndSizes()
    {
        System.Reflection.MethodInfo record = typeof(ICommandLog).GetMethod(nameof(ICommandLog.Record))!;

        Assert.Equal(
            new[] { typeof(string), typeof(IReadOnlyList<string>), typeof(int), typeof(string), typeof(long), typeof(string) },
            record.GetParameters().Select(p => p.ParameterType));
        Assert.DoesNotContain(typeof(CommandLogEntry).GetProperties(), p => p.PropertyType == typeof(System.Text.Json.JsonElement) || p.PropertyType == typeof(object));
    }
}
