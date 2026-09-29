using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Avalonia.Services;

public sealed record BacktestSettingsLoad(
    BacktestConfigurationLoadResult Configuration,
    BacktestReportSettingsLoadResult ReportSettings);

public sealed record BacktestSettingsSave(bool ConfigurationSaved, bool ReportDefaultsSaved, Exception? Failure)
{
    public bool IsComplete => ConfigurationSaved && ReportDefaultsSaved;
}

public interface IBacktestSettingsCoordinator
{
    Task<BacktestReportDefaults> LoadReportDefaultsAsync();

    /// <summary>Same load as <see cref="LoadReportDefaultsAsync"/> but reports whether the values came from a saved file or the built-in fallback. Default implementation treats every result as loaded, so existing implementations are unaffected.</summary>
    async Task<BacktestReportSettingsLoadResult> LoadReportSettingsAsync() =>
        new(await LoadReportDefaultsAsync().ConfigureAwait(false), BacktestReportSettingsLoadStatus.Loaded);

    Task<BacktestSettingsLoad> LoadAsync();
    Task<BacktestSettingsSave> SaveAsync(BacktestConfigurationDto configuration, BacktestReportDefaults reportDefaults);
}

/// <summary>Serializes the two-file Backtest settings workflow without claiming cross-file atomicity.</summary>
public sealed class BacktestSettingsCoordinator : IBacktestSettingsCoordinator
{
    private readonly IBacktestConfigurationManager _configurationManager;
    private readonly IBacktestReportSettingsManager _reportSettingsManager;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public BacktestSettingsCoordinator(
        IBacktestConfigurationManager configurationManager,
        IBacktestReportSettingsManager reportSettingsManager)
    {
        _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
        _reportSettingsManager = reportSettingsManager ?? throw new ArgumentNullException(nameof(reportSettingsManager));
    }

    public async Task<BacktestReportDefaults> LoadReportDefaultsAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _reportSettingsManager.LoadAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<BacktestReportSettingsLoadResult> LoadReportSettingsAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _reportSettingsManager.LoadWithStatusAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<BacktestSettingsLoad> LoadAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            BacktestConfigurationLoadResult configuration = await _configurationManager.LoadAsync().ConfigureAwait(false);
            BacktestReportSettingsLoadResult reportSettings = await _reportSettingsManager.LoadWithStatusAsync().ConfigureAwait(false);
            return new BacktestSettingsLoad(configuration, reportSettings);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<BacktestSettingsSave> SaveAsync(
        BacktestConfigurationDto configuration,
        BacktestReportDefaults reportDefaults)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        BacktestConfigurationDto configurationSnapshot = BacktestConfigurationSnapshot.Create(configuration);
        BacktestReportDefaults reportSnapshot = BacktestReportDefaults.SnapshotValidated(reportDefaults);

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await _configurationManager.SaveAsync(configurationSnapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new BacktestSettingsSave(false, false, ex);
            }

            try
            {
                await _reportSettingsManager.SaveAsync(reportSnapshot).ConfigureAwait(false);
                return new BacktestSettingsSave(true, true, null);
            }
            catch (Exception ex)
            {
                return new BacktestSettingsSave(true, false, ex);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
