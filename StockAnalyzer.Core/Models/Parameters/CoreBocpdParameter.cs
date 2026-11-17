using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreBocpdParameter : CoreIndicatorParameterBase
{
    private double _hazardLambda = IndicatorDefaultConstants.BocpdDefaultHazardLambda;

    [CoreParameterRange(IndicatorDefaultConstants.BocpdMinHazardLambda, IndicatorDefaultConstants.BocpdMaxHazardLambda)]
    [DisplayName("Hazard Lambda")]
    [Description("Expected run length between regime shifts (hazard H = 1 / lambda).")]
    [Category("Model")]
    public double HazardLambda
    {
        get => _hazardLambda;
        set => SetProperty(ref _hazardLambda, value);
    }

    private int _maxRunLength = IndicatorDefaultConstants.BocpdDefaultMaxRunLength;

    [CoreParameterRange(IndicatorDefaultConstants.BocpdMinMaxRunLength, IndicatorDefaultConstants.BocpdMaxMaxRunLength)]
    [DisplayName("Max Run Length")]
    [Description("Truncation horizon for run-length posterior mass.")]
    [Category("Model")]
    public int MaxRunLength
    {
        get => _maxRunLength;
        set => SetProperty(ref _maxRunLength, value);
    }

    private int _warmupPeriod = IndicatorDefaultConstants.BocpdDefaultWarmupPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.BocpdMinWarmupPeriod, IndicatorDefaultConstants.BocpdMaxWarmupPeriod)]
    [DisplayName("Warmup Period")]
    [Description("Causal rolling window (bars) used to Z-standardize log returns.")]
    [Category("Periods")]
    public int WarmupPeriod
    {
        get => _warmupPeriod;
        set => SetProperty(ref _warmupPeriod, value);
    }

    private double _cusumDrift = IndicatorDefaultConstants.BocpdDefaultCusumDrift;

    [CoreParameterRange(IndicatorDefaultConstants.BocpdMinCusumDrift, IndicatorDefaultConstants.BocpdMaxCusumDrift)]
    [DisplayName("CUSUM Drift (k)")]
    [Description("Reference allowance drift k for two-sided recursive CUSUM.")]
    [Category("CUSUM")]
    public double CusumDrift
    {
        get => _cusumDrift;
        set => SetProperty(ref _cusumDrift, value);
    }

    private double _cusumThreshold = IndicatorDefaultConstants.BocpdDefaultCusumThreshold;

    [CoreParameterRange(IndicatorDefaultConstants.BocpdMinCusumThreshold, IndicatorDefaultConstants.BocpdMaxCusumThreshold)]
    [DisplayName("CUSUM Threshold (h)")]
    [Description("Reset threshold h for cumulative upward/downward CUSUM scores.")]
    [Category("CUSUM")]
    public double CusumThreshold
    {
        get => _cusumThreshold;
        set => SetProperty(ref _cusumThreshold, value);
    }

    private PriceType _priceSource = PriceType.Close;

    [DisplayName("Price Source")]
    [Description("Price field used for computing log returns.")]
    [Category("Input")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WarmupPeriod})";

    public override int GetRequiredWarmupBars() => WarmupPeriod;

    public override void Validate()
    {
        ThrowIfDoubleOutOfRange(HazardLambda, IndicatorDefaultConstants.BocpdMinHazardLambda, IndicatorDefaultConstants.BocpdMaxHazardLambda, nameof(HazardLambda));
        ThrowIfOutOfRange(MaxRunLength, IndicatorDefaultConstants.BocpdMinMaxRunLength, IndicatorDefaultConstants.BocpdMaxMaxRunLength, nameof(MaxRunLength));
        ThrowIfOutOfRange(WarmupPeriod, IndicatorDefaultConstants.BocpdMinWarmupPeriod, IndicatorDefaultConstants.BocpdMaxWarmupPeriod, nameof(WarmupPeriod));
        ThrowIfDoubleOutOfRange(CusumDrift, IndicatorDefaultConstants.BocpdMinCusumDrift, IndicatorDefaultConstants.BocpdMaxCusumDrift, nameof(CusumDrift));
        ThrowIfDoubleOutOfRange(CusumThreshold, IndicatorDefaultConstants.BocpdMinCusumThreshold, IndicatorDefaultConstants.BocpdMaxCusumThreshold, nameof(CusumThreshold));
    }

    private static void ThrowIfDoubleOutOfRange(double value, double min, double max, string parameterName)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
