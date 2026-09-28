using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFFTTrendFilterParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.FftTrendFilterDefaultWindowSize;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for the FFT low-pass reconstruction.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _numHarmonics = IndicatorDefaultConstants.FftTrendFilterDefaultNumHarmonics;

    [CoreParameterRange(1, 256)]
    [Range(1, 256)]
    [DisplayName("Num Harmonics")]
    [Description("Number of low-frequency FFT bins (including DC) kept before the inverse transform. Lower values give heavier smoothing.")]
    public int NumHarmonics
    {
        get => _numHarmonics;
        set => SetProperty(ref _numHarmonics, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {NumHarmonics})";

    public override void Validate()
    {
        if (WindowSize <= 0 || WindowSize > 512)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be between 1 and 512");
        if (NumHarmonics <= 0 || NumHarmonics > 256)
            throw new ArgumentOutOfRangeException(nameof(NumHarmonics), "NumHarmonics must be between 1 and 256");
    }
}
