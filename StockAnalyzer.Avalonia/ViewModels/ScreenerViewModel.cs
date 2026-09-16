using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
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
    private readonly IScreenerService? _screenerService;
    private readonly IPatternRecognitionService? _patternService;
    private readonly IStockAnalyzerSettings? _settings;
    private readonly IMarketDataProvider? _marketDataProvider;
    private readonly IWatchlistManager? _watchlistManager;
    private readonly ITickerStateStore? _tickerStateStore;
    private readonly IDialogService? _dialogService;
    private readonly IMessenger? _messenger;
    private readonly IScreenerValueExtractor _valueExtractor;
    private readonly IReadOnlyList<WatchlistColumnMetadata> _allColumns = WatchlistColumnRegistry.AllColumns;
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
    private ObservableCollection<WatchlistItemViewModel> _resultItems = new();

    [ObservableProperty]
    private ObservableCollection<WatchlistColumnMetadata> _activeColumns = new();

    [ObservableProperty]
    private WatchlistItemViewModel? _selectedItem;

    partial void OnSelectedItemChanged(WatchlistItemViewModel? value)
    {
        if (value != null && !string.IsNullOrEmpty(value.Symbol) && _messenger != null)
        {
            _messenger.Send(new TickerSelectedMessage(value.Symbol));
        }
    }

    [ObservableProperty]
    private ObservableCollection<CriteriaFlowItemViewModel> _criteriaFlowItems = new();

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

    [RelayCommand]
    private async Task ShowColumnChooser()
    {
        if (_dialogService == null) return;
        var activeFixed = ActiveColumns.Where(c => !c.MemberName.StartsWith("Indicator_")).Select(c => c.MemberName).ToList();
        var result = await _dialogService.ShowColumnChooserDialogAsync(_allColumns, activeFixed, cols => SetActiveColumns(cols));
        if (result != null)
        {
            SetActiveColumns(result);
        }
    }

    public void SetActiveColumns(IEnumerable<string> columnNames)
    {
        var names = columnNames.ToList();
        if (names.Count == 0) return;

        if (!names.Contains("Symbol", StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, "Symbol");
        }

        var fixedCols = new List<WatchlistColumnMetadata>();
        foreach (var name in names)
        {
            var col = _allColumns.FirstOrDefault(c => c.MemberName == name);
            if (col != null) fixedCols.Add(col);
        }

        var indicatorCols = ActiveColumns.Where(c => c.MemberName.StartsWith("Indicator_")).ToList();
        ActiveColumns.Clear();
        foreach (var col in fixedCols) ActiveColumns.Add(col);
        foreach (var col in indicatorCols) ActiveColumns.Add(col);
    }

    private void InitializeColumns()
    {
        ActiveColumns.Clear();
        var defaultCols = new[] { "Symbol", "Name", "Close", "ChangePercent", "Volume" };
        foreach (var name in defaultCols)
        {
            var col = _allColumns.FirstOrDefault(c => c.MemberName == name);
            if (col != null) ActiveColumns.Add(col);
        }
    }

    public ScreenerViewModel(
        IScreenerService? screenerService = null,
        IPatternRecognitionService? patternService = null,
        IStockAnalyzerSettings? settings = null,
        IMarketDataProvider? marketDataProvider = null,
        IWatchlistManager? watchlistManager = null,
        ITickerStateStore? tickerStateStore = null,
        IIndicatorFactory? indicatorFactory = null,
        IndicatorRegistrationViewModel? indicatorRegistrationViewModel = null,
        IDialogService? dialogService = null,
        IMessenger? messenger = null,
        IScreenerValueExtractor? valueExtractor = null,
        ILogger<ScreenerViewModel>? logger = null)
    {
        _screenerService = screenerService;
        _patternService = patternService;
        _settings = settings;
        _marketDataProvider = marketDataProvider;
        _watchlistManager = watchlistManager;
        _tickerStateStore = tickerStateStore;
        _dialogService = dialogService;
        _messenger = messenger;
        _valueExtractor = valueExtractor ?? ScreenerValueExtractor.Default;
        _logger = logger ?? NullLogger<ScreenerViewModel>.Instance;
        IndicatorRegistrationViewModel = indicatorRegistrationViewModel ?? new IndicatorRegistrationViewModel(indicatorFactory);
        Initialize();
    }

    // Default constructor for design-time
    public ScreenerViewModel()
    {
        _valueExtractor = ScreenerValueExtractor.Default;
        _logger = NullLogger<ScreenerViewModel>.Instance;
        IndicatorRegistrationViewModel = new IndicatorRegistrationViewModel();
        InitializeFallback();
    }

    private void Initialize()
    {
        InitializeColumns();

        if (_watchlistManager != null)
        {
            _watchlistManager.WatchlistsChanged += OnWatchlistsChanged;
        }

        _ = LoadTargetSourcesAsync();

        if (IndicatorRegistrationViewModel != null)
        {
            IndicatorRegistrationViewModel.RegisteredEntries.CollectionChanged += (s, e) => UpdateCriteriaFlowItems();
            UpdateCriteriaFlowItems();
        }

        // Register Conditions
        AvailableConditions.Add(new RsiOversoldCondition(StockAnalyzer.Core.ChartConstants.DefaultRsiPeriod, StockAnalyzer.Core.ChartConstants.DefaultRsiOversoldThreshold));
        
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
        decimal geometricThreshold = _settings?.ZigzagThresholdPercent ?? StockAnalyzer.Core.ChartConstants.DefaultGeometricZigZagThreshold;
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
        if (IndicatorRegistrationViewModel != null)
        {
            IndicatorRegistrationViewModel.RegisteredEntries.CollectionChanged += (s, e) => UpdateCriteriaFlowItems();
            UpdateCriteriaFlowItems();
        }

        TargetSources.Add(new ScreenerTargetSource(TargetSourceType.AllTickers, "All Tickers"));
        SelectedTargetSource = TargetSources.FirstOrDefault();

        AvailableConditions.Add(new RsiOversoldCondition(StockAnalyzer.Core.ChartConstants.DefaultRsiPeriod, StockAnalyzer.Core.ChartConstants.DefaultRsiOversoldThreshold));
        
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

        var registeredEntries = IndicatorRegistrationViewModel?.RegisteredEntries;
        var activeEntries = registeredEntries?.Where(e => e.IsEnabled).ToList();
        if (activeEntries == null || activeEntries.Count == 0)
        {
            StatusMessage = LocalizationManager.Instance["Screener_NoConditionsRegistered"] ?? "Please register at least one screening condition.";
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
        ResultItems.Clear();
        _cts = new CancellationTokenSource();

        void UpdateProgress(int percent)
        {
            StatusMessage = $"Scanning... {percent}%";

            var liveEntries = IndicatorRegistrationViewModel?.RegisteredEntries;
            if (liveEntries != null && liveEntries.Count > 0)
            {
                var liveCounts = new Dictionary<int, int>();
                int totalCount = TargetSymbols.Count;
                int currentProcessed = totalCount > 0 ? (int)(totalCount * (percent / 100.0)) : percent;
                for (int i = 0; i < liveEntries.Count; i++)
                {
                    if (liveEntries[i].IsEnabled)
                    {
                        liveCounts[i] = currentProcessed;
                    }
                }
                UpdateCriteriaFlowItems(liveCounts);
            }
        }

        var progress = new Progress<int>(UpdateProgress);

        try
        {
            List<string> hits = new();
            var counts = new Dictionary<int, int>();
            HashSet<string>? combinedHits = null;

            for (int i = 0; i < activeEntries.Count; i++)
            {
                var entry = activeEntries[i];

                // Screen target symbols for the current entry
                var entryHits = await _screenerService.ScreenAsync(
                    TargetSymbols.ToList(),
                    entry,
                    TimeFrame.D1,
                    null!,
                    _cts.Token
                );

                var hitSet = new HashSet<string>(entryHits);

                if (combinedHits == null)
                {
                    combinedHits = hitSet;
                }
                else
                {
                    var prevOp = activeEntries[i - 1].LogicalOperator;

                    if (prevOp == LogicalOperator.Or)
                    {
                        combinedHits.UnionWith(hitSet);
                    }
                    else
                    {
                        combinedHits.IntersectWith(hitSet);
                    }
                }

                int originalIndex = registeredEntries!.IndexOf(entry);
                if (originalIndex >= 0)
                {
                    counts[originalIndex] = combinedHits.Count;
                }

                int currentPercent = (int)(((double)(i + 1) / activeEntries.Count) * 100);
                UpdateProgress(currentPercent);
            }

            hits = combinedHits?.ToList() ?? new List<string>();

            // Rebuild dynamic indicator columns on ActiveColumns
            var fixedCols = ActiveColumns.Where(c => !c.MemberName.StartsWith("Indicator_")).ToList();
            ActiveColumns.Clear();
            foreach (var col in fixedCols) ActiveColumns.Add(col);

            var sideConfigsForColumns = new List<(ScreenerIndicatorSideConfig Config, ScreenerItemCategoryType Category)>();
            var activeEntriesForColumns = IndicatorRegistrationViewModel?.RegisteredEntries?.Where(e => e.IsEnabled).ToList();
            if (activeEntriesForColumns != null)
            {
                var seenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in activeEntriesForColumns)
                {
                    if (entry.LeftHand != null)
                    {
                        string header = entry.LeftHand.GetColumnHeaderTitle();
                        if (seenHeaders.Add(header))
                        {
                            sideConfigsForColumns.Add((entry.LeftHand, entry.CategoryType));
                        }
                    }

                    if (entry.TargetMode == RightHandTargetMode.Indicator && entry.RightHand != null)
                    {
                        string header = entry.RightHand.GetColumnHeaderTitle();
                        if (seenHeaders.Add(header))
                        {
                            sideConfigsForColumns.Add((entry.RightHand, ScreenerItemCategoryType.Indicator));
                        }
                    }
                }

                for (int idx = 0; idx < sideConfigsForColumns.Count; idx++)
                {
                    var (config, category) = sideConfigsForColumns[idx];
                    string headerTitle = config.GetColumnHeaderTitle();
                    string memberKey = $"Indicator_{idx}_{headerTitle}";
                    ActiveColumns.Add(new WatchlistColumnMetadata(headerTitle, memberKey, "90", 100 + idx));
                }
            }

            foreach (var symbol in hits)
            {
                Results.Add(symbol);

                WatchlistItemViewModel item;
                var existingVm = _tickerStateStore?.DisplayItems.OfType<WatchlistItemViewModel>().FirstOrDefault(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                if (existingVm != null)
                {
                    item = new WatchlistItemViewModel(
                        existingVm.Symbol,
                        existingVm.Name,
                        existingVm.Sector,
                        existingVm.Industry,
                        existingVm.Open,
                        existingVm.High,
                        existingVm.Low,
                        existingVm.Close,
                        existingVm.Volume,
                        existingVm.ChangePercent,
                        existingVm.Change)
                    {
                        Tag = existingVm.Tag
                    };
                }
                else
                {
                    item = new WatchlistItemViewModel(symbol, symbol, "", "", 0, 0, 0, 0, 0, 0, 0);
                }

                if (_marketDataProvider != null)
                {
                    try
                    {
                        var meta = await _marketDataProvider.GetMetadataAsync(symbol);
                        if (!string.IsNullOrEmpty(meta.Sector)) item.Sector = meta.Sector;
                        if (!string.IsNullOrEmpty(meta.Industry)) item.Industry = meta.Industry;
                        item.ReturnOnEquity = meta.ReturnOnEquity;
                        item.ReturnOnAssets = meta.ReturnOnAssets;
                        item.GrossMargins = meta.GrossMargins;
                        item.OperatingMargins = meta.OperatingMargins;
                        item.ProfitMargins = meta.ProfitMargins;
                        item.CurrentRatio = meta.CurrentRatio;
                        item.QuickRatio = meta.QuickRatio;
                        item.DebtToEquity = meta.DebtToEquity;
                        item.MarketCap = meta.MarketCap;
                        item.EnterpriseValue = meta.EnterpriseValue;
                        item.TrailingPE = meta.TrailingPE;
                        item.ForwardPE = meta.ForwardPE;
                        item.PegRatio = meta.PegRatio;
                        item.PriceToSalesTrailing12Months = meta.PriceToSalesTrailing12Months;
                        item.PriceToBook = meta.PriceToBook;
                        item.Ebitda = meta.Ebitda;
                        item.FreeCashflow = meta.FreeCashflow;
                        item.OperatingCashflow = meta.OperatingCashflow;
                        item.TrailingEps = meta.TrailingEps;
                        item.ForwardEps = meta.ForwardEps;
                        item.BookValue = meta.BookValue;
                        item.SharesOutstanding = meta.SharesOutstanding;
                        item.FloatShares = meta.FloatShares;
                        item.TotalDebt = meta.TotalDebt;
                        item.TotalCash = meta.TotalCash;
                        item.TotalRevenue = meta.TotalRevenue;
                        item.DividendYield = meta.DividendYield ?? meta.DividendYieldCalculated;
                        item.DividendYieldCalculated = meta.DividendYieldCalculated;
                        item.DividendRate = meta.DividendRate;
                        item.PbrCalculated = meta.PbrCalculated;
                        item.EarningsYield = meta.EarningsYield;
                        item.FcfYield = meta.FcfYield;
                        item.FcfMargin = meta.FcfMargin;
                        item.NetDebt = meta.NetDebt;
                        item.NetDebtToEbitda = meta.NetDebtToEbitda;
                        item.DividendCoverage = meta.DividendCoverage;
                        item.PctFromFiftyTwoWeekHigh = meta.PctFromFiftyTwoWeekHigh;
                        item.FloatRatio = meta.FloatRatio;
                        item.MarketCapPerEmployee = meta.MarketCapPerEmployee;
                        item.OperatingCashFlowYield = meta.OperatingCashFlowYield;
                        item.NetCashRatio = meta.NetCashRatio;
                        item.PriceToCashFlowRatio = meta.PriceToCashFlowRatio;
                        item.NetDebtEquityRatio = meta.NetDebtEquityRatio;
                        item.FiftyTwoWeekRangePosition = meta.FiftyTwoWeekRangePosition;
                        item.DailyTurnoverRate = meta.DailyTurnoverRate;
                        item.AverageTurnoverRate = meta.AverageTurnoverRate;
                        item.FiftyTwoWeekHigh = meta.FiftyTwoWeekHigh;
                        item.FiftyTwoWeekLow = meta.FiftyTwoWeekLow;
                        item.RevenueGrowth = meta.RevenueGrowth;
                        item.EarningsGrowth = meta.EarningsGrowth;
                        item.PayoutRatio = meta.PayoutRatio;
                        item.EbitdaMargins = meta.EbitdaMargins;
                        item.ExDividendDate = meta.ExDividendDate;
                        item.LastFiscalYearEnd = meta.LastFiscalYearEnd;
                        item.MostRecentQuarter = meta.MostRecentQuarter;
                        item.GmtOffSetMilliseconds = meta.GmtOffSetMilliseconds;
                        item.ExchangeTimezoneName = meta.ExchangeTimezoneName;
                        item.QuoteType = meta.QuoteType;
                        item.RecommendationKey = meta.RecommendationKey;
                        item.RecommendationMean = meta.RecommendationMean;
                        item.NumberOfAnalystOpinions = meta.NumberOfAnalystOpinions;
                        item.TargetHighPrice = meta.TargetHighPrice;
                        item.TargetLowPrice = meta.TargetLowPrice;
                        item.TargetMeanPrice = meta.TargetMeanPrice;
                        item.TargetMedianPrice = meta.TargetMedianPrice;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScreenerViewModel] Failed to load metadata for '{symbol}': {ex.Message}");
                    }

                    try
                    {
                        var candles = await _marketDataProvider.GetTickersDataAsync(symbol, TimeFrame.D1);
                        if (candles != null && candles.Count > 0)
                        {
                            var last = candles[^1];
                            item.Close = last.Close;
                            item.Open = last.Open;
                            item.High = last.High;
                            item.Low = last.Low;
                            item.Volume = last.Volume;
                            if (last.Open != 0)
                            {
                                item.Change = last.Close - last.Open;
                                item.ChangePercent = (double)((last.Close - last.Open) / last.Open * 100m);
                            }
                            else if (candles.Count >= 2)
                            {
                                var prev = candles[^2];
                                item.Change = last.Close - prev.Close;
                                item.ChangePercent = prev.Close != 0 ? (double)((last.Close - prev.Close) / prev.Close * 100m) : 0;
                            }
                            else
                            {
                                item.Change = 0;
                                item.ChangePercent = 0;
                            }

                            if (sideConfigsForColumns != null && sideConfigsForColumns.Count > 0)
                            {
                                for (int idx = 0; idx < sideConfigsForColumns.Count; idx++)
                                {
                                    var (config, category) = sideConfigsForColumns[idx];
                                    string headerTitle = config.GetColumnHeaderTitle();
                                    string memberKey = $"Indicator_{idx}_{headerTitle}";
                                    try
                                    {
                                        if (category == ScreenerItemCategoryType.Criteria)
                                        {
                                            decimal cVal = _valueExtractor.ExtractValue(config, candles);
                                            item.DynamicIndicatorValues[memberKey] = cVal > 0m ? "Match" : "-";
                                        }
                                        else if (category == ScreenerItemCategoryType.Column)
                                        {
                                            string propName = new[] { config.CustomDisplayName, config.OutputName, config.DisplayName }
                                                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s != "SMA" && s != "Main") ?? config.DisplayName;

                                            var rawVal = item.GetRawValue(propName);
                                            if (rawVal is decimal dVal) item.DynamicIndicatorValues[memberKey] = dVal;
                                            else if (rawVal is double dbVal) item.DynamicIndicatorValues[memberKey] = (decimal)dbVal;
                                            else if (rawVal is float fVal) item.DynamicIndicatorValues[memberKey] = (decimal)fVal;
                                            else if (rawVal is int iVal) item.DynamicIndicatorValues[memberKey] = (decimal)iVal;
                                            else if (rawVal is long lVal) item.DynamicIndicatorValues[memberKey] = (decimal)lVal;
                                            else if (rawVal is string sVal) item.DynamicIndicatorValues[memberKey] = sVal;
                                            else item.DynamicIndicatorValues[memberKey] = null;
                                        }
                                        else
                                        {
                                            decimal? val = _valueExtractor.ExtractValueNullable(config, candles);
                                            item.DynamicIndicatorValues[memberKey] = val;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[ScreenerViewModel] Failed to extract value for '{memberKey}' on symbol '{symbol}': {ex.Message}");
                                        item.DynamicIndicatorValues[memberKey] = null;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Error populating indicator columns for symbol {Symbol}", symbol);
                    }
                }

                ResultItems.Add(item);
            }

            UpdateCriteriaFlowItems(counts);

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

    [RelayCommand]
    private void ToggleEntryLogicalOperator(ScreenerIndicatorEntry? entry)
    {
        if (entry == null) return;
        entry.LogicalOperator = entry.LogicalOperator == LogicalOperator.And
            ? LogicalOperator.Or
            : LogicalOperator.And;
        UpdateCriteriaFlowItems();
    }

    public void UpdateCriteriaFlowItems(Dictionary<int, int>? counts = null)
    {
        var entries = IndicatorRegistrationViewModel?.RegisteredEntries;
        if (entries == null || entries.Count == 0)
        {
            CriteriaFlowItems.Clear();
            return;
        }

        while (CriteriaFlowItems.Count > entries.Count)
        {
            CriteriaFlowItems.RemoveAt(CriteriaFlowItems.Count - 1);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            string label = IndexToLabel(i);
            entries[i].Label = label;

            int? count = (counts != null && counts.TryGetValue(i, out int val)) ? val : null;
            bool hasNext = i < entries.Count - 1;
            string opSymbol = entries[i].LogicalOperatorSymbol;

            if (i < CriteriaFlowItems.Count)
            {
                CriteriaFlowItems[i].Label = label;
                CriteriaFlowItems[i].MatchedCount = count;
                CriteriaFlowItems[i].HasNext = hasNext;
                CriteriaFlowItems[i].OperatorSymbol = opSymbol;
            }
            else
            {
                CriteriaFlowItems.Add(new CriteriaFlowItemViewModel
                {
                    Label = label,
                    MatchedCount = count,
                    HasNext = hasNext,
                    OperatorSymbol = opSymbol
                });
            }
        }
    }

    private static string IndexToLabel(int index)
    {
        if (index < 0) return string.Empty;
        string result = string.Empty;
        while (index >= 0)
        {
            result = (char)('A' + (index % 26)) + result;
            index = (index / 26) - 1;
        }
        return result;
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
