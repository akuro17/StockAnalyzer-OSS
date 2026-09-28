using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

/// <summary>
/// Loads/saves <see cref="BacktestDefaultSettings"/> under Data/Backtest/ with the same PathDiscovery +
/// AtomicJsonFile convention as <see cref="Reporting.BacktestReportSettingsManager"/>. Absent or invalid
/// content falls back to the built-in defaults; permission and other I/O failures remain visible.
/// </summary>
public sealed class BacktestDefaultSettingsManager : IBacktestDefaultSettingsManager
{
    /// <summary>File name under Data/Backtest/ holding the persisted defaults.</summary>
    public const string DefaultFileName = "backtest_default_settings.json";

    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<BacktestDefaultSettingsManager> _logger;

    public BacktestDefaultSettingsManager(
        string? settingsFilePath = null,
        ILogger<BacktestDefaultSettingsManager>? logger = null)
    {
        _settingsFilePath = settingsFilePath ?? PathDiscovery.ResolveBacktestDefaultSettingsPath(DefaultFileName);
        _logger = logger ?? NullLogger<BacktestDefaultSettingsManager>.Instance;
    }

    public async Task<BacktestDefaultSettingsLoadResult> LoadAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            BacktestDefaultSettings? data;
            try
            {
                data = await AtomicJsonFile.LoadAsync<BacktestDefaultSettings?>(_settingsFilePath).ConfigureAwait(false);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogDebug(ex, "Backtest default settings were not found; built-in defaults will be used.");
                return Fallback();
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogDebug(ex, "Backtest default settings directory was not found; built-in defaults will be used.");
                return Fallback();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Backtest default settings JSON is invalid; built-in defaults will be used.");
                return Fallback();
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Backtest default settings schema is unsupported; built-in defaults will be used.");
                return Fallback();
            }

            try
            {
                return new BacktestDefaultSettingsLoadResult(
                    BacktestDefaultSettings.SnapshotValidated(data!),
                    BacktestDefaultSettingsLoadStatus.Loaded);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Backtest default settings values are invalid; built-in defaults will be used.");
                return Fallback();
            }
        }
        finally
        {
            _semaphore.Release();
        }

        static BacktestDefaultSettingsLoadResult Fallback() => new(
            BacktestDefaultSettings.BuiltIn,
            BacktestDefaultSettingsLoadStatus.BuiltInFallback);
    }

    public async Task SaveAsync(BacktestDefaultSettings settings)
    {
        BacktestDefaultSettings snapshot = BacktestDefaultSettings.SnapshotValidated(settings);
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
