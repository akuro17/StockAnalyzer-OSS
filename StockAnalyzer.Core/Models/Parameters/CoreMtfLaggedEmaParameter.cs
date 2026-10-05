using System;

using System.ComponentModel;
namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMtfLaggedEmaParameter : CoreIndicatorParameterBase
{
    private int _period = 20;
    [CoreParameterRange(1, 10000)]
    [DisplayName("Period")]
    [Description("Base EMA period.")]
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
    [CoreParameterRange(1, 1000)]
    [DisplayName("Lag")]
    [Description("Lag offset (must be >= 1 to prevent lookahead bias from incomplete higher timeframe bars).")]
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
        if (Lag < 1) throw new ArgumentOutOfRangeException(nameof(Lag), "Lag must be >= 1 to ensure completed higher-timeframe bar reference and prevent lookahead bias.");
    }
}
