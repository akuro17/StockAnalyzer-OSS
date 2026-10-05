using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

/// <summary>
/// Loads and saves <see cref="BacktestColumnSettings"/> under Data/Backtest/ with the
/// PathDiscovery + AtomicJsonFile convention.
/// </summary>
public sealed class BacktestColumnSettingsManager : IBacktestColumnSettingsManager
{
    public const string DefaultFileName = "backtest_column_settings.json";

    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<BacktestColumnSettingsManager> _logger;

    public BacktestColumnSettingsManager(
        string? settingsFilePath = null,
        ILogger<BacktestColumnSettingsManager>? logger = null)
    {
        _settingsFilePath = settingsFilePath ?? PathDiscovery.ResolveBacktestDefaultSettingsPath(DefaultFileName);
        _logger = logger ?? NullLogger<BacktestColumnSettingsManager>.Instance;
    }

    public async Task<BacktestColumnSettings?> LoadAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return null;
            }

            try
            {
                return await AtomicJsonFile.LoadAsync<BacktestColumnSettings>(_settingsFilePath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load backtest column settings from {Path}; defaults will be used.", _settingsFilePath);
                return null;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public BacktestColumnSettings? Load()
    {
        _semaphore.Wait();
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                return JsonSerializer.Deserialize<BacktestColumnSettings>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load backtest column settings synchronously from {Path}; defaults will be used.", _settingsFilePath);
                return null;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveAsync(BacktestColumnSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.SaveAsync(_settingsFilePath, settings, new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save backtest column settings to {Path}", _settingsFilePath);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveColumnsAsync(IEnumerable<string>? tradeColumns, IEnumerable<string>? metricNames)
    {
        var existing = await LoadAsync().ConfigureAwait(false) ?? new BacktestColumnSettings();
        var updated = new BacktestColumnSettings
        {
            SchemaVersion = BacktestColumnSettings.CurrentSchemaVersion,
            TradeVisibleColumns = tradeColumns != null ? tradeColumns.ToList() : existing.TradeVisibleColumns,
            MetricVisibleNames = metricNames != null ? metricNames.ToList() : existing.MetricVisibleNames
        };
        await SaveAsync(updated).ConfigureAwait(false);
    }
}
