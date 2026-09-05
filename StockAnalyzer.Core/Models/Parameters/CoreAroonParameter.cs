using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAroonParameter : CoreIndicatorParameterBase
{
    private int _period = 25;

    [DisplayName("Period")]
    [Description("Calculation period for Aroon Oscillator.")]
    [CoreParameterRange(1, 1000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
