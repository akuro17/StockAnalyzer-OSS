using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Exports a computed <see cref="BacktestReport"/> to Data/Backtest/Reports/, via the same
/// PathDiscovery + AtomicJsonFile convention as <see cref="BacktestReportSettingsManager"/>. The caller
/// supplies the file name explicitly (no auto-naming/timestamp convention exists, so none is invented
/// here). The results UI also uses the destination-directory overload for its folder-picker workflow.
/// </summary>
public sealed class BacktestReportExporter : IBacktestReportExporter
{
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly ILogger<BacktestReportExporter> _logger;

    public BacktestReportExporter(ILogger<BacktestReportExporter>? logger = null)
    {
        _logger = logger ?? NullLogger<BacktestReportExporter>.Instance;
    }

    public async Task ExportAsync(BacktestReport report, string fileName)
    {
        ValidateExport(report, fileName);

        string path = PathDiscovery.ResolveBacktestReportExportPath(fileName);
        await SaveSerializedAsync(path, report).ConfigureAwait(false);
    }

    /// <summary>User-chosen destination variant (SAで改善, Y:\Temp\sa_improvement_plan_BacktestResultsUiPolish.md
    /// Task 1): <paramref name="directoryPath"/> comes from the caller's own folder picker, so it is used
    /// verbatim (no <see cref="PathDiscovery"/> resolution/fallback) - only its existence is ensured.</summary>
    public async Task ExportAsync(BacktestReport report, string directoryPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) throw new ArgumentException("directoryPath must not be blank.", nameof(directoryPath));
        ValidateExport(report, fileName);

        await SaveToDirectoryAsync(directoryPath, fileName, report).ConfigureAwait(false);
    }

    public async Task ExportEvaluationAsync(BacktestEvaluationArtifact artifact, string fileName)
    {
        ValidateEvaluationExport(artifact, fileName);
        string path = PathDiscovery.ResolveBacktestReportExportPath(fileName);
        await SaveSerializedAsync(path, new BacktestEvaluationExportEnvelope(artifact)).ConfigureAwait(false);
    }

    public async Task ExportEvaluationAsync(BacktestEvaluationArtifact artifact, string directoryPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) throw new ArgumentException("directoryPath must not be blank.", nameof(directoryPath));
        ValidateEvaluationExport(artifact, fileName);

        await SaveToDirectoryAsync(directoryPath, fileName, new BacktestEvaluationExportEnvelope(artifact)).ConfigureAwait(false);
    }

    private async Task SaveToDirectoryAsync<T>(string directoryPath, string fileName, T value)
    {
        string path = Path.Combine(directoryPath, fileName);
        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directoryPath);
            await AtomicJsonFile.SaveAsync(path, value).ConfigureAwait(false);
            _logger.LogDebug("Backtest export written: FileName={FileName}, PayloadType={PayloadType}", fileName, typeof(T).Name);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private async Task SaveSerializedAsync<T>(string path, T value)
    {
        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.SaveAsync(path, value).ConfigureAwait(false);
            _logger.LogDebug("Backtest export written: FileName={FileName}, PayloadType={PayloadType}", Path.GetFileName(path), typeof(T).Name);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static void ValidateExport(BacktestReport report, string fileName)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateFileName(fileName);
        BacktestReportValidator.Validate(report);
    }

    private static void ValidateEvaluationExport(BacktestEvaluationArtifact artifact, string fileName)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateFileName(fileName);
        if (artifact.Report is { } report)
        {
            BacktestReportValidator.Validate(report);
        }
        if (artifact.RunStatus == Models.Backtest.Engine.RunStatus.Completed && artifact.Report is null)
        {
            throw new ArgumentException("A completed evaluation artifact must contain a report.", nameof(artifact));
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("fileName must not be blank.", nameof(fileName));
        }
        if (fileName is "." or ".." || Path.IsPathRooted(fileName) ||
            fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':') || fileName.Contains('\0') ||
            fileName.EndsWith(' ') || fileName.EndsWith('.'))
        {
            throw new ArgumentException("fileName must be a safe leaf name.", nameof(fileName));
        }
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("fileName contains an invalid character.", nameof(fileName));
        }

        int firstDot = fileName.IndexOf('.');
        string deviceName = (firstDot >= 0 ? fileName[..firstDot] : fileName).TrimEnd(' ', '.');
        if (IsReservedWindowsDeviceName(deviceName))
        {
            throw new ArgumentException("fileName is a reserved Windows device name.", nameof(fileName));
        }
    }

    private static bool IsReservedWindowsDeviceName(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Length == 4 && value[3] is >= '1' and <= '9')
        {
            return value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
