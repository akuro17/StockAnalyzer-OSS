namespace StockAnalyzer.Core.Models.Screener;

public static class ScreenerGroupNames
{
    // Indicator Groups
    public const string Price = "Price";

    // Column Groups
    public const string Basic = "Basic";
    public const string PriceVolume = "Price/Volume";
    public const string Valuation = "Valuation";
    public const string Profitability = "Profitability";
    public const string FinancialHealth = "Financial Health";
    public const string Performance = "Performance";
    public const string Solvency = "Solvency";
    public const string ManagementGrowth = "Management/Growth";
    public const string ShortOwnership = "Short/Ownership";

    // Criteria Groups
    public const string PatternRecognition = "Pattern Recognition";
    public const string MarketStructure = "Market Structure";
    public const string Harmonic = "Harmonic";
    public const string TradingRules = "Trading Rules";
    public const string WaveTheory = "Wave Theory";
    public const string CandlestickPatterns = "Candlestick Patterns";

    public static int GetGroupSortOrder(string? groupName) => groupName switch
    {
        Price => 0,
        Basic => 1,
        PriceVolume => 2,
        Valuation => 3,
        Profitability => 4,
        FinancialHealth => 5,
        ManagementGrowth => 6,
        ShortOwnership => 7,
        PatternRecognition => 8,
        MarketStructure => 9,
        Harmonic => 10,
        TradingRules => 11,
        WaveTheory => 12,
        CandlestickPatterns => 13,
        _ => 14
    };
}
