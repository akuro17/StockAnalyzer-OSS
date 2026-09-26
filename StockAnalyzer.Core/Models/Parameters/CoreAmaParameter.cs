using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAmaParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.AmaPeriod;

    [DisplayName("Period")]
    [Description("Efficiency Ratio (ER) lookback period for AMA.")]
    [CoreParameterRange(2, 10000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _fast = IndicatorDefaultConstants.AmaFastPeriod;

    [DisplayName("Fast EMA Period")]
    [Description("Fastest EMA constant period for AMA.")]
    [CoreParameterRange(1, 1000)]
    public int Fast
    {
        get => _fast;
        set => SetProperty(ref _fast, value);
    }

    private int _slow = IndicatorDefaultConstants.AmaSlowPeriod;

    [DisplayName("Slow EMA Period")]
    [Description("Slowest EMA constant period for AMA.")]
    [CoreParameterRange(2, 10000)]
    public int Slow
    {
        get => _slow;
        set => SetProperty(ref _slow, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Fast}, {Slow})";

    public override void Validate()
    {
        if (Period < 2 || Period > 10000) throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 2 and 10000.");
        if (Fast < 1 || Fast > 1000) throw new ArgumentOutOfRangeException(nameof(Fast), "Fast period must be between 1 and 1000.");
        if (Slow < 2 || Slow > 10000) throw new ArgumentOutOfRangeException(nameof(Slow), "Slow period must be between 2 and 10000.");
        if (Fast >= Slow) throw new ArgumentException("Fast period must be strictly less than Slow period.", nameof(Fast));
    }
}
