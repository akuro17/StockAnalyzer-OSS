using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>Manual configuration for one phase-space spiral analysis.</summary>
public readonly record struct SpiralAnalysisParameters(
    uint BarsPerTurn,
    uint StartIndex,
    SpiralPriceModelKind Model,
    decimal InitialRadius,
    double? RadialGrowthPerRadian = null,
    double? LogGrowthPerRadian = null,
    uint? EndIndex = null,
    PriceType AppliedPriceType = PriceType.Close,
    bool IsClockwise = false)
{
    public const uint MinimumBarsPerTurn = 1;
    public const uint MaximumBarsPerTurn = 10_000;
    public const double MaximumExponent = 700.0;

    public void Validate(int candleCount)
    {
        if (BarsPerTurn is < MinimumBarsPerTurn or > MaximumBarsPerTurn)
            throw new ArgumentOutOfRangeException(nameof(BarsPerTurn));
        if (InitialRadius <= 0m)
            throw new ArgumentOutOfRangeException(nameof(InitialRadius));

        switch (Model)
        {
            case SpiralPriceModelKind.Archimedean:
                if (!RadialGrowthPerRadian.HasValue || LogGrowthPerRadian.HasValue || !double.IsFinite(RadialGrowthPerRadian.Value))
                    throw new ArgumentException("Archimedean analysis requires only a finite radial growth coefficient.");
                break;
            case SpiralPriceModelKind.Logarithmic:
                if (!LogGrowthPerRadian.HasValue || RadialGrowthPerRadian.HasValue || !double.IsFinite(LogGrowthPerRadian.Value))
                    throw new ArgumentException("Logarithmic analysis requires only a finite logarithmic growth coefficient.");
                break;
            case SpiralPriceModelKind.Golden:
                if (RadialGrowthPerRadian.HasValue || LogGrowthPerRadian.HasValue)
                    throw new ArgumentException("Golden analysis does not accept a manually supplied growth coefficient.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Model));
        }

        if (candleCount < 2 || StartIndex >= (uint)candleCount)
            throw new ArgumentOutOfRangeException(nameof(StartIndex));
        uint endIndex = EndIndex ?? checked((uint)candleCount - 1);
        if (endIndex >= (uint)candleCount || StartIndex >= endIndex)
            throw new ArgumentOutOfRangeException(nameof(EndIndex));
    }

    public uint GetEndIndex(int candleCount)
    {
        Validate(candleCount);
        return EndIndex ?? checked((uint)candleCount - 1);
    }
}

public enum SpiralAnalysisSampleStatus
{
    Valid,
    NonPositivePrice
}

/// <summary>Direction and acceleration classification for the latest valid price sample.</summary>
public enum SpiralPriceMotionState
{
    Unavailable,
    Flat,
    RisingAccelerating,
    RisingDecelerating,
    FallingAccelerating,
    FallingDecelerating,
    RisingConstant,
    FallingConstant
}

/// <summary>Immutable fit and latest-motion values derived from valid samples only.</summary>
public readonly record struct SpiralAnalysisSummary(
    int ValidSampleCount,
    decimal? MeanAbsoluteError,
    decimal? RootMeanSquareError,
    decimal? NormalizedRootMeanSquareError,
    decimal? LatestResidualPercent,
    decimal? LatestPriceVelocityPerBar,
    decimal? LatestPriceAccelerationPerBarSquared,
    double? LatestModelVelocityPerBar,
    double? LatestModelAccelerationPerBarSquared,
    SpiralPriceMotionState LatestMotionState);

/// <summary>One chronological price sample and its phase-space coordinates.</summary>
public readonly record struct SpiralAnalysisSample(
    uint Index,
    DateTime Timestamp,
    SpiralAnalysisSampleStatus Status,
    decimal Price,
    double AngleRadians,
    double Radius,
    double? ModelRadius,
    double? Residual,
    double? PriceVelocityPerBar,
    double? PriceAccelerationPerBarSquared);

/// <summary>Immutable analysis output. A stopped result contains only samples evaluated before the exponent limit.</summary>
public sealed class SpiralAnalysisResult
{
    public SpiralAnalysisResult(SpiralAnalysisParameters parameters, SpiralAnalysisSample[] samples, bool stoppedByExponentLimit, SpiralAnalysisSummary summary)
    {
        Parameters = parameters;
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        StoppedByExponentLimit = stoppedByExponentLimit;
        Summary = summary;
    }

    public SpiralAnalysisParameters Parameters { get; }
    public IReadOnlyList<SpiralAnalysisSample> Samples { get; }
    public bool StoppedByExponentLimit { get; }
    public bool HasNumericLimit { get; init; }
    public SpiralAnalysisSummary Summary { get; }
}
