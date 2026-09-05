using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDmaParameter : CoreIndicatorParameterBase
{
    private int _period = 20;

    [DisplayName("Period")]
    [Description("Calculation period for Displaced Moving Average.")]
    [CoreParameterRange(1, 1000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _displacement = 5;

    [DisplayName("Displacement")]
    [Description("Number of bars to displace the moving average forward or backward.")]
    [CoreParameterRange(-1000, 1000)]
    public int Displacement
    {
        get => _displacement;
        set => SetProperty(ref _displacement, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Displacement})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
