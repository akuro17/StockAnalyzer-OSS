using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFFTCycleParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.FftCycleDefaultWindowSize;

    [CoreParameterRange(IndicatorDefaultConstants.FftCycleMinWindowSize, IndicatorDefaultConstants.FftCycleMaxWindowSize)]
    [Range(IndicatorDefaultConstants.FftCycleMinWindowSize, IndicatorDefaultConstants.FftCycleMaxWindowSize)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for Hanning-window/FFT dominant-cycle detection. Adjusted internally to the nearest power of two (an exact tie rounds down), e.g. 100 becomes 128 and 96 becomes 64.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize})";

    public override void Validate()
    {
        if (WindowSize < IndicatorDefaultConstants.FftCycleMinWindowSize || WindowSize > IndicatorDefaultConstants.FftCycleMaxWindowSize)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), $"WindowSize must be between {IndicatorDefaultConstants.FftCycleMinWindowSize} and {IndicatorDefaultConstants.FftCycleMaxWindowSize}");
    }
}
