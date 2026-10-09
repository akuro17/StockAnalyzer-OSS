namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>The one divisor between a whole-number percent and a fraction (holdout share, weight cap). Not part of the pinned contract hash.</summary>
internal static class RlPercent
{
    public const int Divisor = 100;
    public const decimal DecimalDivisor = Divisor;
}
