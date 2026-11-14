using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public sealed class RegressionTrendExtensionsTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1);

    private static List<CoreCandleData> BuildCandles(int totalBars, bool skipWeekends = false)
    {
        var candles = new List<CoreCandleData>(totalBars);
        DateTime day = Epoch;
        for (int i = 0; i < totalBars; i++)
        {
            if (skipWeekends)
            {
                while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    day = day.AddDays(1);
                }
            }

            decimal close = 100m + i * 5m + (i % 2 == 0 ? 1m : -1m);
            candles.Add(new CoreCandleData(day, close, close, close, close, 1000));
            day = day.AddDays(1);
        }
        return candles;
    }

    private static decimal GetValue(RegressionTrendObject obj, DateTime time, string key)
        => Assert.Single(obj.GetCalculatedValues(time), v => v.Key == key).NumericValue!.Value;

    [Fact]
    public void Defaults_MatchOriginalBehavior_AndClampingEnforcesRange()
    {
        var candles = BuildCandles(10);
        using var obj = new RegressionTrendObject(new ChartPoint(candles[0].Timestamp, 0m), new ChartPoint(candles[5].Timestamp, 0m));
        obj.Recalculate(candles);

        Assert.Equal(2.0m, obj.StdDevMultiplier);
        Assert.False(obj.ExtendRight);

        DateTime query = candles[3].Timestamp;
        decimal center = GetValue(obj, query, "RegressionCenter");
        decimal upper2 = GetValue(obj, query, "RegressionUpper2");
        decimal lower2 = GetValue(obj, query, "RegressionLower2");
        decimal stdDev = GetValue(obj, query, "StdDev");

        Assert.Equal(center + stdDev * 2.0m, upper2);
        Assert.Equal(center - stdDev * 2.0m, lower2);

        obj.StdDevMultiplier = 0.01m;
        Assert.Equal(0.1m, obj.StdDevMultiplier);

        obj.StdDevMultiplier = 25m;
        Assert.Equal(10.0m, obj.StdDevMultiplier);
    }

    [Fact]
    public void StdDevMultiplier_One_HalvesBandWidthInValuesAndHitTest()
    {
        var candles = BuildCandles(10);
        using var defaultObj = new RegressionTrendObject(new ChartPoint(candles[0].Timestamp, 0m), new ChartPoint(candles[5].Timestamp, 0m));
        using var halfObj = new RegressionTrendObject(new ChartPoint(candles[0].Timestamp, 0m), new ChartPoint(candles[5].Timestamp, 0m))
        {
            StdDevMultiplier = 1.0m
        };
        defaultObj.Recalculate(candles);
        halfObj.Recalculate(candles);

        DateTime query = candles[2].Timestamp;
        decimal defaultWidth = GetValue(defaultObj, query, "RegressionUpper2") - GetValue(defaultObj, query, "RegressionLower2");
        decimal halfWidth = GetValue(halfObj, query, "RegressionUpper2") - GetValue(halfObj, query, "RegressionLower2");
        Assert.Equal(defaultWidth / 2m, halfWidth);

        // Point at +1.5 StdDev from center: inside default (2.0), outside half (1.0).
        decimal center = GetValue(defaultObj, query, "RegressionCenter");
        decimal stdDev = GetValue(defaultObj, query, "StdDev");
        var transform = new PixelCoordinateTransform();
        Point probe = transform.ChartToScreen(new ChartPoint(query, center + stdDev * 1.5m));
        Assert.True(defaultObj.HitTest(probe, transform));
        Assert.False(halfObj.HitTest(probe, transform));
    }

    [Fact]
    public void ExtendRight_True_ReturnsExactExtrapolatedValueOnThirdTrailingBar_AndFalsePreservesElapsedTimeBranch()
    {
        // Use weekday candles so weekend gaps distinguish bar-index extrapolation from elapsed-seconds extrapolation.
        var candles = BuildCandles(15, skipWeekends: true);
        int rangeCount = 8; // indices 0..7
        DateTime start = candles[0].Timestamp;
        DateTime end = candles[rangeCount - 1].Timestamp;
        DateTime thirdTrailingBar = candles[rangeCount + 2].Timestamp; // j = 3 -> x = n + 2

        using var extended = new RegressionTrendObject(new ChartPoint(start, 0m), new ChartPoint(end, 0m))
        {
            ExtendRight = true
        };
        using var unextended = new RegressionTrendObject(new ChartPoint(start, 0m), new ChartPoint(end, 0m))
        {
            ExtendRight = false
        };

        extended.Recalculate(candles);
        unextended.Recalculate(candles);

        decimal slope = GetValue(extended, end, "Slope");
        decimal intercept = GetValue(extended, start, "RegressionCenter"); // x = 0 -> exact Intercept
        decimal expectedThird = slope * (rangeCount + 2) + intercept; // Slope * (n + 2) + Intercept
        decimal actualThird = GetValue(extended, thirdTrailingBar, "RegressionCenter");
        Assert.Equal(expectedThird, actualThird);

        // ExtendRight = false keeps the existing elapsed-seconds outside-range branch.
        double totalSec = (end - start).TotalSeconds;
        double curSec = (thirdTrailingBar - start).TotalSeconds;
        int elapsedX = (int)Math.Round((curSec / totalSec) * (rangeCount - 1));
        Assert.Equal(slope * elapsedX + intercept, GetValue(unextended, thirdTrailingBar, "RegressionCenter"));

        // After the final stored bar, ExtendRight = true also falls back to the existing outside-range branch.
        DateTime beyondLastBar = candles[^1].Timestamp.AddDays(30);
        Assert.Equal(GetValue(unextended, beyondLastBar, "RegressionCenter"), GetValue(extended, beyondLastBar, "RegressionCenter"));
    }

    [Fact]
    public void ExtendRight_LogarithmicAxis_RenderedLineMatchesProjectedExtrapolationWithinHalfPixel()
    {
        // Construct candles far apart in days (so x coordinates are well-spaced integer pixels) and strictly rising prices.
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 8; i++)
        {
            decimal price = 100m + i * 25m;
            candles.Add(new CoreCandleData(Epoch.AddDays(50 + i * 60), price, price, price, price, 1000));
        }

        using var obj = new RegressionTrendObject(
            new ChartPoint(candles[0].Timestamp, 0m),
            new ChartPoint(candles[3].Timestamp, 0m))
        {
            ExtendRight = true,
            Color = Colors.White,
            Thickness = 1.0
        };
        obj.Recalculate(candles);

        var transform = new PixelCoordinateTransform(logarithmicPrice: true);
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        decimal slope = GetValue(obj, candles[3].Timestamp, "Slope");
        decimal centerAtEnd = GetValue(obj, candles[3].Timestamp, "RegressionCenter");

        for (int j = 1; j <= 4; j++)
        {
            DateTime barTime = candles[3 + j].Timestamp;
            decimal expectedPrice = centerAtEnd + slope * j;
            Point projected = transform.ChartToScreen(new ChartPoint(barTime, expectedPrice));
            int colX = (int)Math.Round(projected.X);

            // Compute brightness-weighted Y centroid at column colX (line thickness = 1 px).
            double sumWeight = 0;
            double sumY = 0;
            int minY = Math.Max(0, (int)Math.Floor(projected.Y) - 3);
            int maxY = Math.Min(599, (int)Math.Ceiling(projected.Y) + 3);
            for (int y = minY; y <= maxY; y++)
            {
                byte luma = bitmap.GetPixel(colX, y).Red;
                if (luma > 0)
                {
                    sumWeight += luma;
                    sumY += luma * (y + 0.5); // Skia pixel row y is centered at y + 0.5
                }
            }

            Assert.True(sumWeight > 0, $"No rendered pixel found near x={colX}, y={projected.Y}");
            double centroidY = sumY / sumWeight;
            Assert.True(Math.Abs(centroidY - projected.Y) <= 0.5, $"Trailing bar j={j}: rendered y={centroidY:F3}, projected y={projected.Y:F3}");
        }
    }

    [Fact]
    public void ExtendRight_ZeroTrailingBars_AndTinyScreenDeltaX_BehavesSafely()
    {
        var candles = new List<CoreCandleData>(6);
        for (int i = 0; i < 6; i++)
        {
            decimal close = 100m + i * 0.2m;
            candles.Add(new CoreCandleData(Epoch.AddDays(i), close, close, close, close, 1000));
        }
        using var obj = new RegressionTrendObject(
            new ChartPoint(candles[0].Timestamp, 0m),
            new ChartPoint(candles[^1].Timestamp, 0m))
        {
            ExtendRight = true,
            Color = Colors.White,
            Thickness = 1.0
        };
        obj.Recalculate(candles);

        // Normal transform: extends beyond last bar to clipRight.
        var normalTransform = new PixelCoordinateTransform();
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, normalTransform);
        Assert.Contains(Enumerable.Range(0, 600), y => bitmap.GetPixel(750, y).Red > 0);

        // Compressed X transform where dx < 0.5 px between last two bars: must not extend.
        var compressed = new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);
        compressed.UpdateRange(Epoch, Epoch.AddDays(50000), 80m, 200m);
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, compressed);
        Assert.DoesNotContain(Enumerable.Range(0, 600), y => bitmap.GetPixel(750, y).Red > 0);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_ExposesStdDevMultiplierAndExtendRight()
    {
        var candles = BuildCandles(6);
        using var obj = new RegressionTrendObject(new ChartPoint(candles[0].Timestamp, 0m), new ChartPoint(candles[^1].Timestamp, 0m));

        var metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(RegressionTrendObject));
        var stdMeta = Assert.Single(metadata, m => m.Prop.Name == nameof(RegressionTrendObject.StdDevMultiplier));
        var extMeta = Assert.Single(metadata, m => m.Prop.Name == nameof(RegressionTrendObject.ExtendRight));
        Assert.Contains(DrawingParameterTags.Analysis, stdMeta.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, extMeta.Tags);

        var builder = new DrawingParameterViewBuilder();
        var hiddenCommonAndGeometry = new[] { DrawingParameterTags.Common, DrawingParameterTags.Geometry };
        var control = Assert.IsType<StackPanel>(builder.Build(obj, hiddenCommonAndGeometry));
        var cards = control.Children.OfType<Border>().ToList();
        Assert.NotEmpty(cards);

        bool hasNumeric = cards.SelectMany(c => ((StackPanel)c.Child!).Children).OfType<Grid>().Any(g => g.Children.OfType<NumericUpDown>().Any());
        bool hasCheckBox = cards.SelectMany(c => ((StackPanel)c.Child!).Children).OfType<CheckBox>().Any();
        Assert.True(hasNumeric);
        Assert.True(hasCheckBox);
    }
}
