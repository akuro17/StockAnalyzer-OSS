using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKeltnerParameter : CoreIndicatorParameterBase
{
    private int _emaPeriod = 20;

    [DisplayName("EMA Period")]
    [Description("Exponential moving average period for center line.")]
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod
    {
        get => _emaPeriod;
        set => SetProperty(ref _emaPeriod, value);
    }

    private int _atrPeriod = 10;

    [DisplayName("ATR Period")]
    [Description("Average True Range calculation period for channel width.")]
    [CoreParameterRange(1, 1000)]
    public int AtrPeriod
    {
        get => _atrPeriod;
        set => SetProperty(ref _atrPeriod, value);
    }

    private decimal _multiplier = 2.0m;

    [DisplayName("Multiplier")]
    [Description("ATR multiplier for channel bands.")]
    [CoreParameterRange(0.1, 10.0)]
    public decimal Multiplier
    {
        get => _multiplier;
        set => SetProperty(ref _multiplier, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({EmaPeriod}, {AtrPeriod}, {Multiplier})";

    public override void Validate()
    {
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
        if (AtrPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(AtrPeriod));
    }
}
