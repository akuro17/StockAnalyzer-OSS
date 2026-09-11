using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.Services;

public interface ILogService
{
    bool IsLoggingEnabled { get; set; }
    IEnumerable<LogFileInfo> GetLogFiles();
    Task<IEnumerable<LogEntry>> GetLogsAsync(string fileName);
    Task ExportLogAsync(string fileName, string targetPath);
    Task RenameLogAsync(string oldName, string newName);
    Task DeleteLogAsync(string fileName);
    Task<string> GetRawLogContentAsync(string fileName);
}
