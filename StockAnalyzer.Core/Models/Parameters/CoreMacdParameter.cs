using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMacdParameter : CoreIndicatorParameterBase
{
    private int _shortPeriod = 12;
    private int _longPeriod = 26;
    private int _signalPeriod = 9;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Short Period")]
    [Description("Short-term EMA period.")]
    [Category("Periods")]
    public int ShortPeriod 
    { 
        get => _shortPeriod; 
        set => SetProperty(ref _shortPeriod, value); 
    }

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Long Period")]
    [Description("Long-term EMA period.")]
    [Category("Periods")]
    public int LongPeriod 
    { 
        get => _longPeriod; 
        set => SetProperty(ref _longPeriod, value); 
    }

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Signal Period")]
    [Description("Signal line EMA period.")]
    [Category("Periods")]
    public int SignalPeriod 
    { 
        get => _signalPeriod; 
        set => SetProperty(ref _signalPeriod, value); 
    }

    /// <summary>
    /// Matches CoreMacdIndicator's firstValidSignalIndex = (LongPeriod - 1) + SignalPeriod - 1,
    /// plus 1 bar of margin (consistent with the single-Period convention elsewhere).
    /// </summary>
    public override int GetRequiredWarmupBars() => LongPeriod + SignalPeriod - 1;

    public override string GetDisplayName(string type) => $"{type} ({ShortPeriod}, {LongPeriod}, {SignalPeriod})";

    public override void Validate()
    {
        if (ShortPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(ShortPeriod));
        if (LongPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(LongPeriod));
        if (SignalPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(SignalPeriod));
    }
}
