using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAdaptiveRsiParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.AdaptiveRsiDefaultWindowSize;
    private int _defaultPeriod = IndicatorDefaultConstants.AdaptiveRsiDefaultPeriod;
    private int _minPeriod = IndicatorDefaultConstants.AdaptiveRsiMinPeriod;
    private int _maxPeriod = IndicatorDefaultConstants.AdaptiveRsiMaxPeriod;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for FFT dominant-cycle detection.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    [CoreParameterRange(2, 100)]
    [Range(2, 100)]
    [DisplayName("Default Period")]
    [Description("Fallback smoothing period when FFT dominant cycle is unavailable.")]
    public int DefaultPeriod
    {
        get => _defaultPeriod;
        set => SetProperty(ref _defaultPeriod, value);
    }

    [CoreParameterRange(2, 50)]
    [Range(2, 50)]
    [DisplayName("Min Period")]
    [Description("Lower bound clamp for dynamic period.")]
    public int MinPeriod
    {
        get => _minPeriod;
        set => SetProperty(ref _minPeriod, value);
    }

    [CoreParameterRange(10, 200)]
    [Range(10, 200)]
    [DisplayName("Max Period")]
    [Description("Upper bound clamp for dynamic period.")]
    public int MaxPeriod
    {
        get => _maxPeriod;
        set => SetProperty(ref _maxPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {DefaultPeriod})";

    public override void Validate()
    {
        if (WindowSize < 4 || WindowSize > 512)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be between 4 and 512");
        if (DefaultPeriod < 2 || DefaultPeriod > 100)
            throw new ArgumentOutOfRangeException(nameof(DefaultPeriod), "DefaultPeriod must be between 2 and 100");
        if (MinPeriod < 2 || MinPeriod > MaxPeriod)
            throw new ArgumentOutOfRangeException(nameof(MinPeriod), "MinPeriod must be >= 2 and <= MaxPeriod");
        if (MaxPeriod < MinPeriod || MaxPeriod > 200)
            throw new ArgumentOutOfRangeException(nameof(MaxPeriod), "MaxPeriod must be >= MinPeriod and <= 200");
    }
}
