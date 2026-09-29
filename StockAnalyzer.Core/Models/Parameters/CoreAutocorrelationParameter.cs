using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public enum AutocorrelationPeakSelectionMode
{
    [Description("Maximum Correlation (Dominant)")]
    MaxCorrelation = 0,

    [Description("First Peak (Shortest Cycle)")]
    FirstPeak = 1
}

public enum AutocorrelationFallbackPolicy
{
    [Description("Hold Previous Period")]
    HoldPrevious = 0,

    [Description("Static Default Period")]
    StaticDefault = 1
}

public class CoreAutocorrelationParameter : CoreIndicatorParameterBase
{
    private int _period = 30;
    private int _lag = 0;
    private int _minLag = 5;
    private int _maxLag = 50;
    private double _threshold = 0.30;
    private double _minProminence = 0.01;
    private int _smoothingPeriod = 5;
    private int _fallbackPeriod = 20;
    private AutocorrelationPeakSelectionMode _selectionMode = AutocorrelationPeakSelectionMode.MaxCorrelation;
    private AutocorrelationFallbackPolicy _fallbackPolicy = AutocorrelationFallbackPolicy.HoldPrevious;
    private int _maxHoldBars = 5;
    private CorrelationCalculationMode _calculationMode = CorrelationCalculationMode.PriceLevel;
    private PriceType _priceSource = PriceType.Close;

    [Category("1. Basic Settings")]
    [CoreParameterRange(10, 500)]
    [Range(10, 500)]
    [DisplayName("Period")]
    [Description("Calculation window size (bars) used to evaluate autocorrelation. Total warmup required is Period + MaxLag bars.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [Browsable(false)]
    public int WindowSize
    {
        get => Period;
        set => Period = value;
    }

    [Category("1. Basic Settings")]
    [CoreParameterRange(0, 200)]
    [Range(0, 200)]
    [DisplayName("Lag")]
    [Description("Specific lag period (bars) for autocorrelation. Set to 0 for automatic dominant cycle detection (Auto Mode).")]
    public int Lag
    {
        get => _lag;
        set => SetProperty(ref _lag, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Price Source")]
    [Description("Price source to use for autocorrelation calculation.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Calculation Mode")]
    [Description("Calculation basis: Price Level (raw prices) or Log Return (logarithmic returns). Note: Log Return is recommended for cycle detection to prevent spurious correlations from trends.")]
    public CorrelationCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => SetProperty(ref _calculationMode, value);
    }

    [Category("2. Cycle Detection (Lag=0)")]
    [CoreParameterRange(2, 50)]
    [Range(2, 50)]
    [DisplayName("Minimum Lag")]
    [Description("Minimum cycle period (bars) to evaluate for peak detection.")]
    public int MinLag
    {
        get => _minLag;
        set => SetProperty(ref _minLag, value);
    }

    [Category("2. Cycle Detection (Lag=0)")]
    [CoreParameterRange(5, 200)]
    [Range(5, 200)]
    [DisplayName("Maximum Lag")]
    [Description("Maximum cycle period (bars) to evaluate for peak detection.")]
    public int MaxLag
    {
        get => _maxLag;
        set => SetProperty(ref _maxLag, value);
    }

    [Category("2. Cycle Detection (Lag=0)")]
    [CoreParameterRange(0.0, 1.0)]
    [Range(0.0, 1.0)]
    [DisplayName("Correlation Threshold")]
    [Description("Minimum correlation coefficient required to accept a peak as a valid cycle.")]
    public double Threshold
    {
        get => _threshold;
        set => SetProperty(ref _threshold, value);
    }

    [Category("2. Cycle Detection (Lag=0)")]
    [CoreParameterRange(0.0, 0.5)]
    [Range(0.0, 0.5)]
    [DisplayName("Minimum Peak Prominence")]
    [Description("Minimum prominence (height above neighboring lag correlations) required for a peak to be considered a cycle.")]
    public double MinProminence
    {
        get => _minProminence;
        set => SetProperty(ref _minProminence, value);
    }

    [Category("2. Cycle Detection (Lag=0)")]
    [CoreParameterRange(1, 50)]
    [Range(1, 50)]
    [DisplayName("Smoothing Period")]
    [Description("EMA smoothing period applied to the extracted cycle period series.")]
    public int SmoothingPeriod
    {
        get => _smoothingPeriod;
        set => SetProperty(ref _smoothingPeriod, value);
    }

    [Category("2. Cycle Detection (Lag=0)")]
    [DisplayName("Peak Selection Mode")]
    [Description("Criterion for choosing between multiple autocorrelation peaks.")]
    public AutocorrelationPeakSelectionMode SelectionMode
    {
        get => _selectionMode;
        set => SetProperty(ref _selectionMode, value);
    }

    [Category("3. Fallback Policy")]
    [CoreParameterRange(2, 200)]
    [Range(2, 200)]
    [DisplayName("Fallback Cycle Period")]
    [Description("Fallback cycle period (bars) used when no valid cycle is detected.")]
    public int FallbackPeriod
    {
        get => _fallbackPeriod;
        set => SetProperty(ref _fallbackPeriod, value);
    }

    [Browsable(false)]
    public int DefaultPeriod
    {
        get => FallbackPeriod;
        set => FallbackPeriod = value;
    }

    [Category("3. Fallback Policy")]
    [DisplayName("Fallback Policy")]
    [Description("Behavior when no autocorrelation peak meets the threshold.")]
    public AutocorrelationFallbackPolicy FallbackPolicy
    {
        get => _fallbackPolicy;
        set => SetProperty(ref _fallbackPolicy, value);
    }

    [Category("3. Fallback Policy")]
    [CoreParameterRange(1, 50)]
    [Range(1, 50)]
    [DisplayName("Max Hold Bars")]
    [Description("Maximum number of bars to hold previous valid period before falling back to default.")]
    public int MaxHoldBars
    {
        get => _maxHoldBars;
        set => SetProperty(ref _maxHoldBars, value);
    }

    public override string GetDisplayName(string type) =>
        Lag > 0
            ? $"{type} ({Period}, Lag={Lag})"
            : $"{type} ({Period}, {MinLag}-{MaxLag})";

    public override void Validate()
    {
        if (Period < 10)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be >= 10");
        if (Lag < 0)
            throw new ArgumentOutOfRangeException(nameof(Lag), "Lag must be >= 0");
        if (MinLag < 2)
            throw new ArgumentOutOfRangeException(nameof(MinLag), "MinLag must be >= 2");
        if (MaxLag <= MinLag)
            throw new ArgumentOutOfRangeException(nameof(MaxLag), "MaxLag must be greater than MinLag");
        if (Threshold < 0.0 || Threshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(Threshold), "Threshold must be between 0.0 and 1.0");
        if (MinProminence < 0.0 || MinProminence > 0.5)
            throw new ArgumentOutOfRangeException(nameof(MinProminence), "MinProminence must be between 0.0 and 0.5");
        if (SmoothingPeriod < 1)
            throw new ArgumentOutOfRangeException(nameof(SmoothingPeriod), "SmoothingPeriod must be >= 1");
        if (FallbackPeriod < 2)
            throw new ArgumentOutOfRangeException(nameof(FallbackPeriod), "FallbackPeriod must be >= 2");
        if (MaxHoldBars < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxHoldBars), "MaxHoldBars must be >= 1");
    }
}

/// <summary>
/// Backward-compatible alias for CoreAutocorrelationParameter.
/// </summary>
public class CoreAutocorrelationPeriodParameter : CoreAutocorrelationParameter
{
}
