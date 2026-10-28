namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>The one divisor between a whole-number percent and a fraction (holdout share, weight cap, the percent shown for a weight fraction). Public so that no layer defines a second one; not part of the pinned contract hash.</summary>
public static class RlPercent
{
    public const int Divisor = 100;
    public const decimal DecimalDivisor = Divisor;
}
