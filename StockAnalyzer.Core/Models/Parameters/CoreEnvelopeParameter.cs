using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEnvelopeParameter : CoreIndicatorParameterBase
{
    private int _period = 20;

    [DisplayName("Period")]
    [Description("Moving average period for envelope center line.")]
    [CoreParameterRange(1, 1000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private decimal _deviation = 0.025m;

    [DisplayName("Deviation")]
    [Description("Percentage band deviation from the moving average.")]
    [CoreParameterRange(0, 1.0)]
    public decimal Deviation
    {
        get => _deviation;
        set => SetProperty(ref _deviation, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Deviation:P1})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Deviation < 0) throw new ArgumentOutOfRangeException(nameof(Deviation));
    }
}
