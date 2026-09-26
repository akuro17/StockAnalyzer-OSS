using System;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class MajorDrawingCalculatedValuesTests
{
    [Fact]
    public void FibonacciRetracementObject_GetCalculatedValues_ReturnsAllLevelsAndRange()
    {
        var time1 = new DateTime(2026, 1, 1);
        var time2 = new DateTime(2026, 1, 10);
        var p1 = new ChartPoint(time1, 100m); // Start (100%)
        var p2 = new ChartPoint(time2, 200m); // End (0%)

        using var fib = new FibonacciRetracementObject(p1, p2);

        // Test with current price 145m
        var values = fib.GetCalculatedValues(time2, currentPrice: 145m);

        Assert.NotEmpty(values);

        // Range is present, but raw anchor points (Start/End/P1/P2) must NOT be present
        var rangeVal = Assert.Single(values, v => v.Key == "Range");
        Assert.Equal(100m, rangeVal.NumericValue);
        Assert.DoesNotContain(values, v => v.Key == "Start" || v.Key == "End" || v.Key.StartsWith("P"));

        // Check Fib 0% = p2 (200m)
        var fib0 = Assert.Single(values, v => v.Key == "Fib_0.000");
        Assert.Equal(200m, fib0.NumericValue);

        // Check Fib 50% = 150m
        var fib50 = Assert.Single(values, v => v.Key == "Fib_0.500");
        Assert.Equal(150m, fib50.NumericValue);

        // Check Fib 100% = p1 (100m)
        var fib100 = Assert.Single(values, v => v.Key == "Fib_1.000");
        Assert.Equal(100m, fib100.NumericValue);

        // Check Nearest Level: nearest to 145m is 50% (150m), diff is -5m
        var nearest = Assert.Single(values, v => v.Key == "NearestLevel");
        Assert.Equal(150m, nearest.NumericValue);
        Assert.Contains("50.0%", nearest.FormattedText);
        Assert.Contains("-5.000", nearest.FormattedText);

        // Incomplete points should return empty
        fib.Points.Clear();
        Assert.Empty(fib.GetCalculatedValues(time2));
    }

    [Fact]
    public void GannSquareOfNineObject_GetCalculatedValues_ReturnsResistanceAndSupportLevels()
    {
        var time = new DateTime(2026, 1, 1);
        var basePoint = new ChartPoint(time, 100m); // sqrt(100) = 10

        var gann = new GannSquareOfNineObject(basePoint);

        var values = gann.GetCalculatedValues(time);
        Assert.NotEmpty(values);

        // Check Base Price
        var baseVal = Assert.Single(values, v => v.Key == "Base");
        Assert.Equal(100m, baseVal.NumericValue);

        // Check Resistance +45°: (10 + 0.25)^2 = 105.0625
        var res45 = Assert.Single(values, v => v.Key == "Res_45°");
        Assert.NotNull(res45.NumericValue);
        Assert.Equal(105.0625m, res45.NumericValue.Value);

        // Check Resistance +90°: (10 + 0.5)^2 = 110.25
        var res90 = Assert.Single(values, v => v.Key == "Res_90°");
        Assert.NotNull(res90.NumericValue);
        Assert.Equal(110.25m, res90.NumericValue.Value);

        // Check Resistance +360°: (10 + 2.0)^2 = 144.0
        var res360 = Assert.Single(values, v => v.Key == "Res_360°");
        Assert.NotNull(res360.NumericValue);
        Assert.Equal(144.0m, res360.NumericValue.Value);

        // Check Support -45°: (10 - 0.25)^2 = 95.0625
        var sup45 = Assert.Single(values, v => v.Key == "Sup_45°");
        Assert.NotNull(sup45.NumericValue);
        Assert.Equal(95.0625m, sup45.NumericValue.Value);

        // Check Support -90°: (10 - 0.5)^2 = 90.25
        var sup90 = Assert.Single(values, v => v.Key == "Sup_90°");
        Assert.NotNull(sup90.NumericValue);
        Assert.Equal(90.25m, sup90.NumericValue.Value);

        // Incomplete points should return empty
        gann.Points.Clear();
        Assert.Empty(gann.GetCalculatedValues(time));
    }

    [Fact]
    public void TargetPriceProjectionObject_GetCalculatedValues_ReturnsExpectedTargets()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 5);
        var t3 = new DateTime(2026, 1, 10);

        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 120m);
        var p3 = new ChartPoint(t3, 110m);

        using var targetObj = new TargetPriceProjectionObject(p1, p2, p3);

        // Test with current price 115m
        var values = targetObj.GetCalculatedValues(t3, currentPrice: 115m);
        Assert.NotEmpty(values);

        // Anchors (P1, P2, P3) must NOT be present per user constraint
        Assert.DoesNotContain(values, v => v.Key.StartsWith("P"));

        // Target V: P2 + (P2 - P3) = 120 + (120 - 110) = 130m
        var targetV = Assert.Single(values, v => v.Key == "Target_V");
        Assert.Equal(130m, targetV.NumericValue);
        Assert.Contains("130.000", targetV.FormattedText);
        Assert.Contains("+15.000", targetV.FormattedText); // 130 - 115 = +15

        // Target N: P3 + (P2 - P1) = 110 + (120 - 100) = 130m
        var targetN = Assert.Single(values, v => v.Key == "Target_N");
        Assert.Equal(130m, targetN.NumericValue);

        // Target %: P3 * (P2 / P1) = 110 * 1.2 = 132m
        var targetPct = Assert.Single(values, v => v.Key == "Target_%");
        Assert.Equal(132m, targetPct.NumericValue);

        // Target E: P2 + (P2 - P1) = 120 + 20 = 140m
        var targetE = Assert.Single(values, v => v.Key == "Target_E");
        Assert.Equal(140m, targetE.NumericValue);

        // Target NT: P3 + (P3 - P1) = 110 + 10 = 120m
        var targetNT = Assert.Single(values, v => v.Key == "Target_NT");
        Assert.Equal(120m, targetNT.NumericValue);

        // Incomplete points should return empty
        targetObj.Points.Clear();
        Assert.Empty(targetObj.GetCalculatedValues(t3));
    }

    [Fact]
    public void ChartInformationDataProvider_Extract_IncludesMajorDrawingTools()
    {
        var manager = new ChartObjectManager();
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 5);
        var t3 = new DateTime(2026, 1, 10);

        using var fib = new FibonacciRetracementObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m));
        var gann = new GannSquareOfNineObject(new ChartPoint(t1, 100m));
        using var target = new TargetPriceProjectionObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 120m), new ChartPoint(t3, 110m));

        manager.AddObject(fib);
        manager.AddObject(gann);
        manager.AddObject(target);

        var snapshot = ChartInformationDataProvider.Extract(
            candles: null,
            indicators: null,
            indicatorResults: null,
            objectManager: manager,
            targetTime: t2,
            targetPrice: 150m
        );

        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.Drawings);

        // Drawings list should contain metrics from all 3 tools
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Fib"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Resistance") || d.FullLabel.Contains("Base"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Target"));
    }

    [Fact]
    public void TrendLineObject_GetCalculatedValues_DoesNotContainAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 10);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 200m);

        using var trendLine = new TrendLineObject(p1, p2);

        var midTime = new DateTime(2026, 1, 5, 12, 0, 0);
        var values = trendLine.GetCalculatedValues(midTime);

        Assert.NotEmpty(values);
        var linePrice = Assert.Single(values, v => v.Key == "TrendLinePrice");
        Assert.Equal(150m, linePrice.NumericValue);

        // Verify anchor prices (P1Price, P2Price) are NOT present
        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));
    }
}

