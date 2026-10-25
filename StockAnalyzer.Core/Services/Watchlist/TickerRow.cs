using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Watchlist;

/// <summary>
/// Immutable row representation of a ticker and its hydrated metadata/market metrics.
/// Implements <see cref="IFilterableSymbol"/> for filter rule evaluation in Core and MCP.
/// Property names, types, and nullabilities conform to WatchlistItemViewModel for all registry columns.
/// </summary>
public sealed record TickerRow : IFilterableSymbol
{
    // Basic Identifiers
    public string Symbol { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Sector { get; init; } = string.Empty;
    public string Industry { get; init; } = string.Empty;

    // Market / Time-Series Metrics
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long Volume { get; init; }
    public decimal Change { get; init; }
    public double ChangePercent { get; init; }
    public DateTimeOffset? LastUpdatedUtc { get; init; }

    // Fundamentals (from TickerMetadata)
    public decimal? ReturnOnEquity { get; init; }
    public decimal? ReturnOnAssets { get; init; }
    public decimal? GrossMargins { get; init; }
    public decimal? OperatingMargins { get; init; }
    public decimal? ProfitMargins { get; init; }
    public decimal? CurrentRatio { get; init; }
    public decimal? DebtToEquity { get; init; }
    public decimal? Ebitda { get; init; }
    public decimal? FreeCashflow { get; init; }
    public decimal? OperatingCashflow { get; init; }
    public decimal? TrailingPE { get; init; }
    public decimal? ForwardPE { get; init; }
    public decimal? PriceToBook { get; init; }
    public decimal? TrailingEps { get; init; }
    public decimal? ForwardEps { get; init; }
    public decimal? BookValue { get; init; }
    public decimal? SharesOutstanding { get; init; }
    public decimal? FloatShares { get; init; }
    public decimal? ShortRatio { get; init; }
    public decimal? ShortPercentOfFloat { get; init; }
    public decimal? HeldPercentInsiders { get; init; }
    public decimal? HeldPercentInstitutions { get; init; }
    public string? LongBusinessSummary { get; init; }
    public long? FullTimeEmployees { get; init; }
    public decimal? FiftyTwoWeekHigh { get; init; }
    public decimal? FiftyTwoWeekLow { get; init; }
    public decimal? RevenueGrowth { get; init; }
    public decimal? EarningsGrowth { get; init; }
    public decimal? EnterpriseValue { get; init; }
    public decimal? EnterpriseToEbitda { get; init; }
    public decimal? Beta { get; init; }
    public decimal? PayoutRatio { get; init; }
    public decimal? DividendRate { get; init; }
    public decimal? DividendYield { get; init; }
    public decimal? TotalDebt { get; init; }
    public decimal? TotalCash { get; init; }
    public decimal? TotalRevenue { get; init; }
    public decimal? MarketCap { get; init; }

    // Derived Financial & Valuation Metrics
    public decimal? PbrCalculated { get; init; }
    public decimal? DividendYieldCalculated { get; init; }
    public decimal? EarningsYield { get; init; }
    public decimal? FcfYield { get; init; }
    public decimal? FcfMargin { get; init; }
    public decimal? NetDebt { get; init; }
    public decimal? NetDebtToEbitda { get; init; }
    public decimal? DividendCoverage { get; init; }
    public decimal? PctFromFiftyTwoWeekHigh { get; init; }
    public decimal? FloatRatio { get; init; }
    public decimal? MarketCapPerEmployee { get; init; }
    public decimal? PegRatio { get; init; }
    public decimal? OperatingCashFlowYield { get; init; }
    public decimal? NetCashRatio { get; init; }
    public decimal? PriceToSalesTrailing12Months { get; init; }
    public decimal? EnterpriseToRevenue { get; init; }
    public decimal? EbitdaMargins { get; init; }
    public decimal? QuickRatio { get; init; }
    public decimal? AverageVolume { get; init; }
    public decimal? PriceToCashFlowRatio { get; init; }
    public decimal? NetDebtEquityRatio { get; init; }
    public decimal? FiftyTwoWeekRangePosition { get; init; }
    public decimal? DailyTurnoverRate { get; init; }
    public decimal? AverageTurnoverRate { get; init; }
    public decimal? DailyFloatShareTurnoverRatio { get; init; }
    public decimal? AverageFloatTurnover { get; init; }

    // yfinance Metadata Fields
    public long? ExDividendDate { get; init; }
    public string? ExchangeTimezoneName { get; init; }
    public long? GmtOffSetMilliseconds { get; init; }
    public long? LastFiscalYearEnd { get; init; }
    public long? MostRecentQuarter { get; init; }
    public long? NumberOfAnalystOpinions { get; init; }
    public string? QuoteType { get; init; }
    public string? RecommendationKey { get; init; }
    public decimal? RecommendationMean { get; init; }
    public string? Region { get; init; }
    public decimal? TargetHighPrice { get; init; }
    public decimal? TargetLowPrice { get; init; }
    public decimal? TargetMeanPrice { get; init; }
    public decimal? TargetMedianPrice { get; init; }

    // Timestamps
    public DateTime? MetadataLastUpdated { get; init; }

    // User Strategy & Notes
    public string? Tag { get; init; }
    public string? Notes { get; init; }
    public string? Reminder { get; init; }
    public decimal? Long { get; init; }
    public decimal? ExitLong { get; init; }
    public decimal? StopLossLong { get; init; }
    public decimal? Short { get; init; }
    public decimal? ExitShort { get; init; }
    public decimal? StopLossShort { get; init; }

    // Convenience Strategy Aliases (matching WatchlistItemViewModel)
    public decimal? EntryPrice => Long;
    public decimal? TargetPrice => ExitLong;
    public decimal? StopLoss => StopLossLong;

    // Signal Flags
    public bool? IsLong { get; init; }
    public bool? IsTPLong { get; init; }
    public bool? IsSLLong { get; init; }
    public bool? IsShort { get; init; }
    public bool? IsTPShort { get; init; }
    public bool? IsSLShort { get; init; }

    // Property Lookup Table (precomputed for O(1) reflection-free access)
    private static readonly Dictionary<string, Func<TickerRow, object?>> _propertyGetters;
    private static readonly Dictionary<string, Func<TickerRow, object?>> _normalizedGetters;
    private static readonly HashSet<Func<TickerRow, object?>> _candleDependentGetters;

    static TickerRow()
    {
        var getters = new Dictionary<string, Func<TickerRow, object?>>(StringComparer.OrdinalIgnoreCase);
        var normalized = new Dictionary<string, Func<TickerRow, object?>>(StringComparer.OrdinalIgnoreCase);

        // Reflect all public instance readable properties of TickerRow
        foreach (var prop in typeof(TickerRow).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            var capturedProp = prop;
            Func<TickerRow, object?> getter = row => capturedProp.GetValue(row);
            getters[prop.Name] = getter;

            var normKey = NormalizePropertyName(prop.Name);
            if (!normalized.ContainsKey(normKey))
            {
                normalized[normKey] = getter;
            }
        }

        // Register all common aliases from WatchlistItemViewModel.GetRawValue
        void AddAlias(string alias, string targetProperty)
        {
            if (getters.TryGetValue(targetProperty, out var targetGetter))
            {
                getters[alias] = targetGetter;
                var norm = NormalizePropertyName(alias);
                if (!normalized.ContainsKey(norm))
                {
                    normalized[norm] = targetGetter;
                }
            }
        }

        AddAlias("Change %", nameof(ChangePercent));
        AddAlias("ROE", nameof(ReturnOnEquity));
        AddAlias("ROA", nameof(ReturnOnAssets));
        AddAlias("Gross Margin", nameof(GrossMargins));
        AddAlias("Operating Margin", nameof(OperatingMargins));
        AddAlias("Profit Margin", nameof(ProfitMargins));
        AddAlias("D/E Ratio", nameof(DebtToEquity));
        AddAlias("EBITDA", nameof(Ebitda));
        AddAlias("Free Cash Flow", nameof(FreeCashflow));
        AddAlias("Operating Cash Flow", nameof(OperatingCashflow));
        AddAlias("Trailing P/E", nameof(TrailingPE));
        AddAlias("Forward P/E", nameof(ForwardPE));
        AddAlias("P/B Ratio", nameof(PriceToBook));
        AddAlias("Trailing EPS", nameof(TrailingEps));
        AddAlias("EPS", nameof(TrailingEps));
        AddAlias("Forward EPS", nameof(ForwardEps));
        AddAlias("Book Value", nameof(BookValue));
        AddAlias("Book Value Per Share", nameof(BookValue));
        AddAlias("BPS", nameof(BookValue));
        AddAlias("Shares Outstanding", nameof(SharesOutstanding));
        AddAlias("Float Shares", nameof(FloatShares));
        AddAlias("Days to Cover", nameof(ShortRatio));
        AddAlias("Short Ratio", nameof(ShortRatio));
        AddAlias("Short % Float", nameof(ShortPercentOfFloat));
        AddAlias("Short Interest % of Float", nameof(ShortPercentOfFloat));
        AddAlias("Insider Held %", nameof(HeldPercentInsiders));
        AddAlias("Insider Ownership %", nameof(HeldPercentInsiders));
        AddAlias("Inst Held %", nameof(HeldPercentInstitutions));
        AddAlias("Institutional Ownership %", nameof(HeldPercentInstitutions));
        AddAlias("Employees", nameof(FullTimeEmployees));
        AddAlias("Full Time Employees", nameof(FullTimeEmployees));
        AddAlias("52W High", nameof(FiftyTwoWeekHigh));
        AddAlias("52-Week High", nameof(FiftyTwoWeekHigh));
        AddAlias("52W Low", nameof(FiftyTwoWeekLow));
        AddAlias("52-Week Low", nameof(FiftyTwoWeekLow));
        AddAlias("EV", nameof(EnterpriseValue));
        AddAlias("EV/EBITDA", nameof(EnterpriseToEbitda));
        AddAlias("Payout Ratio", nameof(PayoutRatio));
        AddAlias("Dividend Rate", nameof(DividendRate));
        AddAlias("Dividend per Share", nameof(DividendRate));
        AddAlias("Dividend Yield", nameof(DividendYield));
        AddAlias("Total Debt", nameof(TotalDebt));
        AddAlias("Total Cash", nameof(TotalCash));
        AddAlias("Total Revenue", nameof(TotalRevenue));
        AddAlias("Market Cap", nameof(MarketCap));
        AddAlias("P/B (Live)", nameof(PbrCalculated));
        AddAlias("P/B (Calculated)", nameof(PbrCalculated));
        AddAlias("Div Yield (Live)", nameof(DividendYieldCalculated));
        AddAlias("Div Yield (Calc)", nameof(DividendYieldCalculated));
        AddAlias("Earnings Yield", nameof(EarningsYield));
        AddAlias("FCF Yield", nameof(FcfYield));
        AddAlias("FCF Margin", nameof(FcfMargin));
        AddAlias("Net Debt", nameof(NetDebt));
        AddAlias("Net Debt/EBITDA", nameof(NetDebtToEbitda));
        AddAlias("Net Debt / EBITDA", nameof(NetDebtToEbitda));
        AddAlias("Dividend Coverage", nameof(DividendCoverage));
        AddAlias("Div Coverage", nameof(DividendCoverage));
        AddAlias("% Off 52W High", nameof(PctFromFiftyTwoWeekHigh));
        AddAlias("% from 52W High", nameof(PctFromFiftyTwoWeekHigh));
        AddAlias("Float Ratio", nameof(FloatRatio));
        AddAlias("Market Cap/Employee", nameof(MarketCapPerEmployee));
        AddAlias("Market Cap / Employee", nameof(MarketCapPerEmployee));
        AddAlias("PEG", nameof(PegRatio));
        AddAlias("PEG Ratio", nameof(PegRatio));
        AddAlias("OCF Yield", nameof(OperatingCashFlowYield));
        AddAlias("P/S (TTM)", nameof(PriceToSalesTrailing12Months));
        AddAlias("P/S", nameof(PriceToSalesTrailing12Months));
        AddAlias("EV/Revenue", nameof(EnterpriseToRevenue));
        AddAlias("EBITDA Margin", nameof(EbitdaMargins));
        AddAlias("Quick Ratio", nameof(QuickRatio));
        AddAlias("Average Volume", nameof(AverageVolume));
        AddAlias("Avg Volume", nameof(AverageVolume));
        AddAlias("P/CF Ratio", nameof(PriceToCashFlowRatio));
        AddAlias("Net D/E Ratio", nameof(NetDebtEquityRatio));
        AddAlias("52W Range Position", nameof(FiftyTwoWeekRangePosition));
        AddAlias("52W Range Pos.", nameof(FiftyTwoWeekRangePosition));
        AddAlias("Daily Turnover %", nameof(DailyTurnoverRate));
        AddAlias("Turnover Rate", nameof(DailyTurnoverRate));
        AddAlias("Avg Turnover %", nameof(AverageTurnoverRate));
        AddAlias("Avg Turnover Rate", nameof(AverageTurnoverRate));
        AddAlias("Float Turnover Rate", nameof(DailyFloatShareTurnoverRatio));
        AddAlias("Avg Float Turnover", nameof(AverageFloatTurnover));
        AddAlias("Avg Float Turnover Rate", nameof(AverageFloatTurnover));
        AddAlias("Target High", nameof(TargetHighPrice));
        AddAlias("Target Low", nameof(TargetLowPrice));
        AddAlias("Target Mean", nameof(TargetMeanPrice));
        AddAlias("Target Median", nameof(TargetMedianPrice));
        AddAlias("Take Profit Long", nameof(ExitLong));
        AddAlias("TP Long", nameof(ExitLong));
        AddAlias("Stop Loss Long", nameof(StopLossLong));
        AddAlias("SL Long", nameof(StopLossLong));
        AddAlias("Take Profit Short", nameof(ExitShort));
        AddAlias("TP Short", nameof(ExitShort));
        AddAlias("Stop Loss Short", nameof(StopLossShort));
        AddAlias("SL Short", nameof(StopLossShort));

        _propertyGetters = getters;
        _normalizedGetters = normalized;

        string[] candleDependentProps =
        {
            nameof(Open),
            nameof(High),
            nameof(Low),
            nameof(Close),
            nameof(Volume),
            nameof(Change),
            nameof(ChangePercent),
            nameof(LastUpdatedUtc),
            nameof(DailyTurnoverRate),
            nameof(DailyFloatShareTurnoverRatio),
            nameof(MarketCap),
            nameof(PbrCalculated),
            nameof(DividendYieldCalculated),
            nameof(EarningsYield),
            nameof(FcfYield),
            nameof(PctFromFiftyTwoWeekHigh),
            nameof(MarketCapPerEmployee),
            nameof(OperatingCashFlowYield),
            nameof(NetCashRatio),
            nameof(PriceToCashFlowRatio),
            nameof(FiftyTwoWeekRangePosition),
            nameof(TrailingPE),
            nameof(ForwardPE)
        };

        var candleGetters = new HashSet<Func<TickerRow, object?>>(ReferenceEqualityComparer.Instance);
        foreach (var propName in candleDependentProps)
        {
            if (getters.TryGetValue(propName, out var g))
            {
                candleGetters.Add(g);
            }
        }
        _candleDependentGetters = candleGetters;
    }

    private static string NormalizePropertyName(string s)
    {
        return s.Replace(" ", "").Replace("-", "").Replace("_", "").Replace("/", "").Replace("%", "").Replace("(TTM)", "").Replace("Ratio", "").ToLowerInvariant();
    }

    public static bool IsKnownField(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return false;
        if (_propertyGetters.ContainsKey(propertyName)) return true;
        var normalizedKey = NormalizePropertyName(propertyName);
        return _normalizedGetters.ContainsKey(normalizedKey);
    }

    /// <summary>
    /// Returns true if and only if the specified field or alias maps to a property on <see cref="TickerRow"/>
    /// whose value depends on historical candle bars in <see cref="TickerRowCalculator.Compute"/>.
    /// </summary>
    public static bool RequiresCandles(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return false;
        if (_propertyGetters.TryGetValue(propertyName, out var getter))
        {
            return _candleDependentGetters.Contains(getter);
        }
        var normalizedKey = NormalizePropertyName(propertyName);
        if (_normalizedGetters.TryGetValue(normalizedKey, out var normGetter))
        {
            return _candleDependentGetters.Contains(normGetter);
        }
        return false;
    }

    public object? GetPropertyValue(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return null;

        if (_propertyGetters.TryGetValue(propertyName, out var getter))
        {
            return getter(this);
        }

        var normalizedKey = NormalizePropertyName(propertyName);
        if (_normalizedGetters.TryGetValue(normalizedKey, out var normGetter))
        {
            return normGetter(this);
        }

        return null;
    }
}
