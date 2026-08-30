using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKamaParameter : CoreIndicatorParameterBase
{
    private int _period = 10;

    [DisplayName("Period")]
    [Description("Efficiency Ratio (ER) lookback period for KAMA.")]
    [CoreParameterRange(1, 10000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _fast = 2;

    [DisplayName("Fast EMA Period")]
    [Description("Fastest EMA constant period for KAMA.")]
    [CoreParameterRange(1, 1000)]
    public int Fast
    {
        get => _fast;
        set => SetProperty(ref _fast, value);
    }

    private int _slow = 30;

    [DisplayName("Slow EMA Period")]
    [Description("Slowest EMA constant period for KAMA.")]
    [CoreParameterRange(1, 10000)]
    public int Slow
    {
        get => _slow;
        set => SetProperty(ref _slow, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Fast}, {Slow})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Fast <= 0) throw new ArgumentOutOfRangeException(nameof(Fast));
        if (Slow <= 0) throw new ArgumentOutOfRangeException(nameof(Slow));
    }
}
