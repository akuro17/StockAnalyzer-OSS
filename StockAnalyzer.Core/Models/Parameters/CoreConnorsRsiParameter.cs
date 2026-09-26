using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreConnorsRsiParameter : CoreIndicatorParameterBase
{
    private int _rsiPeriod = 3;

    [DisplayName("RSI Period")]
    [Description("Calculation period for standard RSI.")]
    [CoreParameterRange(1, 1000)]
    public int RsiPeriod
    {
        get => _rsiPeriod;
        set => SetProperty(ref _rsiPeriod, value);
    }

    private int _streakPeriod = 2;

    [DisplayName("Streak Period")]
    [Description("Calculation period for Up/Down Streak RSI.")]
    [CoreParameterRange(1, 1000)]
    public int StreakPeriod
    {
        get => _streakPeriod;
        set => SetProperty(ref _streakPeriod, value);
    }

    private int _percentRankPeriod = 100;

    [DisplayName("Percent Rank Period")]
    [Description("Lookback period for Percent Rank calculation.")]
    [CoreParameterRange(1, 10000)]
    public int PercentRankPeriod
    {
        get => _percentRankPeriod;
        set => SetProperty(ref _percentRankPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({RsiPeriod}, {StreakPeriod}, {PercentRankPeriod})";

    public override void Validate()
    {
        if (RsiPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(RsiPeriod));
        if (StreakPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(StreakPeriod));
        if (PercentRankPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(PercentRankPeriod));
    }
}
