namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// One histogram bin covering [<see cref="LowerBound"/>, <see cref="UpperBound"/>).
/// </summary>
public readonly record struct DistributionBin(
    decimal LowerBound,
    decimal UpperBound,
    int Count,
    decimal Density,
    decimal NormalizedHeight);

/// <summary>
/// One evaluated probability density point at <see cref="X"/>.
/// </summary>
public readonly record struct DistributionCurvePoint(decimal X, decimal Density);

/// <summary>
/// Complete Core calculation result for a <c>DistributionRange</c>.
/// </summary>
public sealed class DistributionRangeResult
{
    public static DistributionRangeResult Empty { get; } = new()
    {
        ReferenceStatus = ReferenceFitStatus.InsufficientData,
        ReferenceParameterSummary = string.Empty
    };

    public int SampleCount { get; init; }

    public bool IsLowSampleSize => SampleCount < DistributionRangeParameters.LowSampleThreshold;

    public decimal? Mean { get; init; }

    public decimal? StdDev { get; init; }

    public decimal? Median { get; init; }

    public decimal? Iqr { get; init; }

    public decimal? Skewness { get; init; }

    public decimal? ExcessKurtosis { get; init; }

    public decimal? MinValue { get; init; }

    public decimal? MaxValue { get; init; }

    public decimal? BinWidth { get; init; }

    public IReadOnlyList<DistributionBin> Bins { get; init; } = Array.Empty<DistributionBin>();

    public IReadOnlyList<DistributionCurvePoint> KdeCurve { get; init; } = Array.Empty<DistributionCurvePoint>();

    public IReadOnlyList<DistributionCurvePoint> ReferenceCurve { get; init; } = Array.Empty<DistributionCurvePoint>();

    public ReferenceFitStatus ReferenceStatus { get; init; } = ReferenceFitStatus.InsufficientData;

    public string ReferenceParameterSummary { get; init; } = string.Empty;
}
