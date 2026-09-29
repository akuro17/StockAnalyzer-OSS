using System;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class ComplexGeometryCalculatedValuesTests
{
    [Fact]
    public void ParallelChannelObject_GetCalculatedValues_ReturnsChannels_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var t3 = new DateTime(2026, 1, 6);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 200m); // slope = +10/day
        var p3 = new ChartPoint(t3, 170m); // base at t3 is 150m, offset is +20m

        var channel = new ParallelChannelObject(p1, p2, p3);

        var values = channel.GetCalculatedValues(t3);
        Assert.NotEmpty(values);

        var upper = Assert.Single(values, v => v.Key == "UpperLine");
        var mid = Assert.Single(values, v => v.Key == "Midline");
        var lower = Assert.Single(values, v => v.Key == "LowerLine");
        var width = Assert.Single(values, v => v.Key == "ChannelWidth");
        var slope = Assert.Single(values, v => v.Key == "Slope");

        Assert.Equal(170m, upper.NumericValue);
        Assert.Equal(160m, mid.NumericValue);
        Assert.Equal(150m, lower.NumericValue);
        Assert.Equal(20m, width.NumericValue);
        Assert.Equal(10m, slope.NumericValue);

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2") || v.Key.Contains("P3"));

        channel.Points.Clear();
        Assert.Empty(channel.GetCalculatedValues(t3));
    }

    [Fact]
    public void PitchforkObject_GetCalculatedValues_ReturnsLines_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 5);
        var t3 = new DateTime(2026, 1, 9);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 160m);
        var p3 = new ChartPoint(t3, 140m);

        var pitchfork = new PitchforkObject(p1, p2, p3);

        var values = pitchfork.GetCalculatedValues(new DateTime(2026, 1, 7)); // midTime between p2 and p3
        Assert.NotEmpty(values);

        var upper = Assert.Single(values, v => v.Key == "UpperLine");
        var median = Assert.Single(values, v => v.Key == "MedianLine");
        var lower = Assert.Single(values, v => v.Key == "LowerLine");

        Assert.Equal(160m, upper.NumericValue);
        Assert.Equal(150m, median.NumericValue);
        Assert.Equal(140m, lower.NumericValue);

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2") || v.Key.Contains("P3"));

        pitchfork.Points.Clear();
        Assert.Empty(pitchfork.GetCalculatedValues(t1));
    }

    [Fact]
    public void FibonacciExpansionObject_GetCalculatedValues_ReturnsLevels_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 5);
        var t3 = new DateTime(2026, 1, 10);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 200m); // impulse = +100m
        var p3 = new ChartPoint(t3, 150m); // correction to 150m

        var exp = new FibonacciExpansionObject(p1, p2, p3);

        var values = exp.GetCalculatedValues(t3);
        Assert.NotEmpty(values);

        var impulse = Assert.Single(values, v => v.Key == "Impulse");
        Assert.Equal(100m, impulse.NumericValue);

        var exp100 = Assert.Single(values, v => v.Key == "Exp_1.000");
        Assert.Equal(250m, exp100.NumericValue); // 150 + 100 * 1.0 = 250m

        var exp618 = Assert.Single(values, v => v.Key == "Exp_0.618");
        Assert.Equal(211.8m, exp618.NumericValue); // 150 + 100 * 0.618 = 211.8m

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2") || v.Key.Contains("P3"));

        exp.Points.Clear();
        Assert.Empty(exp.GetCalculatedValues(t3));
    }

    [Fact]
    public void FibonacciFanObject_GetCalculatedValues_ReturnsRays_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 200m); // range = 100m

        var fan = new FibonacciFanObject(p1, p2);

        // At end time t2
        var values = fan.GetCalculatedValues(t2);
        Assert.NotEmpty(values);

        var range = Assert.Single(values, v => v.Key == "Range");
        Assert.Equal(100m, range.NumericValue);

        var fan100 = Assert.Single(values, v => v.Key == "Fan_1.000");
        Assert.Equal(200m, fan100.NumericValue);

        var fan50 = Assert.Single(values, v => v.Key == "Fan_0.500");
        Assert.Equal(150m, fan50.NumericValue);

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        fan.Points.Clear();
        Assert.Empty(fan.GetCalculatedValues(t2));
    }

    [Fact]
    public void FibonacciChannelObject_GetCalculatedValues_ReturnsLevels_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var t3 = new DateTime(2026, 1, 6);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 200m); // base at t3 is 150m
        var p3 = new ChartPoint(t3, 190m); // channelWidth = 40m

        var fibChannel = new FibonacciChannelObject(p1, p2, p3);

        var values = fibChannel.GetCalculatedValues(t3);
        Assert.NotEmpty(values);

        var baseLine = Assert.Single(values, v => v.Key == "BaseLine");
        Assert.Equal(150m, baseLine.NumericValue);

        var width = Assert.Single(values, v => v.Key == "ChannelWidth");
        Assert.Equal(40m, width.NumericValue);

        var ch100 = Assert.Single(values, v => v.Key == "Channel_1.000");
        Assert.Equal(190m, ch100.NumericValue); // 150 + 40 = 190m

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2") || v.Key.Contains("P3"));

        fibChannel.Points.Clear();
        Assert.Empty(fibChannel.GetCalculatedValues(t3));
    }

    [Fact]
    public void GannFanObject_GetCalculatedValues_ReturnsAngles_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 150m); // dy = +50m

        var gannFan = new GannFanObject(p1, p2);

        var values = gannFan.GetCalculatedValues(t2);
        Assert.NotEmpty(values);

        var gann1x1 = Assert.Single(values, v => v.Key == "Gann_1x1");
        Assert.Equal(150m, gann1x1.NumericValue);

        var gann2x1 = Assert.Single(values, v => v.Key == "Gann_2x1");
        Assert.Equal(125m, gann2x1.NumericValue); // 100 + 50 * 0.5 = 125m

        var gann1x2 = Assert.Single(values, v => v.Key == "Gann_1x2");
        Assert.Equal(200m, gann1x2.NumericValue); // 100 + 50 * 2.0 = 200m

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        gannFan.Points.Clear();
        Assert.Empty(gannFan.GetCalculatedValues(t2));
    }

    [Fact]
    public void GannBoxObject_GetCalculatedValues_ReturnsLevelsAndDimensions_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11); // 10 days

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 180m); // height = 80m

        var gannBox = new GannBoxObject(p1, p2);

        var values = gannBox.GetCalculatedValues(t1);
        Assert.NotEmpty(values);

        var high = Assert.Single(values, v => v.Key == "HighPrice");
        Assert.Equal(180m, high.NumericValue);

        var low = Assert.Single(values, v => v.Key == "LowPrice");
        Assert.Equal(100m, low.NumericValue);

        var height = Assert.Single(values, v => v.Key == "Height");
        Assert.Equal(80m, height.NumericValue);

        var half = Assert.Single(values, v => v.Key == "Gann_4_8");
        Assert.Equal(140m, half.NumericValue); // 100 + 80 * 0.5 = 140m

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        gannBox.Points.Clear();
        Assert.Empty(gannBox.GetCalculatedValues(t1));
    }

    [Fact]
    public void CyclicLinesObject_GetCalculatedValues_ReturnsPeriodAndCycles_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11); // 10 days period

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 100m);

        var cyclic = new CyclicLinesObject(p1, p2);

        var cursor = new DateTime(2026, 1, 16); // 1.5 cycles
        var values = cyclic.GetCalculatedValues(cursor);
        Assert.NotEmpty(values);

        var period = Assert.Single(values, v => v.Key == "Period");
        Assert.Equal(10.0m, period.NumericValue);

        var count = Assert.Single(values, v => v.Key == "CycleCount");
        Assert.Equal(1.5m, count.NumericValue);

        var next = Assert.Single(values, v => v.Key == "NextCycle");
        Assert.Contains("2026-01-21", next.FormattedText);

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        cyclic.Points.Clear();
        Assert.Empty(cyclic.GetCalculatedValues(cursor));
    }

    [Fact]
    public void TimeCyclesObject_GetCalculatedValues_ReturnsPeriodAndPhase_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11); // 10 days period

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 100m);

        var timeCycles = new TimeCyclesObject(p1, p2);

        var cursor = new DateTime(2026, 1, 6); // 50% phase
        var values = timeCycles.GetCalculatedValues(cursor);
        Assert.NotEmpty(values);

        var period = Assert.Single(values, v => v.Key == "Period");
        Assert.Equal(10.0m, period.NumericValue);

        var progress = Assert.Single(values, v => v.Key == "CycleProgress");
        Assert.Equal(50.0m, progress.NumericValue);

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        timeCycles.Points.Clear();
        Assert.Empty(timeCycles.GetCalculatedValues(cursor));
    }

    [Fact]
    public void CyclicLinesObject_GetCalculatedValues_ExtremeDates_DoesNotThrow()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 100m);

        var cyclic = new CyclicLinesObject(p1, p2);

        var ex1 = Record.Exception(() => cyclic.GetCalculatedValues(DateTime.MaxValue));
        Assert.Null(ex1);

        var ex2 = Record.Exception(() => cyclic.GetCalculatedValues(DateTime.MinValue));
        Assert.Null(ex2);
    }

    [Fact]
    public void TimeCyclesObject_GetCalculatedValues_ExtremeDates_DoesNotThrow()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 100m);

        var timeCycles = new TimeCyclesObject(p1, p2);

        var ex1 = Record.Exception(() => timeCycles.GetCalculatedValues(DateTime.MaxValue));
        Assert.Null(ex1);

        var ex2 = Record.Exception(() => timeCycles.GetCalculatedValues(DateTime.MinValue));
        Assert.Null(ex2);
    }

    [Fact]
    public void SineLineObject_GetCalculatedValues_ReturnsWaveMetrics_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11); // 10 days period, amp = 20m

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 120m);

        var sine = new SineLineObject(p1, p2);

        // At quarter period (2.5 days), sin(pi/2) = 1 => 100 + 20 = 120m
        var cursor = new DateTime(2026, 1, 3, 12, 0, 0);
        var values = sine.GetCalculatedValues(cursor);
        Assert.NotEmpty(values);

        var sinePrice = Assert.Single(values, v => v.Key == "SinePrice");
        Assert.True(Math.Abs(sinePrice.NumericValue!.Value - 120m) < 0.01m);

        var center = Assert.Single(values, v => v.Key == "CenterPrice");
        Assert.Equal(100m, center.NumericValue);

        var amp = Assert.Single(values, v => v.Key == "Amplitude");
        Assert.Equal(20m, amp.NumericValue);

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        sine.Points.Clear();
        Assert.Empty(sine.GetCalculatedValues(cursor));
    }

    [Fact]
    public void PolylineObject_GetCalculatedValues_ReturnsInterpolatedPrice_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 5);
        var t3 = new DateTime(2026, 1, 11);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 140m);
        var p3 = new ChartPoint(t3, 110m);

        using var poly = new PolylineObject(new[] { p1, p2, p3 })
        {
            LabelType = PolylineLabelType.Numeric
        };

        // Midpoint of first segment: t1 + 2 days = 120m
        var cursor = new DateTime(2026, 1, 3);
        var values = poly.GetCalculatedValues(cursor);
        Assert.NotEmpty(values);

        var polyPrice = Assert.Single(values, v => v.Key == "PolylinePrice");
        Assert.Equal(120m, polyPrice.NumericValue);

        var net = Assert.Single(values, v => v.Key == "NetChange");
        Assert.Equal(10m, net.NumericValue); // 110 - 100 = 10m

        var segments = Assert.Single(values, v => v.Key == "Segments");
        Assert.Equal(2m, segments.NumericValue);

        var labelType = Assert.Single(values, v => v.Key == "LabelType");
        Assert.Equal("Numeric", labelType.FormattedText);

        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2") || v.Key.Contains("P3"));

        poly.Points.Clear();
        Assert.Empty(poly.GetCalculatedValues(cursor));
    }

    [Fact]
    public void CurveTrendObject_GetCalculatedValues_ReturnsCurvePrice_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var tMid = new DateTime(2026, 1, 6);

        var p0 = new ChartPoint(t1, 100m);
        var p1 = new ChartPoint(t2, 100m);
        var p2 = new ChartPoint(tMid, 140m); // Mid control point 140m (deflection +40m)

        using var curve = new CurveTrendObject(p0, p1, p2);

        // At t=0.5: B(0.5) = 0.25 * 100 + 0.5 * 140 + 0.25 * 100 = 25 + 70 + 25 = 120m
        var values = curve.GetCalculatedValues(tMid);
        Assert.NotEmpty(values);

        var curvePrice = Assert.Single(values, v => v.Key == "CurvePrice");
        Assert.Equal(120m, curvePrice.NumericValue);

        var curvature = Assert.Single(values, v => v.Key == "Curvature");
        Assert.Equal(40m, curvature.NumericValue);

        Assert.DoesNotContain(values, v => v.Key.Contains("P0") || v.Key.Contains("P1") || v.Key.Contains("P2"));

        curve.Points.Clear();
        Assert.Empty(curve.GetCalculatedValues(tMid));
    }

    [Fact]
    public void CatenaryCurveObject_GetCalculatedValues_ReturnsCatenaryPrice_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var tMid = new DateTime(2026, 1, 6);

        var p0 = new ChartPoint(t1, 100m);
        var p1 = new ChartPoint(t2, 100m);
        var p2 = new ChartPoint(tMid, 60m); // sag = 60 - 100 = -40m

        using var catenary = new CatenaryCurveObject(p0, p1, p2);

        // At midpoint u=0.5: linearPrice = 100m, sagContribution = 4 * 0.25 * (-40m) = -40m => catenaryPrice = 60m
        var values = catenary.GetCalculatedValues(tMid);
        Assert.NotEmpty(values);

        var catPrice = Assert.Single(values, v => v.Key == "CatenaryPrice");
        Assert.Equal(60m, catPrice.NumericValue);

        var sag = Assert.Single(values, v => v.Key == "Sag");
        Assert.Equal(-40m, sag.NumericValue);

        Assert.DoesNotContain(values, v => v.Key.Contains("P0") || v.Key.Contains("P1") || v.Key.Contains("P2"));

        catenary.Points.Clear();
        Assert.Empty(catenary.GetCalculatedValues(tMid));
    }

    [Fact]
    public void ChartInformationDataProvider_Extract_IncludesComplexDrawingTools()
    {
        var manager = new ChartObjectManager();
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var tMid = new DateTime(2026, 1, 6);

        manager.AddObject(new ParallelChannelObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m), new ChartPoint(tMid, 170m)));
        manager.AddObject(new PitchforkObject(new ChartPoint(t1, 100m), new ChartPoint(tMid, 160m), new ChartPoint(t2, 140m)));
        manager.AddObject(new FibonacciExpansionObject(new ChartPoint(t1, 100m), new ChartPoint(tMid, 200m), new ChartPoint(t2, 150m)));
        manager.AddObject(new FibonacciFanObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m)));
        manager.AddObject(new FibonacciChannelObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m), new ChartPoint(tMid, 170m)));
        manager.AddObject(new GannFanObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m)));
        manager.AddObject(new GannBoxObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 180m)));
        manager.AddObject(new CyclicLinesObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 100m)));
        manager.AddObject(new TimeCyclesObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 100m)));
        manager.AddObject(new SineLineObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 120m)));
        using var poly = new PolylineObject(new[] { new ChartPoint(t1, 100m), new ChartPoint(tMid, 140m), new ChartPoint(t2, 110m) });
        using var curve = new CurveTrendObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 100m), new ChartPoint(tMid, 140m));
        using var catenary = new CatenaryCurveObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 100m), new ChartPoint(tMid, 60m));

        manager.AddObject(poly);
        manager.AddObject(curve);
        manager.AddObject(catenary);

        var snapshot = ChartInformationDataProvider.Extract(
            candles: null,
            indicators: null,
            indicatorResults: null,
            objectManager: manager,
            targetTime: tMid,
            targetPrice: 120m
        );

        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.Drawings);

        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Channel"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Median"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Expansion"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Fan"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Gann"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Cycle"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Sine"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Polyline"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Curve"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Catenary"));
    }
}
