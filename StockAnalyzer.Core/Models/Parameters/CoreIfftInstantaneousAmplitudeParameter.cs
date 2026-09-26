using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreIfftInstantaneousAmplitudeParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.IfftInstantaneousAmplitudeDefaultWindowSize;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for the IFFT analytic-signal instantaneous amplitude line.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize})";

    public override void Validate()
    {
        if (WindowSize <= 0 || WindowSize > 512)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be between 1 and 512");
    }
}
