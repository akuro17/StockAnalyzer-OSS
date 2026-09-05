using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMassIndexParameter : CoreIndicatorParameterBase
{
    private int _period = 25;

    [DisplayName("Period")]
    [Description("Overall summation period for Mass Index.")]
    [CoreParameterRange(1, 1000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _emaPeriod = 9;

    [DisplayName("EMA Period")]
    [Description("EMA period for high-low range smoothing.")]
    [CoreParameterRange(1, 1000)]
    public int EmaPeriod
    {
        get => _emaPeriod;
        set => SetProperty(ref _emaPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {EmaPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (EmaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(EmaPeriod));
    }
}
