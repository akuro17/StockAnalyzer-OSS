using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <inheritdoc cref="ITickerSyncService"/>
public class TickerSyncService : ITickerSyncService
{
    private readonly IPythonService _pythonService;
    private readonly IDialogService _dialogService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<TickerSyncService> _logger;

    public TickerSyncService(
        IPythonService pythonService,
        IDialogService dialogService,
        IDispatcherService dispatcherService,
        ILogger<TickerSyncService>? logger = null)
    {
        _pythonService = pythonService;
        _dialogService = dialogService;
        _dispatcherService = dispatcherService;
        _logger = logger ?? NullLogger<TickerSyncService>.Instance;
    }

    public Task SyncSingleTickerAsync(string symbol, Func<Task>? onSyncedAsync = null)
    {
        if (string.IsNullOrEmpty(symbol)) return Task.CompletedTask;

        var syncSession = _dialogService.CreateMultiSyncProgressSession();
        var vm = syncSession.ViewModel;

        var model = new SyncItemModel(symbol, "D1");
        var itemVm = new SyncItemViewModel(model);

        var sessionCts = new CancellationTokenSource();

        var progress = new Progress<int>(value =>
        {
            model.UpdateProgress(value, $"Syncing {value}%");
            _dispatcherService.Post(static i => i.UpdateFromModel(), itemVm);
        });
        itemVm.ProgressReporter = progress;

        itemVm.RequestRetry += async (ivm, retryCt) =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token, retryCt);
            try
            {
                model.MarkSyncing();
                _dispatcherService.Post(static i => i.UpdateFromModel(), ivm);

                var currentConfig = new SyncSessionConfig(
                    IsTimeSeriesSyncEnabled: vm.IsTimeSeriesSyncEnabled,
                    IsMetadataSyncEnabled: vm.IsMetadataSyncEnabled,
                    IsAutoSyncEnabled: vm.IsAutoSyncEnabled,
                    IsFullHistoryEnabled: vm.IsFullHistoryEnabled,
                    DelayMinSeconds: vm.DelayMinSeconds,
                    DelayMaxSeconds: vm.DelayMaxSeconds,
                    StartSyncPeriodYears: vm.StartSyncPeriodYears,
                    IsForcePeriodDownloadEnabled: vm.IsForcePeriodDownloadEnabled
                );
                await _pythonService.RunUpdatePipelineAsync(symbol, currentConfig, ivm.ProgressReporter, linkedCts.Token);

                model.MarkComplete();
                _dispatcherService.Post(static i => i.UpdateFromModel(), ivm);

                if (onSyncedAsync != null)
                {
                    await _dispatcherService.PostAsync(static callback => callback(), onSyncedAsync);
                }
            }
            catch (Exception ex)
            {
                if (linkedCts.Token.IsCancellationRequested)
                {
                    model.MarkCanceled();
                }
                else
                {
                    _logger.LogError(ex, "Sync failed for {Symbol}", symbol);
                    model.MarkError(ex.Message);
                }
                _dispatcherService.Post(static i => i.UpdateFromModel(), ivm);
            }
        };

        vm.AddItem(itemVm);

        Action stopHandler = () => { try { sessionCts.Cancel(); } catch { } };
        Action closeHandler = () => { try { sessionCts.Cancel(); } catch { } };
        Action startHandler = () =>
        {
            if (sessionCts.IsCancellationRequested)
            {
                sessionCts.Dispose();
                sessionCts = new CancellationTokenSource();
            }
            if (itemVm.Status != SyncStatus.Completed)
            {
                itemVm.MarkWaiting();
            }
            _ = Task.Run(async () =>
            {
                await _dispatcherService.PostAsync(
                    static state => state.cmd.ExecuteAsync(state.token),
                    (cmd: itemVm.RetryCommand, token: sessionCts.Token)
                );
            }, sessionCts.Token);
        };

        vm.RequestStopAll += stopHandler;
        vm.RequestClose += closeHandler;
        vm.RequestStartAll += startHandler;

        syncSession.Show(_dialogService.GetMainWindowOwner());

        if (vm.IsAutoSyncEnabled)
        {
            startHandler();
        }

        vm.RequestClose += () =>
        {
            vm.RequestStopAll -= stopHandler;
            vm.RequestClose -= closeHandler;
            vm.RequestStartAll -= startHandler;
            try { sessionCts.Cancel(); } catch { }
            sessionCts.Dispose();
        };

        return Task.CompletedTask;
    }
}
