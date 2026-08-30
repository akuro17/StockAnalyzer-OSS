using System;
using StockAnalyzer.Core.Constants;

using System.ComponentModel;
namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMtfLaggedEmaParameter : CoreIndicatorParameterBase
{
    private int _period = 20;
    [CoreParameterRange(1, 10000)]
    [DisplayName("Period")]
    [Description("Base EMA period.")]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    private int _timeFrameMultiplier = 4;
    [CoreParameterRange(1, 100)]
    [DisplayName("Timeframe Multiplier")]
    [Description("Multiplier for the secondary timeframe.")]
    public int TimeFrameMultiplier 
    { 
        get => _timeFrameMultiplier; 
        set => SetProperty(ref _timeFrameMultiplier, value); 
    }

    private int _lag = 1;
    [CoreParameterRange(0, 1000)]
    [DisplayName("Lag")]
    [Description("Lag offset.")]
    public int Lag 
    { 
        get => _lag; 
        set => SetProperty(ref _lag, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {TimeFrameMultiplier}, {Lag})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (TimeFrameMultiplier <= 0) throw new ArgumentOutOfRangeException(nameof(TimeFrameMultiplier));
        if (Lag < 0) throw new ArgumentOutOfRangeException(nameof(Lag));
    }
}
