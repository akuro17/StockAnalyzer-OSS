using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSmiParameter : CoreIndicatorParameterBase
{
    private int _period = 14;

    [DisplayName("Period")]
    [Description("Stochastic Momentum Index calculation period.")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _smooth1 = 5;

    [DisplayName("First Smoothing Period")]
    [Description("First EMA smoothing period for SMI.")]
    [CoreParameterRange(1, 1000)]
    public int Smooth1
    {
        get => _smooth1;
        set => SetProperty(ref _smooth1, value);
    }

    private int _smooth2 = 3;

    [DisplayName("Second Smoothing Period")]
    [Description("Second EMA smoothing period for SMI.")]
    [CoreParameterRange(1, 1000)]
    public int Smooth2
    {
        get => _smooth2;
        set => SetProperty(ref _smooth2, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Smooth1}, {Smooth2})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Smooth1 <= 0) throw new ArgumentOutOfRangeException(nameof(Smooth1));
        if (Smooth2 <= 0) throw new ArgumentOutOfRangeException(nameof(Smooth2));
    }
}
