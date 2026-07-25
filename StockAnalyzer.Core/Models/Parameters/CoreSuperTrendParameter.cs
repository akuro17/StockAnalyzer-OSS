using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSuperTrendParameter : CoreIndicatorParameterBase
{
    private int _period = 10;
    [CoreParameterRange(1, 1000)]
    [DisplayName("Period")]
    [Description("ATR period for SuperTrend.")]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    private decimal _multiplier = 3.0m;
    [CoreParameterRange(0.1, 100.0)]
    [DisplayName("Multiplier")]
    [Description("ATR multiplier.")]
    public decimal Multiplier 
    { 
        get => _multiplier; 
        set => SetProperty(ref _multiplier, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Multiplier})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Multiplier <= 0) throw new ArgumentOutOfRangeException(nameof(Multiplier));
    }
}
