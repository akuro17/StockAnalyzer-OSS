using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Sampling resolution of <see cref="EquityCurveSampler"/>.</summary>
public enum EquityCurveResolution
{
    /// <summary>Every evaluated bar.</summary>
    Bar = 0,

    /// <summary>The last evaluated bar of each ISO week.</summary>
    Weekly = 1,

    /// <summary>The last evaluated bar of each calendar month.</summary>
    Monthly = 2,
}

/// <summary>
/// One sampled point of the equity curve. <see cref="Equity"/> is in account currency (Cash + HeldMargin + UnrealizedPnL).
/// <see cref="DrawdownRatio"/> is the unitless ratio <c>(E - P) / P</c> (at most 0; -0.12 means 12% below the running high P), or null with
/// <see cref="DrawdownReason"/> when the running high is not positive.
/// </summary>
public readonly record struct EquityCurvePoint(
    int BarIndex,
    DateTime Timestamp,
    decimal Equity,
    decimal? DrawdownRatio,
    string? DrawdownReason);

/// <summary>
/// Pure sampler of the evaluated equity points. The running high P_k = max(E_0..E_k) with E_0 = initial capital is computed over EVERY
/// evaluated bar first, then the points are thinned, so a thinned curve carries the same drawdown values as the bar-resolution curve.
/// Weekly and monthly period membership is <see cref="TimeFrameAggregator.GetPeriodKey"/> (single definition of ISO week and calendar month).
/// </summary>
public static class EquityCurveSampler
{
    /// <summary>Reason code of a null drawdown ratio: the running high was not positive.</summary>
    public const string NonPositivePeakReason = "NonPositivePeak";

    /// <summary>
    /// <paramref name="points"/> are the EVALUATED points only (warm-up already removed), in chronological order. An empty or default
    /// array yields an empty array. A decimal overflow propagates as <see cref="OverflowException"/>.
    /// </summary>
    public static ImmutableArray<EquityCurvePoint> Sample(
        decimal initialCapital,
        ImmutableArray<EquityPoint> points,
        EquityCurveResolution resolution)
    {
        if (points.IsDefaultOrEmpty)
        {
            return ImmutableArray<EquityCurvePoint>.Empty;
        }

        TimeFrame? periodFrame = resolution switch
        {
            EquityCurveResolution.Bar => null,
            EquityCurveResolution.Weekly => TimeFrame.W1,
            EquityCurveResolution.Monthly => TimeFrame.MN1,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unknown equity curve resolution."),
        };

        ImmutableArray<EquityCurvePoint>.Builder sampled = ImmutableArray.CreateBuilder<EquityCurvePoint>();
        decimal peak = initialCapital;
        for (int i = 0; i < points.Length; i++)
        {
            EquityPoint point = points[i];
            if (point.Equity > peak)
            {
                peak = point.Equity;
            }

            bool emit = periodFrame is not { } frame
                || i == points.Length - 1
                || TimeFrameAggregator.GetPeriodKey(points[i + 1].Timestamp, frame) != TimeFrameAggregator.GetPeriodKey(point.Timestamp, frame);
            if (!emit)
            {
                continue;
            }

            sampled.Add(peak > 0m
                ? new EquityCurvePoint(point.BarIndex, point.Timestamp, point.Equity, (point.Equity - peak) / peak, null)
                : new EquityCurvePoint(point.BarIndex, point.Timestamp, point.Equity, null, NonPositivePeakReason));
        }

        return sampled.ToImmutable();
    }
}
