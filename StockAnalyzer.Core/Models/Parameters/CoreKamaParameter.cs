using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKamaParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.KamaPeriod;

    [DisplayName("Period")]
    [Description("Efficiency Ratio (ER) lookback period for KAMA.")]
    [CoreParameterRange(1, 10000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _fast = IndicatorDefaultConstants.KamaFastPeriod;

    [DisplayName("Fast EMA Period")]
    [Description("Fastest EMA constant period for KAMA.")]
    [CoreParameterRange(1, 1000)]
    public int Fast
    {
        get => _fast;
        set => SetProperty(ref _fast, value);
    }

    private int _slow = IndicatorDefaultConstants.KamaSlowPeriod;

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
        if (Period < 1 || Period > 10000) throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 1 and 10000.");
        if (Fast < 1 || Fast > 1000) throw new ArgumentOutOfRangeException(nameof(Fast), "Fast period must be between 1 and 1000.");
        if (Slow < 1 || Slow > 10000) throw new ArgumentOutOfRangeException(nameof(Slow), "Slow period must be between 1 and 10000.");
    }
}
