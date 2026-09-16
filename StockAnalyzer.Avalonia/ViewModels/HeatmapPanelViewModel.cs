using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class HeatmapPanelViewModel : ViewModelBase, IDisposable, IRecipient<PortfolioSelectedMessage>
{
    private readonly IPortfolioManager _portfolioManager;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<HeatmapPanelViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly List<HeatmapEntry> _entriesBacking = new();
    private CancellationTokenSource? _cts;
    private Portfolio? _currentPortfolio;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private PerformancePeriod _currentPeriod = PerformancePeriod.OneDay;
    [ObservableProperty] private IReadOnlyList<HeatmapEntry> _entries = Array.Empty<HeatmapEntry>();

    public PerformancePeriod[] AvailablePeriods { get; } = Enum.GetValues<PerformancePeriod>();

    public IAsyncRelayCommand<PerformancePeriod> SwitchPeriodCommand { get; }

    public HeatmapPanelViewModel(
        IPortfolioManager portfolioManager,
        IMarketDataProvider marketDataProvider,
        IDispatcherService dispatcherService,
        ILogger<HeatmapPanelViewModel> logger,
        IMessenger messenger)
    {
        _portfolioManager = portfolioManager;
        _marketDataProvider = marketDataProvider;
        _dispatcherService = dispatcherService;
        _logger = logger;
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));

        _messenger.Register<PortfolioSelectedMessage>(this);
        SwitchPeriodCommand = new AsyncRelayCommand<PerformancePeriod>(SwitchPeriodAsync);

        _ = Task.Run(async () =>
        {
            var jitter = Random.Shared.Next(LayoutConstants.HeatmapJitterMinMs, LayoutConstants.HeatmapJitterMaxMs);
            await Task.Delay(jitter);
            await SwitchPeriodAsync(CurrentPeriod);
        });
    }

    public void Receive(PortfolioSelectedMessage message)
    {
        _currentPortfolio = message.Portfolio;
        _ = SwitchPeriodAsync(CurrentPeriod);
    }

    private async Task SwitchPeriodAsync(PerformancePeriod period)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _dispatcherService.Post(static state =>
        {
            var vm = state.Vm;
            vm.CurrentPeriod = state.Period;
            vm.IsLoading = true;
        }, (Vm: this, Period: period));

        try
        {
            var portfolio = _currentPortfolio ?? StockAnalyzer.Core.Factories.PortfolioFactory.CreateDefaultMock();

            var result = await Task.Run(() => 
                _portfolioManager.GetPerformanceHeatmapAsync(portfolio, period, _marketDataProvider, token), token);

            if (token.IsCancellationRequested) return;

            _dispatcherService.Post(static state =>
            {
                var vm = state.Vm;
                var result = state.Result;
                var period = state.Period;
                var portfolio = state.Portfolio;

                // Diff-sync: Clear and update the backing pre-allocated list to prevent allocations
                vm._entriesBacking.Clear();
                vm._entriesBacking.AddRange(result);

                // Notify Avalonia binding
                vm.Entries = vm._entriesBacking;
                vm.IsEmpty = vm.Entries.Count == 0;
                vm.IsLoading = false;

                if (vm.IsEmpty)
                {
                    vm._logger.LogWarning("Heatmap fetch returned 0 entries for period {Period}. Tickers attempted: {Tickers}", 
                        period, string.Join(", ", portfolio.Positions.Keys));
                }
                else
                {
                    vm._logger.LogInformation("Heatmap successfully loaded {Count} entries for period {Period}.", vm.Entries.Count, period);
                }
            }, (Vm: this, Result: result, Period: period, Portfolio: portfolio));
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heatmap fetch failed for period {Period}", period);
            _dispatcherService.Post(static vm => vm.IsLoading = false, this);
        }
    }

    public void Dispose()
    {
        _messenger.Unregister<PortfolioSelectedMessage>(this);
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
