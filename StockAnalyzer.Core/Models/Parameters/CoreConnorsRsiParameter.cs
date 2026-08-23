using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreConnorsRsiParameter : CoreIndicatorParameterBase
{
    [DisplayName("RSI Period")]
    [Description("Calculation period for standard RSI.")]
    [CoreParameterRange(1, 1000)]
    public int RsiPeriod { get; set; } = 3;

    [DisplayName("Streak Period")]
    [Description("Calculation period for Up/Down Streak RSI.")]
    [CoreParameterRange(1, 1000)]
    public int StreakPeriod { get; set; } = 2;

    [DisplayName("Percent Rank Period")]
    [Description("Lookback period for Percent Rank calculation.")]
    [CoreParameterRange(1, 10000)]
    public int PercentRankPeriod { get; set; } = 100;

    public override string GetDisplayName(string type) => $"{type} ({RsiPeriod}, {StreakPeriod}, {PercentRankPeriod})";

    public override void Validate()
    {
        if (RsiPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(RsiPeriod));
        if (StreakPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(StreakPeriod));
        if (PercentRankPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(PercentRankPeriod));
    }
}
