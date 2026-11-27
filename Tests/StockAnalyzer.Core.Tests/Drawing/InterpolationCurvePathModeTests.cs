using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>
/// The path mode of the anchor curves (settings follow-up Run 8, DP-1 = B): the parametric curve through the anchors in placement order. Both interpolations (PCHIP and the natural cubic spline) obey
/// one contract. The comparison with the time-series mode is exact only where the chord length is proportional to time (anchors on a straight line); for general data the two modes pass through the
/// same anchors but the curve between them differs (the chord also counts the price distance), so only the anchors are compared.
/// </summary>
public sealed class InterpolationCurvePathModeTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> Bars(int count = 120)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new CoreCandleData(Start.AddDays(i), 100m, 101m, 99m, 100m, 1000L));
        }

        return list;
    }

    private static DrawingAnchor A(int bar, decimal price) => new(Start.AddDays(bar), price);

    public static TheoryData<InterpolationCurveKind> Kinds() => new() { InterpolationCurveKind.Pchip, InterpolationCurveKind.CubicSpline };

    private static InterpolationCurve Path(InterpolationCurveKind kind, params DrawingAnchor[] anchors)
        => InterpolationCurveValues.Calculate(Bars(), anchors, kind, InterpolationCurveMode.Path);

    private static InterpolationCurve Series(InterpolationCurveKind kind, params DrawingAnchor[] anchors)
        => InterpolationCurveValues.Calculate(Bars(), anchors, kind, InterpolationCurveMode.TimeSeries);

    private static int SamplesFor(int anchors) => ((anchors - 1) * InterpolationCurveValues.SamplesPerSegment) + 1;

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheDefaultMode_IsTimeSeries_AndTheOldCallGivesTheSameCurve(InterpolationCurveKind kind)
    {
        DrawingAnchor[] anchors = { A(10, 100m), A(25, 130m), A(40, 110m), A(60, 140m) };
        InterpolationCurve viaDefault = InterpolationCurveValues.Calculate(Bars(), anchors, kind);
        InterpolationCurve viaSeries = Series(kind, anchors);

        Assert.Equal(viaSeries.Times, viaDefault.Times);
        Assert.Equal(viaSeries.Prices, viaDefault.Prices);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void OnAStraightLine_PathAndTimeSeriesAreTheSameCurve_EvenWithUnevenSpacing(InterpolationCurveKind kind)
    {
        // y = 100 + 2 (x - 10): the chord length of the (normalised) anchors is proportional to x.
        DrawingAnchor[] anchors = { A(10, 100m), A(14, 108m), A(30, 140m), A(33, 146m), A(70, 220m) };
        InterpolationCurve path = Path(kind, anchors);
        InterpolationCurve series = Series(kind, anchors);

        Assert.True(path.IsOk && series.IsOk);
        Assert.Equal(series.Count, path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            Assert.True(Math.Abs((path.Times[i] - series.Times[i]).TotalSeconds) < 1.0, $"time {i}");
            Assert.True(Math.Abs(path.Prices[i] - series.Prices[i]) < 0.000001m, $"price {i}: {path.Prices[i]} vs {series.Prices[i]}");
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ForGeneralIncreasingData_PathPassesThroughEveryAnchor_ButIsADifferentCurveBetweenThem(InterpolationCurveKind kind)
    {
        // (0, 0), (1, 10), (2, 11) in the spirit of the review: a steep step then a flat one.
        DrawingAnchor[] anchors = { A(10, 100m), A(11, 110m), A(12, 111m), A(40, 140m) };
        InterpolationCurve path = Path(kind, anchors);
        InterpolationCurve series = Series(kind, anchors);

        Assert.True(path.IsOk);
        Assert.Equal(SamplesFor(4), path.Count);
        for (int i = 0; i < anchors.Length; i++)
        {
            int sample = i * InterpolationCurveValues.SamplesPerSegment;
            Assert.Equal(anchors[i].Time, path.Times[sample]);
            Assert.Equal(anchors[i].Price, path.Prices[sample]);
        }

        Assert.Contains(Enumerable.Range(0, path.Count), i => Math.Abs(path.Prices[i] - series.Prices[i]) > 0.001m || Math.Abs((path.Times[i] - series.Times[i]).TotalSeconds) > 1.0);
    }

    [Fact]
    public void Pchip_Path_KeepsTheTimeMonotoneWhenTheAnchorsRiseInTime()
    {
        InterpolationCurve path = Path(InterpolationCurveKind.Pchip, A(10, 100m), A(11, 110m), A(12, 111m), A(40, 140m), A(80, 90m));

        Assert.True(path.IsOk);
        for (int i = 1; i < path.Count; i++)
        {
            Assert.True(path.Times[i] >= path.Times[i - 1], $"time moves back at sample {i}");
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AnAnchorBeforeThePreviousOne_MakesTheCurveTurnBack_InPathMode_AndIsSortedInTimeSeriesMode(InterpolationCurveKind kind)
    {
        DrawingAnchor[] anchors = { A(10, 100m), A(40, 130m), A(25, 150m) };   // the third anchor lies before the second one in time
        InterpolationCurve path = Path(kind, anchors);
        InterpolationCurve series = Series(kind, anchors);

        Assert.True(path.IsOk && series.IsOk);
        Assert.Equal(anchors[0].Time, path.Times[0]);
        Assert.Equal(anchors[2].Time, path.Times[path.Count - 1]);   // the last PLACED anchor ends the path
        Assert.Contains(Enumerable.Range(1, path.Count - 1), i => path.Times[i] < path.Times[i - 1]);   // the path goes back in time
        Assert.Equal(anchors[1].Time, path.Times[InterpolationCurveValues.SamplesPerSegment]);        // and passes the middle anchor in placement order

        for (int i = 1; i < series.Count; i++)
        {
            Assert.True(series.Times[i] >= series.Times[i - 1]);   // the time-series curve never goes back
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AClosedLoop_KeepsTheRepeatedFirstAnchor_AndPassesThroughAllOfThem(InterpolationCurveKind kind)
    {
        DrawingAnchor[] anchors = { A(10, 100m), A(30, 100m), A(30, 130m), A(10, 130m), A(10, 100m) };   // a square that returns to its start
        InterpolationCurve path = Path(kind, anchors);

        Assert.True(path.IsOk);
        Assert.Equal(5, path.DistinctAnchors);   // the non-adjacent repeat of the first anchor is not merged
        Assert.Equal(SamplesFor(5), path.Count);
        Assert.Equal(path.Times[0], path.Times[path.Count - 1]);
        Assert.Equal(path.Prices[0], path.Prices[path.Count - 1]);
        for (int i = 0; i < anchors.Length; i++)
        {
            Assert.Equal(anchors[i].Price, path.Prices[i * InterpolationCurveValues.SamplesPerSegment]);
        }

        Assert.All(path.Prices, price => Assert.True(price > 0m));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void OnlyAdjacentIdenticalAnchorsAreMerged(InterpolationCurveKind kind)
    {
        InterpolationCurve doubled = Path(kind, A(10, 100m), A(10, 100m), A(30, 120m), A(30, 120m), A(50, 105m));
        Assert.True(doubled.IsOk);
        Assert.Equal(3, doubled.DistinctAnchors);
        Assert.Equal(SamplesFor(3), doubled.Count);

        InterpolationCurve revisited = Path(kind, A(10, 100m), A(30, 120m), A(10, 100m));
        Assert.Equal(3, revisited.DistinctAnchors);   // the same point later in the sequence stays

        InterpolationCurve verticalStep = Path(kind, A(10, 100m), A(10, 120m), A(30, 110m));
        Assert.Equal(3, verticalStep.DistinctAnchors);   // the same time with another price is another anchor (the time-series mode would merge them)
        Assert.Equal(2, Series(kind, A(10, 100m), A(10, 120m), A(30, 110m)).DistinctAnchors);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FewerThanTwoAnchorsLeft_DrawNothing_AndExactlyTwoAreAStraightSegment(InterpolationCurveKind kind)
    {
        InterpolationCurve single = Path(kind, A(10, 100m), A(10, 100m), A(10, 100m));
        Assert.Equal(InterpolationCurveStatus.TooFewAnchors, single.Status);
        Assert.Equal(1, single.DistinctAnchors);
        Assert.Equal(0, single.Count);

        InterpolationCurve pair = Path(kind, A(10, 100m), A(10, 100m), A(30, 120m));
        Assert.True(pair.IsOk);
        Assert.Equal(2, pair.Count);
        Assert.Equal(A(30, 120m).Time, pair.Times[1]);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ATooLongPath_AndNoData_AreReported(InterpolationCurveKind kind)
    {
        DrawingAnchor[] many = Enumerable.Range(0, InterpolationCurveValues.MaxAnchors + 1).Select(i => A(i, 100m + (i % 2 == 0 ? 0m : 10m))).ToArray();
        Assert.Equal(InterpolationCurveStatus.TooManyAnchors, Path(kind, many).Status);
        Assert.Equal(InterpolationCurveStatus.NoData, InterpolationCurveValues.Calculate(new List<CoreCandleData>(), many, kind, InterpolationCurveMode.Path).Status);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AFlatOrVerticalPath_HasFiniteSamples(InterpolationCurveKind kind)
    {
        InterpolationCurve flat = Path(kind, A(10, 100m), A(30, 100m), A(50, 100m), A(20, 100m));   // no spread on the price axis
        Assert.True(flat.IsOk);
        Assert.All(flat.Prices, price => Assert.Equal(100m, price));

        InterpolationCurve vertical = Path(kind, A(30, 100m), A(30, 120m), A(30, 105m));   // no spread on the time axis
        Assert.True(vertical.IsOk);
        Assert.All(vertical.Times, time => Assert.Equal(A(30, 100m).Time, time));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ThePathIsDeterministic_AndTheDataTabRowsFollowThePlacementOrder(InterpolationCurveKind kind)
    {
        DrawingAnchor[] anchors = { A(10, 100m), A(40, 130m), A(25, 150m) };
        InterpolationCurve first = Path(kind, anchors);
        InterpolationCurve second = Path(kind, anchors);
        Assert.Equal(first.Prices, second.Prices);
        Assert.Equal(first.Times, second.Times);

        IReadOnlyList<DrawingCalculatedValue> rows = InterpolationCurveValues.Calculate(first, IndicatorColor.Gray);
        Assert.Equal(100m, rows.Single(r => r.Key == "CurveStart").NumericValue);
        Assert.Equal(150m, rows.Single(r => r.Key == "CurveEnd").NumericValue);   // the last placed anchor, not the latest in time
    }
}
