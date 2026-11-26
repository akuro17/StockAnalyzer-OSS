using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Single source of truth for parameters, defaults, and domain bounds of <see cref="DistributionRangeCalculator"/>.
/// </summary>
public sealed record DistributionRangeParameters
{
    public const int MinFixedBinCount = 5;
    public const int MaxFixedBinCount = 100;
    public const int DefaultFixedBinCount = 20;

    public const int CurveSamplePoints = 128;
    public const int LowSampleThreshold = 30;

    public const decimal MinStudentTDegreesOfFreedom = 2.1m;
    public const decimal MaxStudentTDegreesOfFreedom = 1000m;
    public const decimal DefaultStudentTDegreesOfFreedom = 5.0m;
    public const decimal DefaultFallbackStudentTDegreesOfFreedom = 30.0m;

    public const decimal MinGevShapeParameter = -0.5m;
    public const decimal MaxGevShapeParameter = 0.45m;
    public const decimal DefaultGevShapeParameter = 0m;

    public const double MinBandwidthMultiplier = 0.25;
    public const double MaxBandwidthMultiplier = 4.0;
    public const double DefaultBandwidthMultiplier = 1.0;

    public static DistributionRangeParameters Default { get; } = new();

    public DistributionDataMode DataMode { get; init; } = DistributionDataMode.LogReturn;

    public PriceType PriceSource { get; init; } = PriceType.Close;

    public DistributionBinningRule BinningRule { get; init; } = DistributionBinningRule.FreedmanDiaconis;

    public int FixedBinCount { get; init; } = DefaultFixedBinCount;

    public ReferenceDistributionType ReferenceDistribution { get; init; } = ReferenceDistributionType.Normal;

    public bool OverrideStudentTDegreesOfFreedom { get; init; }

    public decimal StudentTDegreesOfFreedom { get; init; } = DefaultStudentTDegreesOfFreedom;

    public decimal GevShapeParameter { get; init; } = DefaultGevShapeParameter;

    public double BandwidthMultiplier { get; init; } = DefaultBandwidthMultiplier;

    public bool IncludeKde { get; init; } = true;

    /// <summary>
    /// Returns a clamped copy respecting all documented parameter bounds.
    /// </summary>
    public DistributionRangeParameters Normalize()
    {
        int bins = Math.Clamp(FixedBinCount, MinFixedBinCount, MaxFixedBinCount);
        decimal df = ClampDecimal(StudentTDegreesOfFreedom, MinStudentTDegreesOfFreedom, MaxStudentTDegreesOfFreedom);
        decimal xi = ClampDecimal(GevShapeParameter, MinGevShapeParameter, MaxGevShapeParameter);
        double bw = double.IsFinite(BandwidthMultiplier)
            ? Math.Clamp(BandwidthMultiplier, MinBandwidthMultiplier, MaxBandwidthMultiplier)
            : DefaultBandwidthMultiplier;

        return this with
        {
            FixedBinCount = bins,
            StudentTDegreesOfFreedom = df,
            GevShapeParameter = xi,
            BandwidthMultiplier = bw
        };
    }

    public static decimal ClampStudentTDegreesOfFreedom(decimal value)
        => ClampDecimal(value, MinStudentTDegreesOfFreedom, MaxStudentTDegreesOfFreedom);

    public static decimal ClampGevShapeParameter(decimal value)
        => ClampDecimal(value, MinGevShapeParameter, MaxGevShapeParameter);

    public static bool IsPositiveOnlyDistribution(ReferenceDistributionType distribution) => distribution switch
    {
        ReferenceDistributionType.LogNormal => true,
        ReferenceDistributionType.Gamma => true,
        ReferenceDistributionType.Weibull => true,
        ReferenceDistributionType.Exponential => true,
        ReferenceDistributionType.Pareto => true,
        _ => false
    };

    private static decimal ClampDecimal(decimal value, decimal min, decimal max)
        => value < min ? min : (value > max ? max : value);
}
