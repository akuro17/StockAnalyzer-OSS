using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFRAMAParameter : CoreIndicatorParameterBase
{
    private int _period = 16;

    [DisplayName("Period")]
    [Description("Fractal calculation window (must be an even number).")]
    [CoreParameterRange(2, 200)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period < 2) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
