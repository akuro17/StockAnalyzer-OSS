using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Loads/saves <see cref="BacktestReportDefaults"/> under Data/Backtest/, via the same
/// PathDiscovery + AtomicJsonFile convention already used by NotesSettingsManager/PythonSettingsManager/
/// FontSettingsManager. Invalid or absent content falls back as one complete built-in settings object;
/// permission and other I/O failures remain visible to the caller.
/// </summary>
public sealed class BacktestReportSettingsManager : IBacktestReportSettingsManager
{
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<BacktestReportSettingsManager> _logger;

    public BacktestReportSettingsManager(
        string? settingsFilePath = null,
        ILogger<BacktestReportSettingsManager>? logger = null)
    {
        _settingsFilePath = settingsFilePath ?? PathDiscovery.ResolveBacktestReportSettingsPath("backtest_report_settings.json");
        _logger = logger ?? NullLogger<BacktestReportSettingsManager>.Instance;
    }

    public async Task<BacktestReportDefaults> LoadAsync() =>
        (await LoadWithStatusAsync().ConfigureAwait(false)).Defaults;

    public async Task<BacktestReportSettingsLoadResult> LoadWithStatusAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            BacktestReportDefaults? data;
            try
            {
                data = await AtomicJsonFile.LoadAsync<BacktestReportDefaults?>(_settingsFilePath).ConfigureAwait(false);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogDebug(ex, "Backtest report settings were not found; built-in defaults will be used.");
                return Fallback();
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogDebug(ex, "Backtest report settings directory was not found; built-in defaults will be used.");
                return Fallback();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Backtest report settings JSON is invalid; built-in defaults will be used.");
                return Fallback();
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Backtest report settings schema is unsupported; built-in defaults will be used.");
                return Fallback();
            }

            try
            {
                return new BacktestReportSettingsLoadResult(
                    BacktestReportDefaults.SnapshotValidated(data!),
                    BacktestReportSettingsLoadStatus.Loaded);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Backtest report settings values are invalid; built-in defaults will be used.");
                return Fallback();
            }
        }
        finally
        {
            _semaphore.Release();
        }

        static BacktestReportSettingsLoadResult Fallback() => new(
            BacktestReportDefaults.BuiltIn,
            BacktestReportSettingsLoadStatus.BuiltInFallback);
    }

    public async Task SaveAsync(BacktestReportDefaults defaults)
    {
        BacktestReportDefaults snapshot = BacktestReportDefaults.SnapshotValidated(defaults);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.SaveAsync(_settingsFilePath, snapshot).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
