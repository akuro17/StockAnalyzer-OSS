namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// Represents metadata for a ticker, such as sector and industry information.
/// Defined as a readonly record struct for high-performance zero-allocation use.
/// </summary>
public readonly record struct TickerMetadata(
    string ShortName, 
    string LongName,
    string Region, 
    string Sector, 
    string Industry, 
    string Currency,
    decimal? CurrentPrice = null,
    decimal? LastClose = null)
{
    private static readonly TickerMetadata _unknown = new("", "", "", "", "", "");

    /// <summary>
    /// Gets a default instance for unknown tickers.
    /// </summary>
    public static TickerMetadata Unknown => _unknown;

    // Fundamentals
    public decimal? ReturnOnEquity { get; init; } = null;
    public decimal? ReturnOnAssets { get; init; } = null;
    public decimal? GrossMargins { get; init; } = null;
    public decimal? OperatingMargins { get; init; } = null;
    public decimal? ProfitMargins { get; init; } = null;
    public decimal? CurrentRatio { get; init; } = null;
    public decimal? DebtToEquity { get; init; } = null;
    public decimal? Ebitda { get; init; } = null;
    public decimal? FreeCashflow { get; init; } = null;
    public decimal? OperatingCashflow { get; init; } = null;
    public decimal? TrailingPE { get; init; } = null;
    public decimal? ForwardPE { get; init; } = null;
    public decimal? PriceToBook { get; init; } = null;
    public decimal? TrailingEps { get; init; } = null;
    public decimal? ForwardEps { get; init; } = null;
    public decimal? BookValue { get; init; } = null;
    public decimal? SharesOutstanding { get; init; } = null;
    public decimal? FloatShares { get; init; } = null;
    public decimal? ShortRatio { get; init; } = null;
    public decimal? ShortPercentOfFloat { get; init; } = null;
    public decimal? HeldPercentInsiders { get; init; } = null;
    public decimal? HeldPercentInstitutions { get; init; } = null;
    public string? LongBusinessSummary { get; init; } = null;
    public long? FullTimeEmployees { get; init; } = null;
    public decimal? FiftyTwoWeekHigh { get; init; } = null;
    public decimal? FiftyTwoWeekLow { get; init; } = null;
    public decimal? RevenueGrowth { get; init; } = null;
    public decimal? EarningsGrowth { get; init; } = null;
    public decimal? EnterpriseValue { get; init; } = null;
    public decimal? EnterpriseToEbitda { get; init; } = null;
    public decimal? Beta { get; init; } = null;
    public decimal? PayoutRatio { get; init; } = null;
    public decimal? DividendRate { get; init; } = null;
    public decimal? DividendYield { get; init; } = null;
    public decimal? TotalDebt { get; init; } = null;
    public decimal? TotalCash { get; init; } = null;
    public decimal? TotalRevenue { get; init; } = null;
    public decimal? MarketCap { get; init; } = null;

    // Derived Metrics
    public decimal? PbrCalculated { get; init; } = null;
    public decimal? DividendYieldCalculated { get; init; } = null;
    public decimal? EarningsYield { get; init; } = null;
    public decimal? FcfYield { get; init; } = null;
    public decimal? FcfMargin { get; init; } = null;
    public decimal? NetDebt { get; init; } = null;
    public decimal? NetDebtToEbitda { get; init; } = null;
    public decimal? DividendCoverage { get; init; } = null;
    public decimal? PctFromFiftyTwoWeekHigh { get; init; } = null;
    public decimal? FloatRatio { get; init; } = null;
    public decimal? MarketCapPerEmployee { get; init; } = null;
    public decimal? PegRatio { get; init; } = null;
    public decimal? OperatingCashFlowYield { get; init; } = null;
    public decimal? NetCashRatio { get; init; } = null;

    // Additional raw fields from webai.txt
    public decimal? PriceToSalesTrailing12Months { get; init; } = null;
    public decimal? EnterpriseToRevenue { get; init; } = null;
    public decimal? EbitdaMargins { get; init; } = null;
    public decimal? QuickRatio { get; init; } = null;
    public decimal? AverageVolume { get; init; } = null;

    // New custom derived metrics
    public decimal? PriceToCashFlowRatio { get; init; } = null;
    public decimal? NetDebtEquityRatio { get; init; } = null;
    public decimal? FiftyTwoWeekRangePosition { get; init; } = null;
    public decimal? DailyTurnoverRate { get; init; } = null;
    public decimal? AverageTurnoverRate { get; init; } = null;
    public decimal? DailyFloatShareTurnoverRatio { get; init; } = null;
    public decimal? AverageFloatTurnover { get; init; } = null;

    // New yfinance metadata fields
    public string? QuoteType { get; init; } = null;
    public string? ExchangeTimezoneName { get; init; } = null;
    public long? GmtOffSetMilliseconds { get; init; } = null;
    public long? ExDividendDate { get; init; } = null;
    public long? LastFiscalYearEnd { get; init; } = null;
    public long? MostRecentQuarter { get; init; } = null;
    public decimal? TargetHighPrice { get; init; } = null;
    public decimal? TargetLowPrice { get; init; } = null;
    public decimal? TargetMeanPrice { get; init; } = null;
    public decimal? TargetMedianPrice { get; init; } = null;
    public string? RecommendationKey { get; init; } = null;
    public decimal? RecommendationMean { get; init; } = null;
    public long? NumberOfAnalystOpinions { get; init; } = null;

    // Timestamp
    public DateTime? MetadataLastUpdated { get; init; } = null;

    // User-defined Tag & Strategy Metadata
    public string? Tag { get; init; } = null;
    public decimal? Long { get; init; } = null;
    public decimal? ExitLong { get; init; } = null;
    public decimal? StopLossLong { get; init; } = null;
    public decimal? Short { get; init; } = null;
    public decimal? ExitShort { get; init; } = null;
    public decimal? StopLossShort { get; init; } = null;
    public decimal? EntryPrice { get; init; } = null;
    public decimal? TargetPrice { get; init; } = null;
    public decimal? StopLoss { get; init; } = null;
    public string? Notes { get; init; } = null;

    // Signal Flags
    public bool? IsLong { get; init; } = null;
    public bool? IsTPLong { get; init; } = null;
    public bool? IsSLLong { get; init; } = null;
    public bool? IsShort { get; init; } = null;
    public bool? IsTPShort { get; init; } = null;
    public bool? IsSLShort { get; init; } = null;
}
