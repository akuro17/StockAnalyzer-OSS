using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreHilbertTrendlineParameter : CoreIndicatorParameterBase
{
    private int _defaultPeriod = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    private int _minPeriod = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    private int _maxPeriod = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    private decimal _smoothBeta = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    private decimal _deltaLimit = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;

    [CoreParameterRange(2, 100)]
    [Range(2, 100)]
    [DisplayName("Default Period")]
    [Description("Fallback dominant cycle period when Hilbert estimation is warming up or invalid.")]
    public int DefaultPeriod
    {
        get => _defaultPeriod;
        set => SetProperty(ref _defaultPeriod, value);
    }

    [CoreParameterRange(2, 50)]
    [Range(2, 50)]
    [DisplayName("Min Period")]
    [Description("Lower bound clamp for estimated cycle period (Ehlers recommended: 6).")]
    public int MinPeriod
    {
        get => _minPeriod;
        set => SetProperty(ref _minPeriod, value);
    }

    [CoreParameterRange(10, 200)]
    [Range(10, 200)]
    [DisplayName("Max Period")]
    [Description("Upper bound clamp for estimated cycle period (Ehlers recommended: 50).")]
    public int MaxPeriod
    {
        get => _maxPeriod;
        set => SetProperty(ref _maxPeriod, value);
    }

    [CoreParameterRange(0.01, 1.0)]
    [Range(0.01, 1.0)]
    [DisplayName("Smooth Beta")]
    [Description("Exponential smoothing weight applied to the raw estimated cycle.")]
    public decimal SmoothBeta
    {
        get => _smoothBeta;
        set => SetProperty(ref _smoothBeta, value);
    }

    [CoreParameterRange(0.5, 20.0)]
    [Range(0.5, 20.0)]
    [DisplayName("Delta Limit")]
    [Description("Maximum allowable period jump per bar to suppress noise spikes.")]
    public decimal DeltaLimit
    {
        get => _deltaLimit;
        set => SetProperty(ref _deltaLimit, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({MinPeriod}-{MaxPeriod})";

    public override void Validate()
    {
        if (DefaultPeriod < MinPeriod || DefaultPeriod > MaxPeriod)
            throw new ArgumentOutOfRangeException(nameof(DefaultPeriod), "DefaultPeriod must be between MinPeriod and MaxPeriod.");

        if (MinPeriod >= MaxPeriod)
            throw new ArgumentOutOfRangeException(nameof(MinPeriod), "MinPeriod must be strictly less than MaxPeriod.");

        if (MinPeriod < 2)
            throw new ArgumentOutOfRangeException(nameof(MinPeriod), "MinPeriod must be at least 2.");

        if (SmoothBeta <= 0m || SmoothBeta > 1m)
            throw new ArgumentOutOfRangeException(nameof(SmoothBeta), "SmoothBeta must be in (0, 1].");

        if (DeltaLimit <= 0m)
            throw new ArgumentOutOfRangeException(nameof(DeltaLimit), "DeltaLimit must be positive.");
    }
}
