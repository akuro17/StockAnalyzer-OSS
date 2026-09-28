using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreJmaParameter : CoreIndicatorParameterBase
{
    private int _period = 7;

    [DisplayName("Period")]
    [Description("Calculation period for Jurik Moving Average.")]
    [CoreParameterRange(1, 10000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private double _phase = 0;

    [DisplayName("Phase")]
    [Description("Phase lag / lead parameter (-100 to +100).")]
    [CoreParameterRange(-100.0, 100.0)]
    public double Phase
    {
        get => _phase;
        set => SetProperty(ref _phase, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Phase})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        // Phase can be negative, standard range often -100 to 100
    }
}
