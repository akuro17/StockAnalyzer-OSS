using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreUltimateOscillatorParameter : CoreIndicatorParameterBase
{
    private int _period1 = 7;

    [DisplayName("Short Period")]
    [Description("Short lookback period (typically 7).")]
    [CoreParameterRange(1, 1000)]
    public int Period1
    {
        get => _period1;
        set => SetProperty(ref _period1, value);
    }

    private int _period2 = 14;

    [DisplayName("Medium Period")]
    [Description("Medium lookback period (typically 14).")]
    [CoreParameterRange(1, 1000)]
    public int Period2
    {
        get => _period2;
        set => SetProperty(ref _period2, value);
    }

    private int _period3 = 28;

    [DisplayName("Long Period")]
    [Description("Long lookback period (typically 28).")]
    [CoreParameterRange(1, 1000)]
    public int Period3
    {
        get => _period3;
        set => SetProperty(ref _period3, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period1}, {Period2}, {Period3})";

    public override void Validate()
    {
        if (Period1 <= 0) throw new ArgumentOutOfRangeException(nameof(Period1));
        if (Period2 <= 0) throw new ArgumentOutOfRangeException(nameof(Period2));
        if (Period3 <= 0) throw new ArgumentOutOfRangeException(nameof(Period3));
    }
}
