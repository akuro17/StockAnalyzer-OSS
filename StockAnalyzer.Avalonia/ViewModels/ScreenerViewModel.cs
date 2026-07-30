using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core;

using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Models.Watchlist;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ScreenerViewModel : ViewModelBase, IDisposable
{
    private readonly ScreenerService? _screenerService;
    private readonly PatternRecognitionService? _patternService;
    private readonly IStockAnalyzerSettings? _settings;
    private readonly IMarketDataProvider? _marketDataProvider;
    private readonly IWatchlistManager? _watchlistManager;
    private readonly ITickerStateStore? _tickerStateStore;
    private readonly ILogger<ScreenerViewModel> _logger;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _statusResetCts;
    private bool _isDisposed;

    [ObservableProperty]
    private ObservableCollection<ScreenerTargetSource> _targetSources = new();

    [ObservableProperty]
    private ScreenerTargetSource? _selectedTargetSource;

    partial void OnSelectedTargetSourceChanged(ScreenerTargetSource? value)
    {
        _ = UpdateTargetSymbolsAsync(value);
    }

    [ObservableProperty]
    private ObservableCollection<string> _targetSymbols = new();

    [ObservableProperty]
    private ObservableCollection<IScreeningCondition> _availableConditions = new();

    [ObservableProperty]
    private IScreeningCondition? _selectedCondition;

    [ObservableProperty]
    private ObservableCollection<string> _results = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotScanning))]
    private bool _isScanning;

    public bool IsNotScanning => !IsScanning;

    [ObservableProperty]
    private string _statusMessage = "Ready to scan.";

    [ObservableProperty]
    private int _selectedTabIndex;

    public IndicatorRegistrationViewModel IndicatorRegistrationViewModel { get; }

    [RelayCommand]
    private void SwitchToIndicatorTab()
    {
        SelectedTabIndex = 1;
    }

    public ScreenerViewModel(
        ScreenerService screenerService,
        PatternRecognitionService patternService,
        IStockAnalyzerSettings settings,
        IMarketDataProvider? marketDataProvider = null,
        IWatchlistManager? watchlistManager = null,
        ITickerStateStore? tickerStateStore = null,
        IIndicatorFactory? indicatorFactory = null,
        IndicatorRegistrationViewModel? indicatorRegistrationViewModel = null,
        ILogger<ScreenerViewModel>? logger = null)
    {
        _screenerService = screenerService;
        _patternService = patternService;
        _settings = settings;
        _marketDataProvider = marketDataProvider;
        _watchlistManager = watchlistManager;
        _tickerStateStore = tickerStateStore;
        _logger = logger ?? NullLogger<ScreenerViewModel>.Instance;
        IndicatorRegistrationViewModel = indicatorRegistrationViewModel ?? new IndicatorRegistrationViewModel(indicatorFactory);
        Initialize();
    }

    // Default constructor for design-time
    public ScreenerViewModel()
    {
        _logger = NullLogger<ScreenerViewModel>.Instance;
        IndicatorRegistrationViewModel = new IndicatorRegistrationViewModel();
        InitializeFallback();
    }

    private void Initialize()
    {
        if (_watchlistManager != null)
        {
            _watchlistManager.WatchlistsChanged += OnWatchlistsChanged;
        }

        _ = LoadTargetSourcesAsync();

        // Register Conditions
        AvailableConditions.Add(new RsiOversoldCondition(ChartConstants.DefaultRsiPeriod, ChartConstants.DefaultRsiOversoldThreshold));
        
        // Register Divergence & Cross Conditions (RSI based by default for screening)
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBullishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBearishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.GoldenCross));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.DeadCross));
        
        if (_patternService != null)
        {
            // ML-based pattern matching (HeadAndShoulders, DoubleBottom, etc.)
            AvailableConditions.Add(new PatternMatchCondition(_patternService)); // Any pattern
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "HeadAndShoulders"));
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "InverseHeadAndShoulders"));
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "DoubleTop"));
            AvailableConditions.Add(new PatternMatchCondition(_patternService, "DoubleBottom"));
        }

        // Register Geometric formations
        decimal geometricThreshold = _settings?.ZigzagThresholdPercent ?? ChartConstants.DefaultGeometricZigZagThreshold;
        AvailableConditions.Add(new GeometricPatternCondition(GeometricFormationType.BullishFlag, geometricThreshold));
        AvailableConditions.Add(new GeometricPatternCondition(GeometricFormationType.BearishFlag, geometricThreshold));

        // Register all classical candle patterns
        foreach (CandlePatternType patternType in Enum.GetValues(typeof(CandlePatternType)))
        {
            if (patternType != CandlePatternType.None)
            {
                AvailableConditions.Add(new CandlePatternCondition(patternType));
            }
        }

        // Register Market Structure conditions
        foreach (MarketStructureType msType in Enum.GetValues(typeof(MarketStructureType)))
        {
             // Typically we want a threshold > 0, using the default from settings or 5.0m as fallback
             decimal zigzagThreshold = _settings?.ZigzagThresholdPercent ?? 5.0m;
             AvailableConditions.Add(new MarketStructureCondition(msType, zigzagThreshold));
        }

        // Register Harmonic Pattern conditions
        foreach (StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType hpType in Enum.GetValues(typeof(StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType)))
        {
             AvailableConditions.Add(new HarmonicPatternCondition(hpType));
        }

        // Register Granville's Laws conditions
        foreach (GranvilleLawConditionType glType in Enum.GetValues(typeof(GranvilleLawConditionType)))
        {
             AvailableConditions.Add(new GranvilleLawCondition(glType));
        }

        // Register Elliott Wave conditions
        foreach (ElliottWaveConditionType ewType in Enum.GetValues(typeof(ElliottWaveConditionType)))
        {
             AvailableConditions.Add(new ElliottWaveCondition(ewType));
        }

        SelectedCondition = AvailableConditions.FirstOrDefault();
    }

    private void OnWatchlistsChanged(object? sender, EventArgs e)
    {
        _ = LoadTargetSourcesAsync();
    }

    public async Task LoadTargetSourcesAsync()
    {
        var sources = new List<ScreenerTargetSource>();

        if (_tickerStateStore != null)
        {
            // Mirror the Groups tree from ITickerStateStore directly
            foreach (var groupNode in _tickerStateStore.Groups)
            {
                AppendNodeAsSource(sources, groupNode, indent: "");
            }
        }
        else if (_watchlistManager != null)
        {
            // Fallback when TickerListViewModel is unavailable
            string allTickersLabel = LocalizationManager.Instance["TargetSource_AllTickers"] ?? "All Tickers";
            sources.Add(new ScreenerTargetSource(TargetSourceType.AllTickers, allTickersLabel));

            var profiles = _watchlistManager.GetAllProfiles();
            var watchlists = profiles.Where(p => !p.IsPortfolio).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (watchlists.Count > 0)
            {
                string wlHeader = LocalizationManager.Instance["TargetSource_WatchlistHeader"] ?? "Watchlists";
                sources.Add(new ScreenerTargetSource(TargetSourceType.Watchlist, wlHeader, isHeader: true));
                foreach (var p in watchlists)
                    sources.Add(new ScreenerTargetSource(TargetSourceType.Watchlist, $"   {p.Name}", profileId: p.Id));
            }
            var portfolios = profiles.Where(p => p.IsPortfolio).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (portfolios.Count > 0)
            {
                string pHeader = LocalizationManager.Instance["TargetSource_PortfolioHeader"] ?? "Portfolios";
                sources.Add(new ScreenerTargetSource(TargetSourceType.Portfolio, pHeader, isHeader: true));
                foreach (var p in portfolios)
                    sources.Add(new ScreenerTargetSource(TargetSourceType.Portfolio, $"   {p.Name}", profileId: p.Id));
            }
        }
        else
        {
            string allTickersLabel = LocalizationManager.Instance["TargetSource_AllTickers"] ?? "All Tickers";
            sources.Add(new ScreenerTargetSource(TargetSourceType.AllTickers, allTickersLabel));
        }

        // Load unique metadata tags from MarketDataProvider and append as TagFilter sub-items under All Tickers
        if (_marketDataProvider != null)
        {
            try
            {
                var allCandidateTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var masterTickers = await _marketDataProvider.GetAvailableTickersAsync();
                foreach (var t in masterTickers) allCandidateTickers.Add(t);
                if (_watchlistManager != null)
                {
                    foreach (var profile in _watchlistManager.GetAllProfiles())
                    {
                        foreach (var item in profile.Items) allCandidateTickers.Add(item.Ticker);
                    }
                }

                var uniqueTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ticker in allCandidateTickers)
                {
                    var meta = await _marketDataProvider.GetMetadataAsync(ticker);
                    if (!string.IsNullOrEmpty(meta.Tag))
                    {
                        var parts = meta.Tag.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            var trimmed = part.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                uniqueTags.Add(trimmed);
                            }
                        }
                    }
                }

                var allTickersIndex = sources.FindIndex(s => s.Type == TargetSourceType.AllTickers);
                int insertIndex = allTickersIndex >= 0 ? allTickersIndex + 1 : sources.Count;
                while (insertIndex < sources.Count && sources[insertIndex].DisplayName.StartsWith("   ") && !sources[insertIndex].IsHeader && sources[insertIndex].Type != TargetSourceType.Watchlist && sources[insertIndex].Type != TargetSourceType.Portfolio)
                {
                    insertIndex++;
                }

                foreach (var tag in uniqueTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
                {
                    if (!sources.Any(s => s.Type == TargetSourceType.TagFilter && string.Equals(s.TagName, tag, StringComparison.OrdinalIgnoreCase)))
                    {
                        sources.Insert(insertIndex++, new ScreenerTargetSource(TargetSourceType.TagFilter, $"   {tag}", tagName: tag));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load metadata tags for Screener target sources.");
            }
        }

        var currentSelected = SelectedTargetSource;
        TargetSources.Clear();
        foreach (var s in sources)
            TargetSources.Add(s);

        if (currentSelected != null && !currentSelected.IsHeader)
        {
            var match = TargetSources.FirstOrDefault(s =>
                !s.IsHeader &&
                s.Type == currentSelected.Type &&
                s.ProfileId == currentSelected.ProfileId &&
                string.Equals(s.TagName, currentSelected.TagName, StringComparison.OrdinalIgnoreCase) &&
                s.FilterSettings?.Id == currentSelected.FilterSettings?.Id);
            SelectedTargetSource = match ?? TargetSources.FirstOrDefault(s => !s.IsHeader);
        }
        else
        {
            SelectedTargetSource = TargetSources.FirstOrDefault(s => !s.IsHeader);
        }
    }

    /// <summary>
    /// Recursively maps a TickerGroupNode to ScreenerTargetSource entries,
    /// mirroring the Tickers tab tree structure.
    /// </summary>
    private void AppendNodeAsSource(List<ScreenerTargetSource> sources, TickerGroupNode node, string indent)
    {
        if (node is AllTickersNode)
        {
            string label = LocalizationManager.Instance["TargetSource_AllTickers"] ?? "All Tickers";
            sources.Add(new ScreenerTargetSource(TargetSourceType.AllTickers, $"{indent}{label}"));
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    AppendNodeAsSource(sources, child, indent + "   ");
            }
        }
        else if (node is CategoryNode)
        {
            sources.Add(new ScreenerTargetSource(
                node.Id == TickerListViewModel.WatchlistsCategoryId ? TargetSourceType.Watchlist : TargetSourceType.Portfolio,
                $"{indent}{node.DisplayName}",
                isHeader: false));
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    AppendNodeAsSource(sources, child, indent + "   ");
            }
        }
        else if (node is WatchlistNode wlNode)
        {
            sources.Add(new ScreenerTargetSource(TargetSourceType.Watchlist, $"{indent}{node.DisplayName}", profileId: wlNode.Profile.Id));
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    AppendNodeAsSource(sources, child, indent + "   ");
            }
        }
        else if (node is PortfolioNode pNode)
        {
            sources.Add(new ScreenerTargetSource(TargetSourceType.Portfolio, $"{indent}{node.DisplayName}", profileId: pNode.Profile.Id));
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    AppendNodeAsSource(sources, child, indent + "   ");
            }
        }
        else if (node is FilterNode filterNode)
        {
            sources.Add(new ScreenerTargetSource(TargetSourceType.SavedFilter, $"{indent}{node.DisplayName}", profileId: filterNode.Settings.ParentId, filterSettings: filterNode.Settings));
        }
    }

    public async Task UpdateTargetSymbolsAsync(ScreenerTargetSource? source)
    {
        TargetSymbols.Clear();
        if (source == null) return;

        switch (source.Type)
        {
            case TargetSourceType.AllTickers:
                if (_marketDataProvider != null)
                {
                    var tickers = await _marketDataProvider.GetAvailableTickersAsync();
                    foreach (var ticker in tickers)
                    {
                        TargetSymbols.Add(ticker);
                    }
                }
                else if (_settings?.DefaultScreenerSymbols != null)
                {
                    foreach (var symbol in _settings.DefaultScreenerSymbols)
                    {
                        TargetSymbols.Add(symbol);
                    }
                }
                break;

            case TargetSourceType.Watchlist:
            case TargetSourceType.Portfolio:
                if (_watchlistManager != null)
                {
                    if (source.ProfileId.HasValue && source.ProfileId.Value != Guid.Empty)
                    {
                        // Specific profile selection
                        var profile = _watchlistManager.GetProfileById(source.ProfileId.Value);
                        if (profile != null)
                        {
                            foreach (var item in profile.Items)
                            {
                                TargetSymbols.Add(item.Ticker);
                            }
                        }
                    }
                    else
                    {
                        // Parent category selection ("Watchlists" or "Portfolios")
                        var isPortfolioCategory = (source.Type == TargetSourceType.Portfolio);
                        var profiles = _watchlistManager.GetAllProfiles().Where(p => p.IsPortfolio == isPortfolioCategory);
                        var categoryTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var profile in profiles)
                        {
                            foreach (var item in profile.Items)
                            {
                                categoryTickers.Add(item.Ticker);
                            }
                        }
                        foreach (var ticker in categoryTickers)
                        {
                            TargetSymbols.Add(ticker);
                        }
                    }
                }
                break;

            case TargetSourceType.SavedFilter:
                if (source.FilterSettings != null && _marketDataProvider != null)
                {
                    var filterEngine = new WatchlistFilterEngine();
                    var candidateTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    WatchlistProfile? parentProfile = null;
                    if (source.ProfileId.HasValue && source.ProfileId.Value != Guid.Empty && source.ProfileId.Value != TickerListViewModel.AllTickersId && _watchlistManager != null)
                    {
                        parentProfile = _watchlistManager.GetProfileById(source.ProfileId.Value);
                    }
                    else if (source.FilterSettings.ParentId != Guid.Empty && source.FilterSettings.ParentId != TickerListViewModel.AllTickersId && _watchlistManager != null)
                    {
                        parentProfile = _watchlistManager.GetProfileById(source.FilterSettings.ParentId);
                    }

                    if (parentProfile != null)
                    {
                        foreach (var item in parentProfile.Items) candidateTickers.Add(item.Ticker);
                    }
                    else
                    {
                        var masterTickers = await _marketDataProvider.GetAvailableTickersAsync();
                        foreach (var t in masterTickers) candidateTickers.Add(t);
                        if (_watchlistManager != null)
                        {
                            foreach (var profile in _watchlistManager.GetAllProfiles())
                            {
                                foreach (var item in profile.Items) candidateTickers.Add(item.Ticker);
                            }
                        }
                        if (_tickerStateStore != null)
                        {
                            foreach (var item in _tickerStateStore.DisplayItems) candidateTickers.Add(item.Symbol);
                        }
                    }

                    IReadOnlyDictionary<string, decimal>? latestPrices = null;
                    try
                    {
                        latestPrices = await _marketDataProvider.GetLatestPricesAsync(candidateTickers);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to load latest prices for candidate tickers in Screener filter evaluation.");
                    }

                    foreach (var ticker in candidateTickers)
                    {
                        var meta = await _marketDataProvider.GetMetadataAsync(ticker);
                        var existingVm = _tickerStateStore?.DisplayItems.FirstOrDefault(x => string.Equals(x.Symbol, ticker, StringComparison.OrdinalIgnoreCase));
                        var liveTag = existingVm?.Tag;
                        var finalTag = !string.IsNullOrEmpty(liveTag) ? liveTag : meta.Tag;

                        IFilterableSymbol itemVm;
                        if (existingVm != null)
                        {
                            itemVm = existingVm;
                        }
                        else
                        {
                            itemVm = new WatchlistItemViewModel(ticker, meta.ShortName ?? ticker, meta.Sector ?? "", meta.Industry ?? "", 0, 0, 0, 0, 0, 0, 0)
                            {
                                Tag = finalTag,
                                Sector = meta.Sector ?? "",
                                Industry = meta.Industry ?? "",
                                ReturnOnEquity = meta.ReturnOnEquity,
                                ReturnOnAssets = meta.ReturnOnAssets,
                                GrossMargins = meta.GrossMargins,
                                OperatingMargins = meta.OperatingMargins,
                                ProfitMargins = meta.ProfitMargins,
                                CurrentRatio = meta.CurrentRatio,
                                QuickRatio = meta.QuickRatio,
                                DebtToEquity = meta.DebtToEquity,
                                MarketCap = meta.MarketCap,
                                EnterpriseValue = meta.EnterpriseValue,
                                TrailingPE = meta.TrailingPE,
                                ForwardPE = meta.ForwardPE,
                                PegRatio = meta.PegRatio,
                                PriceToSalesTrailing12Months = meta.PriceToSalesTrailing12Months,
                                PriceToBook = meta.PriceToBook
                            };
                            if (latestPrices != null && latestPrices.TryGetValue(ticker, out var price) && price > 0)
                            {
                                if (itemVm is WatchlistItemViewModel wlVm)
                                {
                                    wlVm.Close = price;
                                }
                            }
                        }

                        if (filterEngine.EvaluateSettings(itemVm, source.FilterSettings))
                        {
                            TargetSymbols.Add(ticker);
                        }
                    }
                }
                break;

            case TargetSourceType.TagFilter:
                if (!string.IsNullOrEmpty(source.TagName) && _marketDataProvider != null)
                {
                    var searchTag = source.TagName.Trim();
                    var candidateTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var masterTickers = await _marketDataProvider.GetAvailableTickersAsync();
                    foreach (var t in masterTickers) candidateTickers.Add(t);
                    if (_watchlistManager != null)
                    {
                        foreach (var profile in _watchlistManager.GetAllProfiles())
                        {
                            foreach (var item in profile.Items) candidateTickers.Add(item.Ticker);
                        }
                    }
                    if (_tickerStateStore != null)
                    {
                        foreach (var item in _tickerStateStore.DisplayItems) candidateTickers.Add(item.Symbol);
                    }

                    foreach (var ticker in candidateTickers)
                    {
                        var meta = await _marketDataProvider.GetMetadataAsync(ticker);
                        var liveTag = _tickerStateStore?.DisplayItems.FirstOrDefault(x => string.Equals(x.Symbol, ticker, StringComparison.OrdinalIgnoreCase))?.Tag;
                        var finalTag = !string.IsNullOrEmpty(liveTag) ? liveTag : meta.Tag;

                        if (!string.IsNullOrEmpty(finalTag) && WatchlistFilterEngine.ContainsTag(finalTag, searchTag))
                        {
                            TargetSymbols.Add(ticker);
                        }
                    }
                }
                break;
        }
    }

    private void InitializeFallback()
    {
        TargetSources.Add(new ScreenerTargetSource(TargetSourceType.AllTickers, "All Tickers"));
        SelectedTargetSource = TargetSources.FirstOrDefault();

        AvailableConditions.Add(new RsiOversoldCondition(ChartConstants.DefaultRsiPeriod, ChartConstants.DefaultRsiOversoldThreshold));
        
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBullishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.RegularBearishDivergence));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.GoldenCross));
        AvailableConditions.Add(new DivergenceCrossCondition(IndicatorType.RSI, SignalType.DeadCross));
        
        foreach (CandlePatternType patternType in Enum.GetValues(typeof(CandlePatternType)))
        {
            if (patternType != CandlePatternType.None)
            {
                AvailableConditions.Add(new CandlePatternCondition(patternType));
            }
        }

        foreach (MarketStructureType msType in Enum.GetValues(typeof(MarketStructureType)))
        {
             AvailableConditions.Add(new MarketStructureCondition(msType, 5.0m));
        }

        foreach (StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType hpType in Enum.GetValues(typeof(StockAnalyzer.Core.Models.HarmonicPattern.HarmonicPatternType)))
        {
             AvailableConditions.Add(new HarmonicPatternCondition(hpType));
        }

        foreach (GranvilleLawConditionType glType in Enum.GetValues(typeof(GranvilleLawConditionType)))
        {
             AvailableConditions.Add(new GranvilleLawCondition(glType));
        }

        // Register Elliott Wave conditions (fallback)
        foreach (ElliottWaveConditionType ewType in Enum.GetValues(typeof(ElliottWaveConditionType)))
        {
              AvailableConditions.Add(new ElliottWaveCondition(ewType));
        }

        SelectedCondition = AvailableConditions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_isDisposed || IsScanning) return;
        if (SelectedCondition == null)
        {
            StatusMessage = "Please select a screening condition.";
            _ = ScheduleStatusResetAsync();
            return;
        }

        if (_screenerService == null)
        {
             StatusMessage = "Screener service not initialized.";
             _ = ScheduleStatusResetAsync();
             return;
        }

        _statusResetCts?.Cancel();
        IsScanning = true;
        StatusMessage = "Scanning...";
        Results.Clear();
        _cts = new CancellationTokenSource();

        void UpdateProgress(int percent)
        {
            StatusMessage = $"Scanning... {percent}%";
        }

        var progress = new Progress<int>(UpdateProgress);

        try
        {

            // Use the service
            // Note: ScreenerService.ScreenAsync now takes IScreeningCondition
            var hits = await _screenerService.ScreenAsync(
                TargetSymbols.ToList(),
                SelectedCondition,
                TimeFrame.D1,
                progress,
                _cts.Token
            );

            foreach (var symbol in hits)
            {
                Results.Add(symbol);
            }

            StatusMessage = hits.Count > 0 
                ? $"Scan complete. Found {hits.Count} matches." 
                : "Scan complete. No matches found.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan canceled.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error occurred during screener scan execution.");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
            _ = ScheduleStatusResetAsync();
        }
    }

    private async Task ScheduleStatusResetAsync()
    {
        _statusResetCts?.Cancel();
        _statusResetCts = new CancellationTokenSource();
        var token = _statusResetCts.Token;

        try
        {
            await Task.Delay(5000, token);
            if (!token.IsCancellationRequested && !IsScanning)
            {
                StatusMessage = "Ready to scan.";
            }
        }
        catch (TaskCanceledException)
        {
            // Ignored, cancelled by another scan starting or component disposing
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        if (_isDisposed) return;
        _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_watchlistManager != null)
        {
            _watchlistManager.WatchlistsChanged -= OnWatchlistsChanged;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _statusResetCts?.Cancel();
        _statusResetCts?.Dispose();
        _statusResetCts = null;

        IndicatorRegistrationViewModel?.Dispose();

        GC.SuppressFinalize(this);
    }
}
