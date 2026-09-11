using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Serilog.Core;
using Serilog.Events;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.Services;

public class LogService : ILogService
{
    private static readonly LogEventLevel DisabledLogLevel = (LogEventLevel)100;

    public static readonly LoggingLevelSwitch LevelSwitch = new LoggingLevelSwitch(DisabledLogLevel);

    private volatile bool _isLoggingEnabled;

    public bool IsLoggingEnabled
    {
        get => _isLoggingEnabled;
        set
        {
            _isLoggingEnabled = value;
            LevelSwitch.MinimumLevel = value ? LogEventLevel.Information : DisabledLogLevel;
        }
    }

    private static readonly string _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly Regex LogRegex = new Regex(@"^(?<Timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<Level>\w{3})\] (?<Message>.*)$", RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.U4)]
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public IEnumerable<LogFileInfo> GetLogFiles()
    {
        if (!Directory.Exists(_logDirectory))
            Directory.CreateDirectory(_logDirectory);

        return Directory.GetFiles(_logDirectory, "stockanalyzer-*.log")
            .Select(f => {
                var info = new FileInfo(f);
                return new LogFileInfo(info.Name, info.LastWriteTime, info.Length, info.FullName);
            })
            .OrderByDescending(f => f.LastModified);
    }

    public async Task<IEnumerable<LogEntry>> GetLogsAsync(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        var filePath = Path.Combine(_logDirectory, safeName);
        if (!File.Exists(filePath)) return Enumerable.Empty<LogEntry>();

        var entries = new List<LogEntry>();
        LogEntry? currentEntry = null;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var match = LogRegex.Match(line);
                if (match.Success)
                {
                    if (currentEntry != null) entries.Add(currentEntry);

                    currentEntry = new LogEntry
                    {
                        Timestamp = DateTime.ParseExact(match.Groups["Timestamp"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        Level = ParseLevel(match.Groups["Level"].Value),
                        Message = match.Groups["Message"].Value
                    };
                }
                else if (currentEntry != null)
                {
                    if (string.IsNullOrEmpty(currentEntry.Exception))
                        currentEntry = currentEntry with { Exception = line };
                    else
                        currentEntry = currentEntry with { Exception = currentEntry.Exception + Environment.NewLine + line };
                }
            }
            if (currentEntry != null) entries.Add(currentEntry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to read log file: {ex.Message}");
        }

        return entries.AsEnumerable().Reverse(); // Latest first
    }

    public async Task ExportLogAsync(string fileName, string targetPath)
    {
        var safeName = Path.GetFileName(fileName);
        var sourcePath = Path.Combine(_logDirectory, safeName);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source log file not found", safeName);

        using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        await sourceStream.CopyToAsync(targetStream);
    }

    public async Task RenameLogAsync(string oldName, string newName)
    {
        var oldPath = Path.Combine(_logDirectory, Path.GetFileName(oldName));
        var newPath = Path.Combine(_logDirectory, Path.GetFileName(newName));

        if (!File.Exists(oldPath)) throw new FileNotFoundException("Log file not found", oldName);
        if (File.Exists(newPath)) throw new IOException($"File already exists: {newName}");

        File.Move(oldPath, newPath);
        await Task.CompletedTask;
    }

    public async Task DeleteLogAsync(string fileName)
    {
        var filePath = Path.GetFullPath(Path.Combine(_logDirectory, Path.GetFileName(fileName)));
        if (File.Exists(filePath))
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var fileOp = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = filePath + "\0\0", // Double null terminator required
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION
                };
                int result = SHFileOperation(ref fileOp);
                if (result != 0)
                {
                    throw new IOException($"Failed to move file to Recycle Bin. Error code: {result}");
                }
            }
            else
            {
                File.Delete(filePath);
            }
        }
        await Task.CompletedTask;
    }

    public async Task<string> GetRawLogContentAsync(string fileName)
    {
        var filePath = Path.Combine(_logDirectory, Path.GetFileName(fileName));
        if (!File.Exists(filePath)) return string.Empty;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private LogLevel ParseLevel(string level)
    {
        return level switch
        {
            "VRB" => LogLevel.Verbose,
            "DBG" => LogLevel.Debug,
            "INF" => LogLevel.Information,
            "WRN" => LogLevel.Warning,
            "ERR" => LogLevel.Error,
            "FTL" => LogLevel.Fatal,
            _ => LogLevel.Information
        };
    }
}
