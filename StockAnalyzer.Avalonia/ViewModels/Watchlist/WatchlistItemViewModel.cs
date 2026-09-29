using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Linq;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Watchlist
{
    /// <summary>
    /// Represents a single row in the Advanced Watchlist.
    /// Changed to ObservableObject to support two-way selection binding.
    /// </summary>
    public partial class WatchlistItemViewModel : ObservableObject, IFilterableSymbol
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.PropertyInfo?> _watchlistPropertyCache = new(StringComparer.OrdinalIgnoreCase);

        [ObservableProperty]
        private bool _isChecked;

        [ObservableProperty]
        private string _symbol;

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplaySector))]
        private string _sector;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayIndustry))]
        private string _industry;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayOpen))]
        private decimal _open;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayHigh))]
        private decimal _high;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayLow))]
        private decimal _low;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayClose))]
        private decimal _close;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayVolume))]
        private long _volume;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayChangePercent))]
        private double _changePercent;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayChange))]
        private decimal _change;

        [ObservableProperty]
        private LoadStatus _status = LoadStatus.Pending;

        [ObservableProperty]
        private bool _isMetadataLoaded;

        [ObservableProperty]
        private string? _errorCode;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayLastUpdatedUtc))]
        private DateTimeOffset? _lastUpdatedUtc;

        [ObservableProperty]
        private int _retryCount;

        // Fundamentals
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayReturnOnEquity))] private decimal? _returnOnEquity;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayReturnOnAssets))] private decimal? _returnOnAssets;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayGrossMargins))] private decimal? _grossMargins;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayOperatingMargins))] private decimal? _operatingMargins;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayProfitMargins))] private decimal? _profitMargins;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayCurrentRatio))] private decimal? _currentRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayDebtToEquity))] private decimal? _debtToEquity;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayEbitda))] private decimal? _ebitda;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFreeCashflow))] private decimal? _freeCashflow;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayOperatingCashflow))] private decimal? _operatingCashflow;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTrailingPE))] private decimal? _trailingPE;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayForwardPE))] private decimal? _forwardPE;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayPriceToBook))] private decimal? _priceToBook;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTrailingEps))] private decimal? _trailingEps;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayForwardEps))] private decimal? _forwardEps;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayBookValue))] private decimal? _bookValue;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplaySharesOutstanding))] private decimal? _sharesOutstanding;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFloatShares))] private decimal? _floatShares;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayShortRatio))] private decimal? _shortRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayShortPercentOfFloat))] private decimal? _shortPercentOfFloat;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayHeldPercentInsiders))] private decimal? _heldPercentInsiders;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayHeldPercentInstitutions))] private decimal? _heldPercentInstitutions;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayLongBusinessSummary))] private string? _longBusinessSummary;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFullTimeEmployees))] private long? _fullTimeEmployees;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFiftyTwoWeekHigh))] private decimal? _fiftyTwoWeekHigh;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFiftyTwoWeekLow))] private decimal? _fiftyTwoWeekLow;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayRevenueGrowth))] private decimal? _revenueGrowth;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayEarningsGrowth))] private decimal? _earningsGrowth;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayEnterpriseValue))] private decimal? _enterpriseValue;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayEnterpriseToEbitda))] private decimal? _enterpriseToEbitda;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayBeta))] private decimal? _beta;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayPayoutRatio))] private decimal? _payoutRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayDividendRate))] private decimal? _dividendRate;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayDividendYield))] private decimal? _dividendYield;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTotalDebt))] private decimal? _totalDebt;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTotalCash))] private decimal? _totalCash;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTotalRevenue))] private decimal? _totalRevenue;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayMarketCap))] private decimal? _marketCap;

        // Derived Metrics
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayPbrCalculated))] private decimal? _pbrCalculated;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayDividendYieldCalculated))] private decimal? _dividendYieldCalculated;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayEarningsYield))] private decimal? _earningsYield;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFcfYield))] private decimal? _fcfYield;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFcfMargin))] private decimal? _fcfMargin;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayNetDebt))] private decimal? _netDebt;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayNetDebtToEbitda))] private decimal? _netDebtToEbitda;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayDividendCoverage))] private decimal? _dividendCoverage;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayPctFromFiftyTwoWeekHigh))] private decimal? _pctFromFiftyTwoWeekHigh;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFloatRatio))] private decimal? _floatRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayMarketCapPerEmployee))] private decimal? _marketCapPerEmployee;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayPegRatio))] private decimal? _pegRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayOperatingCashFlowYield))] private decimal? _operatingCashFlowYield;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayNetCashRatio))] private decimal? _netCashRatio;

        // Advanced metrics from webai.txt
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayPriceToSalesTrailing12Months))] private decimal? _priceToSalesTrailing12Months;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayEnterpriseToRevenue))] private decimal? _enterpriseToRevenue;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayEbitdaMargins))] private decimal? _ebitdaMargins;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayQuickRatio))] private decimal? _quickRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayAverageVolume))] private decimal? _averageVolume;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayPriceToCashFlowRatio))] private decimal? _priceToCashFlowRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayNetDebtEquityRatio))] private decimal? _netDebtEquityRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayFiftyTwoWeekRangePosition))] private decimal? _fiftyTwoWeekRangePosition;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayDailyTurnoverRate))] private decimal? _dailyTurnoverRate;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayAverageTurnoverRate))] private decimal? _averageTurnoverRate;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayDailyFloatShareTurnoverRatio))] private decimal? _dailyFloatShareTurnoverRatio;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayAverageFloatTurnover))] private decimal? _averageFloatTurnover;

        // New yfinance metadata fields
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayRegion))] private string? _region;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayQuoteType))] private string? _quoteType;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayExchangeTimezoneName))] private string? _exchangeTimezoneName;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayGmtOffSetMilliseconds))] private long? _gmtOffSetMilliseconds;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayExDividendDate))] private long? _exDividendDate;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayLastFiscalYearEnd))] private long? _lastFiscalYearEnd;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayMostRecentQuarter))] private long? _mostRecentQuarter;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTargetHighPrice))] private decimal? _targetHighPrice;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTargetLowPrice))] private decimal? _targetLowPrice;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTargetMeanPrice))] private decimal? _targetMeanPrice;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTargetMedianPrice))] private decimal? _targetMedianPrice;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayRecommendationKey))] private string? _recommendationKey;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayRecommendationMean))] private decimal? _recommendationMean;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayNumberOfAnalystOpinions))] private long? _numberOfAnalystOpinions;

        // Formatters for new fields
        public string DisplaySector => string.IsNullOrEmpty(Sector) || Sector == "N/A" ? "-" : Sector;
        public string DisplayIndustry => string.IsNullOrEmpty(Industry) || Industry == "N/A" ? "-" : Industry;
        public string DisplayRegion => Region ?? "-";
        public string DisplayQuoteType => QuoteType ?? "-";
        public string DisplayExchangeTimezoneName => ExchangeTimezoneName ?? "-";
        public string DisplayRecommendationKey => RecommendationKey ?? "-";
        public string DisplayGmtOffSetMilliseconds => GmtOffSetMilliseconds.HasValue ? GmtOffSetMilliseconds.Value.ToString() : "-";
        public string DisplayExDividendDate => FormatUnixDate(ExDividendDate);
        public string DisplayLastFiscalYearEnd => FormatUnixDate(LastFiscalYearEnd);
        public string DisplayMostRecentQuarter => FormatUnixDate(MostRecentQuarter);
        public string DisplayTargetHighPrice => TargetHighPrice == null || TargetHighPrice == 0 ? "-" : FormatDecimal(TargetHighPrice);
        public string DisplayTargetLowPrice => TargetLowPrice == null || TargetLowPrice == 0 ? "-" : FormatDecimal(TargetLowPrice);
        public string DisplayTargetMeanPrice => TargetMeanPrice == null || TargetMeanPrice == 0 ? "-" : FormatDecimal(TargetMeanPrice);
        public string DisplayTargetMedianPrice => TargetMedianPrice == null || TargetMedianPrice == 0 ? "-" : FormatDecimal(TargetMedianPrice);
        public string DisplayRecommendationMean => RecommendationMean == null || RecommendationMean == 0 ? "-" : FormatDecimal(RecommendationMean);
        public string DisplayNumberOfAnalystOpinions => NumberOfAnalystOpinions.HasValue && NumberOfAnalystOpinions.Value != 0 ? FormatLargeNumber(NumberOfAnalystOpinions) : "-";

        private string FormatUnixDate(long? seconds)
        {
            if (!seconds.HasValue) return "-";
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).LocalDateTime.ToString("yyyy-MM-dd");
            }
            catch
            {
                return "-";
            }
        }

        // Timestamp
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayMetadataLastUpdated))] private DateTime? _metadataLastUpdated;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayTag))]
        private string? _tag;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayNotes))] private string? _notes;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayReminder))] private string? _reminder;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayLong)), NotifyPropertyChangedFor(nameof(DisplayEntryPrice))] private decimal? _long;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayExitLong)), NotifyPropertyChangedFor(nameof(DisplayTargetPrice))] private decimal? _exitLong;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayStopLossLong)), NotifyPropertyChangedFor(nameof(DisplayStopLoss))] private decimal? _stopLossLong;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayShort))] private decimal? _short;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayExitShort))] private decimal? _exitShort;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayStopLossShort))] private decimal? _stopLossShort;

        // Signal Flags
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayIsLong)), NotifyPropertyChangedFor(nameof(ToolTipIsLong))] private bool? _isLong;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayIsTPLong)), NotifyPropertyChangedFor(nameof(ToolTipIsTPLong))] private bool? _isTPLong;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayIsSLLong)), NotifyPropertyChangedFor(nameof(ToolTipIsSLLong))] private bool? _isSLLong;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayIsShort)), NotifyPropertyChangedFor(nameof(ToolTipIsShort))] private bool? _isShort;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayIsTPShort)), NotifyPropertyChangedFor(nameof(ToolTipIsTPShort))] private bool? _isTPShort;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayIsSLShort)), NotifyPropertyChangedFor(nameof(ToolTipIsSLShort))] private bool? _isSLShort;

        public decimal? EntryPrice { get => Long; set => Long = value; }
        public decimal? TargetPrice { get => ExitLong; set => ExitLong = value; }
        public decimal? StopLoss { get => StopLossLong; set => StopLossLong = value; }

        /// <summary>Single-line cell text: <see cref="Notes"/> (the Notes tab's latest-article "Read
        /// more" collapse preview, real newlines preserved) with newlines replaced by spaces so the
        /// cell never wraps. The tooltip (bound directly to <see cref="Notes"/>) shows the original,
        /// unconverted text.</summary>
        public string DisplayNotes => string.IsNullOrWhiteSpace(Notes) ? "-" : Notes.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        public string DisplayReminder => string.IsNullOrWhiteSpace(Reminder) ? "-" : Reminder;
        public string DisplayLong => Long == null || Long == 0 ? "-" : FormatDecimal(Long);
        public string DisplayExitLong => ExitLong == null || ExitLong == 0 ? "-" : FormatDecimal(ExitLong);
        public string DisplayStopLossLong => StopLossLong == null || StopLossLong == 0 ? "-" : FormatDecimal(StopLossLong);
        public string DisplayShort => Short == null || Short == 0 ? "-" : FormatDecimal(Short);
        public string DisplayExitShort => ExitShort == null || ExitShort == 0 ? "-" : FormatDecimal(ExitShort);
        public string DisplayStopLossShort => StopLossShort == null || StopLossShort == 0 ? "-" : FormatDecimal(StopLossShort);

        public string DisplayIsLong => IsLong.HasValue ? (IsLong.Value ? "True" : "False") : "-";
        public string DisplayIsTPLong => IsTPLong.HasValue ? (IsTPLong.Value ? "True" : "False") : "-";
        public string DisplayIsSLLong => IsSLLong.HasValue ? (IsSLLong.Value ? "True" : "False") : "-";
        public string DisplayIsShort => IsShort.HasValue ? (IsShort.Value ? "True" : "False") : "-";
        public string DisplayIsTPShort => IsTPShort.HasValue ? (IsTPShort.Value ? "True" : "False") : "-";
        public string DisplayIsSLShort => IsSLShort.HasValue ? (IsSLShort.Value ? "True" : "False") : "-";

        public string ToolTipIsLong => GetSignalToolTip(StockAnalyzer.Core.Models.Screener.SignalTargetType.Long, IsLong);
        public string ToolTipIsTPLong => GetSignalToolTip(StockAnalyzer.Core.Models.Screener.SignalTargetType.ExitLong, IsTPLong);
        public string ToolTipIsSLLong => GetSignalToolTip(StockAnalyzer.Core.Models.Screener.SignalTargetType.StopLossLong, IsSLLong);
        public string ToolTipIsShort => GetSignalToolTip(StockAnalyzer.Core.Models.Screener.SignalTargetType.Short, IsShort);
        public string ToolTipIsTPShort => GetSignalToolTip(StockAnalyzer.Core.Models.Screener.SignalTargetType.ExitShort, IsTPShort);
        public string ToolTipIsSLShort => GetSignalToolTip(StockAnalyzer.Core.Models.Screener.SignalTargetType.StopLossShort, IsSLShort);

        private string GetSignalToolTip(StockAnalyzer.Core.Models.Screener.SignalTargetType targetType, bool? flagState)
        {
            if (string.IsNullOrEmpty(Symbol) || !flagState.HasValue) return "-";
            try
            {
                var bundles = StockAnalyzer.Core.Services.UserStrategyMetadataRepository.Instance.GetSignalBundles(Symbol)
                    .Where(b => b.TargetType == targetType).ToList();
                if (bundles.Count == 0)
                {
                    return flagState.Value ? "True" : "False";
                }
                return string.Join("\n", bundles.Select(b => $"{b.Name} {(b.IsHit ? "True" : "False")}"));
            }
            catch
            {
                return flagState.Value ? "True" : "False";
            }
        }

        public string DisplayEntryPrice => DisplayLong;
        public string DisplayTargetPrice => DisplayExitLong;
        public string DisplayStopLoss => DisplayStopLossLong;

        partial void OnSymbolChanged(string value)
        {
            LoadStrategyMetadata(value);
        }

        /// <summary>Re-reads Notes from UserStrategyMetadataRepository via the Notes property setter
        /// (not the backing field LoadStrategyMetadata uses), so DisplayNotes' change notification
        /// actually fires. Called when UserStrategyMetadataRepository.StrategyChanged reports this
        /// row's ticker was updated (e.g. a new Notes-tab article), so the grid no longer requires an
        /// app restart to reflect the change.</summary>
        public void RefreshNotes()
        {
            if (string.IsNullOrEmpty(Symbol)) return;
            var saved = StockAnalyzer.Core.Services.UserStrategyMetadataRepository.Instance.GetStrategy(Symbol);
            Notes = saved?.Notes;
        }

        private void LoadStrategyMetadata(string? symbol)
        {
            if (!string.IsNullOrEmpty(symbol))
            {
                var saved = StockAnalyzer.Core.Services.UserStrategyMetadataRepository.Instance.GetStrategy(symbol);
                if (saved != null)
                {
                    _long = saved.EffectiveLong;
                    _exitLong = saved.EffectiveExitLong;
                    _stopLossLong = saved.EffectiveStopLossLong;
                    _short = saved.Short;
                    _exitShort = saved.ExitShort;
                    _stopLossShort = saved.StopLossShort;
                    _notes = saved.Notes;
                    _reminder = saved.Reminder;
                    IsLong = saved.IsLong;
                    IsTPLong = saved.IsTPLong;
                    IsSLLong = saved.IsSLLong;
                    IsShort = saved.IsShort;
                    IsTPShort = saved.IsTPShort;
                    IsSLShort = saved.IsSLShort;
                }

                var bundles = StockAnalyzer.Core.Services.UserStrategyMetadataRepository.Instance.GetSignalBundles(symbol);
                if (bundles != null && bundles.Count > 0)
                {
                    var longB = bundles.Where(b => b.TargetType == SignalTargetType.Long).ToList();
                    if (longB.Count > 0) IsLong = longB.Any(b => b.IsHit);

                    var exitLongB = bundles.Where(b => b.TargetType == SignalTargetType.ExitLong).ToList();
                    if (exitLongB.Count > 0) IsTPLong = exitLongB.Any(b => b.IsHit);

                    var slLongB = bundles.Where(b => b.TargetType == SignalTargetType.StopLossLong).ToList();
                    if (slLongB.Count > 0) IsSLLong = slLongB.Any(b => b.IsHit);

                    var shortB = bundles.Where(b => b.TargetType == SignalTargetType.Short).ToList();
                    if (shortB.Count > 0) IsShort = shortB.Any(b => b.IsHit);

                    var exitShortB = bundles.Where(b => b.TargetType == SignalTargetType.ExitShort).ToList();
                    if (exitShortB.Count > 0) IsTPShort = exitShortB.Any(b => b.IsHit);

                    var slShortB = bundles.Where(b => b.TargetType == SignalTargetType.StopLossShort).ToList();
                    if (slShortB.Count > 0) IsSLShort = slShortB.Any(b => b.IsHit);
                }
            }
        }

        /// <summary>
        /// Applies the Ticker Dashboard's Save/Apply result. The trailing string is the Dashboard's
        /// Reminder field, not Notes - Notes is an auto-derived preview of the Notes tab's latest
        /// article (maintained by TickerMetadataNotesCacheSynchronizer), so its current value is
        /// read back and passed through unchanged here to avoid clobbering that cache.
        /// </summary>
        public void UpdateStrategyMetadata(decimal? longVal, decimal? exitLong, decimal? stopLossLong, decimal? shortVal, decimal? exitShort, decimal? stopLossShort, string? reminder)
        {
            Long = longVal;
            ExitLong = exitLong;
            StopLossLong = stopLossLong;
            Short = shortVal;
            ExitShort = exitShort;
            StopLossShort = stopLossShort;
            Reminder = reminder;
            if (!string.IsNullOrEmpty(Symbol))
            {
                var currentNotes = StockAnalyzer.Core.Services.UserStrategyMetadataRepository.Instance.GetStrategy(Symbol)?.Notes;
                StockAnalyzer.Core.Services.UserStrategyMetadataRepository.Instance.SaveStrategy(Symbol, longVal, exitLong, stopLossLong, shortVal, exitShort, stopLossShort, currentNotes, reminder: reminder);
                LoadStrategyMetadata(Symbol);
            }
        }

        public void UpdateStrategyMetadata(decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? reminder)
        {
            UpdateStrategyMetadata(entryPrice, targetPrice, stopLoss, Short, ExitShort, StopLossShort, reminder);
        }

        public System.Collections.ObjectModel.ObservableCollection<string> TagsList { get; } = new();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<decimal, string> _percentCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<decimal, string> _decimalCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<decimal, string> _largeDecimalCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _largeLongCache = new();

        private string FormatPercent(decimal? val)
        {
            if (!val.HasValue) return "-";
            if (_percentCache.Count > 10000) _percentCache.Clear();
            return _percentCache.GetOrAdd(val.Value, v => $"{v.ToString(WatchlistConstants.FormatPercent)}{WatchlistConstants.PercentSuffix}");
        }

        private string FormatDecimal(decimal? val)
        {
            if (!val.HasValue) return "-";
            if (_decimalCache.Count > 10000) _decimalCache.Clear();
            return _decimalCache.GetOrAdd(val.Value, v => v.ToString(WatchlistConstants.FormatDecimal));
        }

        private string FormatLargeNumber(decimal? val)
        {
            if (!val.HasValue) return "-";
            if (_largeDecimalCache.Count > 10000) _largeDecimalCache.Clear();
            return _largeDecimalCache.GetOrAdd(val.Value, v => v.ToString("N0"));
        }

        private string FormatLargeNumber(long? val)
        {
            if (!val.HasValue) return "-";
            if (_largeLongCache.Count > 10000) _largeLongCache.Clear();
            return _largeLongCache.GetOrAdd(val.Value, v => v.ToString("N0"));
        }
        private string FormatDateTime(DateTime? val) => val.HasValue ? val.Value.ToString("yyyyMMdd") : "";

        public string DisplayReturnOnEquity => FormatPercent(ReturnOnEquity);
        public string DisplayReturnOnAssets => FormatPercent(ReturnOnAssets);
        public string DisplayGrossMargins => FormatPercent(GrossMargins);
        public string DisplayOperatingMargins => FormatPercent(OperatingMargins);
        public string DisplayProfitMargins => FormatPercent(ProfitMargins);
        public string DisplayCurrentRatio => FormatDecimal(CurrentRatio);
        public string DisplayDebtToEquity => FormatDecimal(DebtToEquity);
        public string DisplayEbitda => FormatLargeNumber(Ebitda);
        public string DisplayFreeCashflow => FormatLargeNumber(FreeCashflow);
        public string DisplayOperatingCashflow => FormatLargeNumber(OperatingCashflow);
        public string DisplayTrailingPE => TrailingPE == null || TrailingPE == 0 ? "-" : FormatDecimal(TrailingPE);
        public string DisplayForwardPE => ForwardPE == null || ForwardPE == 0 ? "-" : FormatDecimal(ForwardPE);
        public string DisplayPriceToBook => PriceToBook == null || PriceToBook == 0 ? "-" : FormatDecimal(PriceToBook);
        public string DisplayTrailingEps => FormatDecimal(TrailingEps);
        public string DisplayForwardEps => FormatDecimal(ForwardEps);
        public string DisplayBookValue => FormatDecimal(BookValue);
        public string DisplaySharesOutstanding => SharesOutstanding == null || SharesOutstanding == 0 ? "-" : FormatLargeNumber(SharesOutstanding);
        public string DisplayFloatShares => FloatShares == null || FloatShares == 0 ? "-" : FormatLargeNumber(FloatShares);
        public string DisplayShortRatio => FormatDecimal(ShortRatio);
        public string DisplayShortPercentOfFloat => FormatPercent(ShortPercentOfFloat);
        public string DisplayHeldPercentInsiders => FormatPercent(HeldPercentInsiders);
        public string DisplayHeldPercentInstitutions => FormatPercent(HeldPercentInstitutions);
        public string DisplayLongBusinessSummary => LongBusinessSummary ?? "-";
        public string DisplayFullTimeEmployees => FormatLargeNumber(FullTimeEmployees);
        public string DisplayFiftyTwoWeekHigh => FormatDecimal(FiftyTwoWeekHigh);
        public string DisplayFiftyTwoWeekLow => FormatDecimal(FiftyTwoWeekLow);
        public string DisplayRevenueGrowth => FormatPercent(RevenueGrowth);
        public string DisplayEarningsGrowth => FormatPercent(EarningsGrowth);
        public string DisplayEnterpriseValue => FormatLargeNumber(EnterpriseValue);
        public string DisplayEnterpriseToEbitda => FormatDecimal(EnterpriseToEbitda);
        public string DisplayBeta => FormatDecimal(Beta);
        public string DisplayPayoutRatio => FormatPercent(PayoutRatio);
        public string DisplayDividendRate => FormatDecimal(DividendRate);
        public string DisplayDividendYield => FormatPercent(DividendYield);
        public string DisplayTotalDebt => FormatLargeNumber(TotalDebt);
        public string DisplayTotalCash => FormatLargeNumber(TotalCash);
        public string DisplayTotalRevenue => FormatLargeNumber(TotalRevenue);
        public string DisplayMarketCap => FormatLargeNumber(MarketCap);
        public string DisplayPbrCalculated => FormatDecimal(PbrCalculated);
        public string DisplayDividendYieldCalculated => DividendYieldCalculated.HasValue ? $"{FormatDecimal(DividendYieldCalculated)}%" : "-";
        public string DisplayEarningsYield => EarningsYield.HasValue ? $"{FormatDecimal(EarningsYield)}%" : "-";
        public string DisplayFcfYield => FcfYield.HasValue ? $"{FormatDecimal(FcfYield)}%" : "-";
        public string DisplayFcfMargin => FcfMargin.HasValue ? $"{FormatDecimal(FcfMargin)}%" : "-";
        public string DisplayNetDebt => FormatLargeNumber(NetDebt);
        public string DisplayNetDebtToEbitda => FormatDecimal(NetDebtToEbitda);
        public string DisplayDividendCoverage => FormatDecimal(DividendCoverage);
        public string DisplayPctFromFiftyTwoWeekHigh => PctFromFiftyTwoWeekHigh.HasValue ? $"{FormatDecimal(PctFromFiftyTwoWeekHigh)}%" : "-";
        public string DisplayFloatRatio => FloatRatio.HasValue ? $"{FormatDecimal(FloatRatio)}%" : "-";
        public string DisplayMarketCapPerEmployee => FormatLargeNumber(MarketCapPerEmployee);
        public string DisplayPegRatio => PegRatio == null || PegRatio == 0 ? "-" : FormatDecimal(PegRatio);
        public string DisplayOperatingCashFlowYield => OperatingCashFlowYield.HasValue ? $"{FormatDecimal(OperatingCashFlowYield)}%" : "-";
        public string DisplayNetCashRatio => FormatPercent(NetCashRatio);
        public string DisplayPriceToSalesTrailing12Months => FormatDecimal(PriceToSalesTrailing12Months);
        public string DisplayEnterpriseToRevenue => FormatDecimal(EnterpriseToRevenue);
        public string DisplayEbitdaMargins => FormatPercent(EbitdaMargins);
        public string DisplayQuickRatio => FormatDecimal(QuickRatio);
        public string DisplayAverageVolume => FormatLargeNumber(AverageVolume);
        public string DisplayPriceToCashFlowRatio => FormatDecimal(PriceToCashFlowRatio);
        public string DisplayNetDebtEquityRatio => FormatDecimal(NetDebtEquityRatio);
        public string DisplayFiftyTwoWeekRangePosition => FormatDecimal(FiftyTwoWeekRangePosition);
        public string DisplayDailyTurnoverRate => DailyTurnoverRate.HasValue ? $"{FormatDecimal(DailyTurnoverRate)}%" : "-";
        public string DisplayAverageTurnoverRate => AverageTurnoverRate.HasValue ? $"{FormatDecimal(AverageTurnoverRate)}%" : "-";
        public string DisplayDailyFloatShareTurnoverRatio => DailyFloatShareTurnoverRatio.HasValue ? $"{FormatDecimal(DailyFloatShareTurnoverRatio)}%" : "-";
        public string DisplayAverageFloatTurnover => AverageFloatTurnover.HasValue ? $"{FormatDecimal(AverageFloatTurnover)}%" : "-";
        public string DisplayLastUpdatedUtc => LastUpdatedUtc.HasValue ? FormatDateTime(LastUpdatedUtc.Value.LocalDateTime) : "";
        public string DisplayMetadataLastUpdated => MetadataLastUpdated.HasValue ? FormatDateTime(MetadataLastUpdated.Value.ToLocalTime()) : "";
        public string DisplayTag => Tag ?? "-";

        /// <summary>
        /// Transition guards to ensure deterministic state changes as per strict specifications.
        /// </summary>
        private bool IsValidTransition(LoadStatus to) => (Status, to) switch
        {
            (LoadStatus.Pending, LoadStatus.Loading) => true,
            (LoadStatus.Loading, LoadStatus.Success) => true,
            (LoadStatus.Loading, LoadStatus.Failed) => true,
            (LoadStatus.Loading, LoadStatus.Canceled) => true,
            (LoadStatus.Success, LoadStatus.Loading) => true,
            (LoadStatus.Failed, LoadStatus.Pending) => true,
            (LoadStatus.Canceled, LoadStatus.Pending) => true,
            _ => false
        };

        private void SetStatus(LoadStatus next, string? errorCode = null, string? errorMessage = null)
        {
            if (Status == next) return;

            if (!IsValidTransition(next))
                throw new InvalidOperationException($"Illegal LoadStatus transition: {Status} -> {next}");

            Status = next;
            if (errorCode != null) ErrorCode = errorCode;
            if (errorMessage != null) ErrorMessage = errorMessage;
            
            if (next is LoadStatus.Success or LoadStatus.Failed)
            {
                if (LastUpdatedUtc == null)
                {
                    LastUpdatedUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        public void MarkLoading() => SetStatus(LoadStatus.Loading);
        public void MarkSuccess() => SetStatus(LoadStatus.Success);
        public void MarkCanceled() => SetStatus(LoadStatus.Canceled);
        public void MarkFailed(string errorCode, string errorMessage) => SetStatus(LoadStatus.Failed, errorCode, errorMessage);
        public void ResetToPending() => SetStatus(LoadStatus.Pending);


        public WatchlistItemViewModel(
            string symbol,
            string name,
            string sector,
            string industry,
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            long volume,
            double changePercent,
            decimal change = 0)
        {
            _symbol = symbol;
            _name = name;
            _sector = sector;
            _industry = industry;
            _open = open;
            _high = high;
            _low = low;
            _close = close;
            _volume = volume;
            _changePercent = changePercent;
            _change = change;
            TagsList.CollectionChanged += (s, e) => UpdateTagString();
            LoadStrategyMetadata(symbol);
        }

        public static StockAnalyzer.Core.Services.IDispatcherService? DispatcherService { get; set; }

        private bool _isUpdatingTagsList;
        partial void OnTagChanged(string? value)
        {
            var dispatcher = DispatcherService;
            if (dispatcher == null)
            {
                UpdateTagsListInternal(value);
                return;
            }

            if (dispatcher.CheckAccess())
            {
                UpdateTagsListInternal(value);
            }
            else
            {
                dispatcher.Post(() => UpdateTagsListInternal(value));
            }
        }

        private void UpdateTagsListInternal(string? value)
        {
            if (_isUpdatingTagsList) return;
            _isUpdatingTagsList = true;
            try
            {
                TagsList.Clear();
                if (!string.IsNullOrEmpty(value))
                {
                    foreach (var t in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = t.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !TagsList.Contains(trimmed))
                        {
                            TagsList.Add(trimmed);
                        }
                    }
                }
            }
            finally
            {
                _isUpdatingTagsList = false;
            }
        }

        private void UpdateTagString()
        {
            if (_isUpdatingTagsList) return;
            _isUpdatingTagsList = true;
            try
            {
                Tag = TagsList.Count > 0 ? string.Join(",", TagsList) : null;
            }
            finally
            {
                _isUpdatingTagsList = false;
            }
        }

        /// <summary>
        /// Forces a Tag property change notification to trigger layout recalculation
        /// (e.g., when font size changes require chip fitting to be recalculated).
        /// </summary>
        public void NotifyTagLayoutChanged()
        {
            OnPropertyChanged(nameof(Tag));
        }

        public object? GetPropertyValue(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return null;
            return GetRawValue(propertyName);
        }

        public string DisplayOpen => Open == 0 ? "-" : Open.ToString(WatchlistConstants.FormatDecimal);
        public string DisplayHigh => High == 0 ? "-" : High.ToString(WatchlistConstants.FormatDecimal);
        public string DisplayLow => Low == 0 ? "-" : Low.ToString(WatchlistConstants.FormatDecimal);
        public string DisplayClose => Close == 0 ? "-" : Close.ToString(WatchlistConstants.FormatDecimal);
        public string DisplayVolume => Volume == 0 ? "-" : Volume.ToString(WatchlistConstants.FormatInteger);
        public string DisplayChange => Change == 0 ? "-" : Change.ToString(WatchlistConstants.FormatDecimal);
        public string DisplayChangePercent => ChangePercent == 0 ? "-" : $"{ChangePercent.ToString(WatchlistConstants.FormatPercent)}{WatchlistConstants.PercentSuffix}";

        public string GetDisplayValue(string memberName) => memberName switch
        {
            "Symbol" => Symbol,
            "Name" => Name,
            "Sector" => DisplaySector,
            "Industry" => DisplayIndustry,
            "Open" => DisplayOpen,
            "High" => DisplayHigh,
            "Low" => DisplayLow,
            "Close" => DisplayClose,
            "Volume" => DisplayVolume,
            "Change" => DisplayChange,
            "ChangePercent" => DisplayChangePercent,
            "ReturnOnEquity" => DisplayReturnOnEquity,
            "ReturnOnAssets" => DisplayReturnOnAssets,
            "GrossMargins" => DisplayGrossMargins,
            "OperatingMargins" => DisplayOperatingMargins,
            "ProfitMargins" => DisplayProfitMargins,
            "CurrentRatio" => DisplayCurrentRatio,
            "DebtToEquity" => DisplayDebtToEquity,
            "Ebitda" => DisplayEbitda,
            "FreeCashflow" => DisplayFreeCashflow,
            "OperatingCashflow" => DisplayOperatingCashflow,
            "TrailingPE" => DisplayTrailingPE,
            "ForwardPE" => DisplayForwardPE,
            "PriceToBook" => DisplayPriceToBook,
            "TrailingEps" => DisplayTrailingEps,
            "ForwardEps" => DisplayForwardEps,
            "BookValue" => DisplayBookValue,
            "SharesOutstanding" => DisplaySharesOutstanding,
            "FloatShares" => DisplayFloatShares,
            "ShortRatio" => DisplayShortRatio,
            "ShortPercentOfFloat" => DisplayShortPercentOfFloat,
            "HeldPercentInsiders" => DisplayHeldPercentInsiders,
            "HeldPercentInstitutions" => DisplayHeldPercentInstitutions,
            "LongBusinessSummary" => DisplayLongBusinessSummary,
            "FullTimeEmployees" => DisplayFullTimeEmployees,
            "FiftyTwoWeekHigh" => DisplayFiftyTwoWeekHigh,
            "FiftyTwoWeekLow" => DisplayFiftyTwoWeekLow,
            "RevenueGrowth" => DisplayRevenueGrowth,
            "EarningsGrowth" => DisplayEarningsGrowth,
            "EnterpriseValue" => DisplayEnterpriseValue,
            "EnterpriseToEbitda" => DisplayEnterpriseToEbitda,
            "Beta" => DisplayBeta,
            "PayoutRatio" => DisplayPayoutRatio,
            "DividendRate" => DisplayDividendRate,
            "DividendYield" => DisplayDividendYield,
            "TotalDebt" => DisplayTotalDebt,
            "TotalCash" => DisplayTotalCash,
            "TotalRevenue" => DisplayTotalRevenue,
            "MarketCap" => DisplayMarketCap,
            "PbrCalculated" => DisplayPbrCalculated,
            "DividendYieldCalculated" => DisplayDividendYieldCalculated,
            "EarningsYield" => DisplayEarningsYield,
            "FcfYield" => DisplayFcfYield,
            "FcfMargin" => DisplayFcfMargin,
            "NetDebt" => DisplayNetDebt,
            "NetDebtToEbitda" => DisplayNetDebtToEbitda,
            "DividendCoverage" => DisplayDividendCoverage,
            "PctFromFiftyTwoWeekHigh" => DisplayPctFromFiftyTwoWeekHigh,
            "FloatRatio" => DisplayFloatRatio,
            "MarketCapPerEmployee" => DisplayMarketCapPerEmployee,
            "PegRatio" => DisplayPegRatio,
            "OperatingCashFlowYield" => DisplayOperatingCashFlowYield,
            "NetCashRatio" => DisplayNetCashRatio,
            "PriceToSalesTrailing12Months" => DisplayPriceToSalesTrailing12Months,
            "EnterpriseToRevenue" => DisplayEnterpriseToRevenue,
            "EbitdaMargins" => DisplayEbitdaMargins,
            "QuickRatio" => DisplayQuickRatio,
            "AverageVolume" => DisplayAverageVolume,
            "PriceToCashFlowRatio" => DisplayPriceToCashFlowRatio,
            "NetDebtEquityRatio" => DisplayNetDebtEquityRatio,
            "FiftyTwoWeekRangePosition" => DisplayFiftyTwoWeekRangePosition,
            "DailyTurnoverRate" => DisplayDailyTurnoverRate,
            "AverageTurnoverRate" => DisplayAverageTurnoverRate,
            "DailyFloatShareTurnoverRatio" => DisplayDailyFloatShareTurnoverRatio,
            "AverageFloatTurnover" => DisplayAverageFloatTurnover,
            "Region" => DisplayRegion,
            "QuoteType" => DisplayQuoteType,
            "ExchangeTimezoneName" => DisplayExchangeTimezoneName,
            "GmtOffSetMilliseconds" => DisplayGmtOffSetMilliseconds,
            "ExDividendDate" => DisplayExDividendDate,
            "LastFiscalYearEnd" => DisplayLastFiscalYearEnd,
            "MostRecentQuarter" => DisplayMostRecentQuarter,
            "TargetHighPrice" => DisplayTargetHighPrice,
            "TargetLowPrice" => DisplayTargetLowPrice,
            "TargetMeanPrice" => DisplayTargetMeanPrice,
            "TargetMedianPrice" => DisplayTargetMedianPrice,
            "RecommendationKey" => DisplayRecommendationKey,
            "RecommendationMean" => DisplayRecommendationMean,
            "NumberOfAnalystOpinions" => DisplayNumberOfAnalystOpinions,
            "LastUpdatedUtc" => DisplayLastUpdatedUtc,
            "MetadataLastUpdated" => DisplayMetadataLastUpdated,
            "Tag" => DisplayTag,
            "IsLong" => DisplayIsLong,
            "IsTPLong" => DisplayIsTPLong,
            "IsSLLong" => DisplayIsSLLong,
            "IsShort" => DisplayIsShort,
            "IsTPShort" => DisplayIsTPShort,
            "IsSLShort" => DisplayIsSLShort,
            _ => "-"
        };

        /// <summary>
        /// Dictionary holding dynamic calculated values for screening indicators (keyed by MemberName).
        /// </summary>
        public System.Collections.Generic.Dictionary<string, object?> DynamicIndicatorValues { get; } = new();

        public IComparable? GetRawValue(string memberName) => memberName switch
        {
            "Symbol" => Symbol,
            "Name" => Name,
            "Sector" => Sector,
            "Industry" => Industry,
            "Open" => Open,
            "High" => High,
            "Low" => Low,
            "Close" => Close,
            "Volume" => Volume,
            "Change" => Change,
            "ChangePercent" or "Change %" => ChangePercent,
            "ReturnOnEquity" or "ROE" => ReturnOnEquity,
            "ReturnOnAssets" or "ROA" => ReturnOnAssets,
            "GrossMargins" or "Gross Margin" => GrossMargins,
            "OperatingMargins" or "Operating Margin" => OperatingMargins,
            "ProfitMargins" or "Profit Margin" => ProfitMargins,
            "CurrentRatio" => CurrentRatio,
            "DebtToEquity" or "D/E Ratio" => DebtToEquity,
            "Ebitda" or "EBITDA" => Ebitda,
            "FreeCashflow" or "Free Cash Flow" => FreeCashflow,
            "OperatingCashflow" or "Operating Cash Flow" => OperatingCashflow,
            "TrailingPE" or "Trailing P/E" => TrailingPE,
            "ForwardPE" or "Forward P/E" => ForwardPE,
            "PriceToBook" or "P/B Ratio" => PriceToBook,
            "TrailingEps" or "Trailing EPS" or "EPS" => TrailingEps,
            "ForwardEps" or "Forward EPS" => ForwardEps,
            "BookValue" or "Book Value" or "Book Value Per Share" or "BPS" => BookValue,
            "SharesOutstanding" or "Shares Outstanding" => SharesOutstanding,
            "FloatShares" or "Float Shares" => FloatShares,
            "ShortRatio" or "Short Ratio" or "Days to Cover" => ShortRatio,
            "ShortPercentOfFloat" or "Short % Float" or "Short Interest % of Float" => ShortPercentOfFloat,
            "HeldPercentInsiders" or "Insider Held %" or "Insider Ownership %" => HeldPercentInsiders,
            "HeldPercentInstitutions" or "Inst Held %" or "Institutional Ownership %" => HeldPercentInstitutions,
            "LongBusinessSummary" => LongBusinessSummary,
            "FullTimeEmployees" or "Full Time Employees" or "Employees" => FullTimeEmployees,
            "FiftyTwoWeekHigh" or "52W High" or "52-Week High" => FiftyTwoWeekHigh,
            "FiftyTwoWeekLow" or "52W Low" or "52-Week Low" => FiftyTwoWeekLow,
            "RevenueGrowth" or "Revenue Growth" => RevenueGrowth,
            "EarningsGrowth" or "Earnings Growth" => EarningsGrowth,
            "EnterpriseValue" or "EV" => EnterpriseValue,
            "EnterpriseToEbitda" or "EV/EBITDA" => EnterpriseToEbitda,
            "Beta" => Beta,
            "PayoutRatio" or "Payout Ratio" => PayoutRatio,
            "DividendRate" or "Dividend Rate" or "Dividend per Share" => DividendRate,
            "DividendYield" or "Dividend Yield" => DividendYield,
            "TotalDebt" or "Total Debt" => TotalDebt,
            "TotalCash" or "Total Cash" => TotalCash,
            "TotalRevenue" or "Total Revenue" => TotalRevenue,
            "MarketCap" or "Market Cap" => MarketCap,
            "PbrCalculated" or "P/B (Live)" or "P/B (Calculated)" => PbrCalculated,
            "DividendYieldCalculated" or "Div Yield (Live)" or "Div Yield (Calc)" => DividendYieldCalculated,
            "EarningsYield" or "Earnings Yield" => EarningsYield,
            "FcfYield" or "FCF Yield" => FcfYield,
            "FcfMargin" or "FCF Margin" => FcfMargin,
            "NetDebt" or "Net Debt" => NetDebt,
            "NetDebtToEbitda" or "Net Debt/EBITDA" or "Net Debt / EBITDA" => NetDebtToEbitda,
            "DividendCoverage" or "Dividend Coverage" or "Div Coverage" => DividendCoverage,
            "PctFromFiftyTwoWeekHigh" or "% Off 52W High" or "% from 52W High" => PctFromFiftyTwoWeekHigh,
            "FloatRatio" or "Float Ratio" => FloatRatio,
            "MarketCapPerEmployee" or "Market Cap/Employee" or "Market Cap / Employee" => MarketCapPerEmployee,
            "PegRatio" or "PEG Ratio" or "PEG" => PegRatio,
            "PriceToSalesTrailing12Months" or "P/S (TTM)" or "P/S" => PriceToSalesTrailing12Months,
            "EnterpriseToRevenue" or "EV/Revenue" => EnterpriseToRevenue,
            "EbitdaMargins" or "EBITDA Margin" => EbitdaMargins,
            "QuickRatio" or "Quick Ratio" => QuickRatio,
            "AverageVolume" or "Average Volume" or "Avg Volume" => AverageVolume,
            "PriceToCashFlowRatio" or "P/CF Ratio" => PriceToCashFlowRatio,
            "NetDebtEquityRatio" or "Net D/E Ratio" => NetDebtEquityRatio,
            "FiftyTwoWeekRangePosition" or "52W Range Position" or "52W Range Pos." => FiftyTwoWeekRangePosition,
            "DailyTurnoverRate" or "Daily Turnover %" or "Turnover Rate" => DailyTurnoverRate,
            "AverageTurnoverRate" or "Avg Turnover %" or "Avg Turnover Rate" => AverageTurnoverRate,
            "DailyFloatShareTurnoverRatio" or "Float Turnover Rate" => DailyFloatShareTurnoverRatio,
            "AverageFloatTurnover" or "Avg Float Turnover Rate" => AverageFloatTurnover,
            "Region" => Region,
            "QuoteType" => QuoteType,
            "ExchangeTimezoneName" => ExchangeTimezoneName,
            "GmtOffSetMilliseconds" => GmtOffSetMilliseconds,
            "ExDividendDate" => ExDividendDate,
            "LastFiscalYearEnd" => LastFiscalYearEnd,
            "MostRecentQuarter" => MostRecentQuarter,
            "TargetHighPrice" or "Target High" => TargetHighPrice,
            "TargetLowPrice" or "Target Low" => TargetLowPrice,
            "TargetMeanPrice" or "Target Mean" => TargetMeanPrice,
            "TargetMedianPrice" or "Target Median" => TargetMedianPrice,
            "RecommendationKey" => RecommendationKey,
            "RecommendationMean" => RecommendationMean,
            "NumberOfAnalystOpinions" => NumberOfAnalystOpinions,
            "LastUpdatedUtc" => LastUpdatedUtc,
            "MetadataLastUpdated" => MetadataLastUpdated,
            "Tag" => Tag,
            "Long" => Long,
            "ExitLong" or "Exit Long" or "TP Long" or "Take Profit Long" => ExitLong,
            "StopLossLong" or "Stop Loss Long" or "SL Long" => StopLossLong,
            "Short" => Short,
            "ExitShort" or "Exit Short" or "TP Short" or "Take Profit Short" => ExitShort,
            "StopLossShort" or "Stop Loss Short" or "SL Short" => StopLossShort,
            "EntryPrice" => Long,
            "TargetPrice" => ExitLong,
            "StopLoss" => StopLossLong,
            "IsLong" => IsLong,
            "IsTPLong" => IsTPLong,
            "IsSLLong" => IsSLLong,
            "IsShort" => IsShort,
            "IsTPShort" => IsTPShort,
            "IsSLShort" => IsSLShort,
            _ => DynamicIndicatorValues.TryGetValue(memberName, out var dynamicVal) ? dynamicVal as IComparable : GetRawValueReflectionFallback(memberName)
        };

        private static readonly (System.Reflection.PropertyInfo Prop, string CleanedName)[] _preNormalizedWatchlistProperties =
            Array.ConvertAll(
                typeof(WatchlistItemViewModel).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
                p => (p, NormalizePropertyName(p.Name)));

        private static string NormalizePropertyName(string s)
        {
            return s.Replace(" ", "").Replace("-", "").Replace("_", "").Replace("/", "").Replace("%", "").Replace("(TTM)", "").Replace("Ratio", "").ToLowerInvariant();
        }

        private IComparable? GetRawValueReflectionFallback(string memberName)
        {
            if (string.IsNullOrWhiteSpace(memberName)) return null;

            try
            {
                var prop = _watchlistPropertyCache.GetOrAdd(memberName, key =>
                {
                    string cleanedTarget = NormalizePropertyName(key);
                    for (int i = 0; i < _preNormalizedWatchlistProperties.Length; i++)
                    {
                        var entry = _preNormalizedWatchlistProperties[i];
                        if (entry.CleanedName == cleanedTarget || entry.Prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            return entry.Prop;
                        }
                    }
                    return null;
                });

                if (prop != null)
                {
                    var val = prop.GetValue(this);
                    if (val is IComparable comp) return comp;
                }
            }
            catch { }

            return null;
        }
    }
}
