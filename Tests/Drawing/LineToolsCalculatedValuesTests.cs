using System;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class LineToolsCalculatedValuesTests
{
    [Fact]
    public void RayObject_GetCalculatedValues_ReturnsRayPriceAndSlope_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11); // 10 days
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 150m); // +50 over 10 days = +5/day

        using var ray = new RayObject(p1, p2);

        // At midpoint t1 + 5 days
        var midTime = new DateTime(2026, 1, 6);
        var values = ray.GetCalculatedValues(midTime, currentPrice: 130m);

        Assert.NotEmpty(values);

        // Check RayPrice
        var rayPrice = Assert.Single(values, v => v.Key == "RayPrice");
        Assert.Equal(125m, rayPrice.NumericValue);
        Assert.Contains("125.000", rayPrice.FormattedText);
        Assert.Contains("+5.000", rayPrice.FormattedText); // 130 - 125 = +5

        // Check Slope
        var slope = Assert.Single(values, v => v.Key == "Slope");
        Assert.Equal(5m, slope.NumericValue);
        Assert.Contains("+5.000 / day", slope.FormattedText);

        // Anchors must NOT be present
        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2") || v.Key.StartsWith("P"));

        // Incomplete points should return empty
        ray.Points.Clear();
        Assert.Empty(ray.GetCalculatedValues(midTime));
    }

    [Fact]
    public void VerticalLineObject_GetCalculatedValues_ReturnsDateAndOffset_ExcludesAnchorPrices()
    {
        var lineTime = new DateTime(2026, 1, 10, 12, 0, 0);
        var p1 = new ChartPoint(lineTime, 100m);

        var vert = new VerticalLineObject(p1);

        var cursorTime = new DateTime(2026, 1, 15, 12, 0, 0); // +5 days
        var values = vert.GetCalculatedValues(cursorTime);

        Assert.NotEmpty(values);

        var lineDate = Assert.Single(values, v => v.Key == "LineDate");
        Assert.Contains("2026-01-10", lineDate.FormattedText);

        var offset = Assert.Single(values, v => v.Key == "Offset");
        Assert.Equal(5.0m, offset.NumericValue);
        Assert.Contains("+5.0 days", offset.FormattedText);

        // Anchors must NOT be present
        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.StartsWith("P"));

        vert.Points.Clear();
        Assert.Empty(vert.GetCalculatedValues(cursorTime));
    }

    [Fact]
    public void PriceLabelObject_GetCalculatedValues_ReturnsPriceAndDiff_ExcludesAnchorPrices()
    {
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 5), 100m);

        using var label = new PriceLabelObject(p1, p2);

        var values = label.GetCalculatedValues(DateTime.Now, currentPrice: 110m);
        Assert.NotEmpty(values);

        var priceVal = Assert.Single(values, v => v.Key == "Price");
        Assert.Equal(100m, priceVal.NumericValue);

        var diffVal = Assert.Single(values, v => v.Key == "PriceDiff");
        Assert.Equal(10m, diffVal.NumericValue);
        Assert.Contains("+10.000", diffVal.FormattedText);
        Assert.Contains("+10.00%", diffVal.FormattedText);

        // Anchors must NOT be present
        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        label.Points.Clear();
        Assert.Empty(label.GetCalculatedValues(DateTime.Now));
    }

    [Fact]
    public void AngleObject_GetCalculatedValues_ReturnsLinePriceAndSlope_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 200m);

        using var angle = new AngleObject(p1, p2);

        var midTime = new DateTime(2026, 1, 6);
        var values = angle.GetCalculatedValues(midTime);

        Assert.NotEmpty(values);

        var linePrice = Assert.Single(values, v => v.Key == "LinePrice");
        Assert.Equal(150m, linePrice.NumericValue);

        var delta = Assert.Single(values, v => v.Key == "PriceDelta");
        Assert.Equal(100m, delta.NumericValue);
        Assert.Contains("+100.000", delta.FormattedText);
        Assert.Contains("+100.00%", delta.FormattedText);

        var slope = Assert.Single(values, v => v.Key == "Slope");
        Assert.Equal(10m, slope.NumericValue);
        Assert.Contains("+10.000 / day", slope.FormattedText);

        // Anchors must NOT be present
        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        angle.Points.Clear();
        Assert.Empty(angle.GetCalculatedValues(midTime));
    }

    [Fact]
    public void LineTextObject_GetCalculatedValues_ReturnsPriceAndAnnotations_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 120m);

        using var lineText = new LineTextObject(p1, p2)
        {
            TopText = "Breakout Level",
            BottomText = "Watch Volume"
        };

        var midTime = new DateTime(2026, 1, 6);
        var values = lineText.GetCalculatedValues(midTime);

        Assert.NotEmpty(values);

        var linePrice = Assert.Single(values, v => v.Key == "LinePrice");
        Assert.Equal(110m, linePrice.NumericValue);

        var top = Assert.Single(values, v => v.Key == "TopText");
        Assert.Equal("Breakout Level", top.FormattedText);

        var btm = Assert.Single(values, v => v.Key == "BottomText");
        Assert.Equal("Watch Volume", btm.FormattedText);

        // Anchors must NOT be present
        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        lineText.Points.Clear();
        Assert.Empty(lineText.GetCalculatedValues(midTime));
    }

    [Fact]
    public void ArrowObject_GetCalculatedValues_ReturnsDirectionAndDuration_ExcludesAnchorPrices()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 6);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 150m);

        using var arrow = new ArrowObject(p1, p2);

        var values = arrow.GetCalculatedValues(t2);

        Assert.NotEmpty(values);

        var arrowPrice = Assert.Single(values, v => v.Key == "ArrowPrice");
        Assert.Equal(150m, arrowPrice.NumericValue);

        var direction = Assert.Single(values, v => v.Key == "Direction");
        Assert.Contains("Bullish", direction.FormattedText);

        var delta = Assert.Single(values, v => v.Key == "PriceDelta");
        Assert.Equal(50m, delta.NumericValue);

        var duration = Assert.Single(values, v => v.Key == "Duration");
        Assert.Equal(5.0m, duration.NumericValue);
        Assert.Contains("5.0 days", duration.FormattedText);

        // Anchors must NOT be present
        Assert.DoesNotContain(values, v => v.Key.Contains("P1") || v.Key.Contains("P2"));

        arrow.Points.Clear();
        Assert.Empty(arrow.GetCalculatedValues(t2));
    }

    [Fact]
    public void ChartInformationDataProvider_Extract_IncludesAllLineTools()
    {
        var manager = new ChartObjectManager();
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 11);

        using var ray = new RayObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m));
        var vert = new VerticalLineObject(new ChartPoint(t1, 100m));
        using var priceLabel = new PriceLabelObject(new ChartPoint(t1, 100m));
        using var angle = new AngleObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m));
        using var lineText = new LineTextObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 120m)) { TopText = "Note" };
        using var arrow = new ArrowObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m));

        manager.AddObject(ray);
        manager.AddObject(vert);
        manager.AddObject(priceLabel);
        manager.AddObject(angle);
        manager.AddObject(lineText);
        manager.AddObject(arrow);

        var snapshot = ChartInformationDataProvider.Extract(
            candles: null,
            indicators: null,
            indicatorResults: null,
            objectManager: manager,
            targetTime: new DateTime(2026, 1, 6),
            targetPrice: 120m
        );

        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.Drawings);

        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Ray Price"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Line Date"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Price"));
        Assert.Contains(snapshot.Drawings, d => d.FullLabel.Contains("Direction"));
        Assert.Contains(snapshot.Drawings, d => d.FormattedValue.Contains("Note"));
    }
}
