using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using AColor = global::Avalonia.Media.Color;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The settings added to the GARCH cone (price source, fill colour and opacity) and the Robust Regression Trend (period, price source, fill colour and opacity): behaviour in the chart object.</summary>
public sealed class RangeToolFillAndPriceTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);
    private static readonly AColor Blue = AColor.FromRgb(0, 0, 255);
    private static readonly AColor Red = AColor.FromRgb(255, 0, 0);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static List<CoreCandleData> MakeCandles(int count)
    {
        var list = new List<CoreCandleData>(count);
        double state = 4242.0;
        double variance = 1.0;
        decimal close = 100m;
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                state = ((state * 1103515245.0) + 12345.0) % 2147483648.0;
                double shock = ((state / 2147483648.0) - 0.5) * 3.4641016151377544;
                double ret = Math.Sqrt(variance) * shock;
                variance = 0.2 + (0.1 * ret * ret) + (0.8 * variance);
                close = Math.Round(close * (decimal)Math.Exp(ret * 0.01), 6);
            }

            list.Add(new CoreCandleData(BaseTime.AddDays(i), close - 0.1m, close + 0.9m, close - 0.8m, close, 1000L + i));
        }

        return list;
    }

    private static LinearCoordinateTransform MakeTransform() => new(BaseTime, BaseTime.AddDays(170), 60m, 160m, 900, 600);

    private static ChartPoint At(List<CoreCandleData> candles, int index) => new(candles[index].Timestamp, candles[index].Close);

    private static SKBitmap RenderTransparent(IChartObject obj, LinearCoordinateTransform transform)
    {
        var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        obj.Render(canvas, transform);
        return bitmap;
    }

    private static SKColor PixelAt(SKBitmap bitmap, global::Avalonia.Point p) => bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));

    // ----- GARCH cone -----

    private static GarchVolatilityConeObject Cone(List<CoreCandleData> candles) => new(At(candles, 20), At(candles, 100)) { Color = Blue };

    /// <summary>A point inside the 1 sigma band, 10 bars after the range, a third of the way from the centre to the upper edge (away from the dashed centre line and the dashed edge).</summary>
    private static global::Avalonia.Point InsideInnerBand(GarchVolatilityConeObject obj, List<CoreCandleData> candles, LinearCoordinateTransform transform)
    {
        GarchVolatilityCone cone = obj.Cone;
        Assert.True(cone.IsOk);
        decimal upper = cone.Upper1[9]!.Value;
        decimal price = cone.CenterPrice + ((upper - cone.CenterPrice) / 3m);
        global::Avalonia.Point point = transform.ChartToScreen(new ChartPoint(candles[100].Timestamp.AddDays(10), price));
        global::Avalonia.Point centre = transform.ChartToScreen(new ChartPoint(candles[100].Timestamp.AddDays(10), cone.CenterPrice));
        Assert.True(Math.Abs(point.Y - centre.Y) >= 4.0, "the sample point must be clear of the centre line");
        return point;
    }

    [Theory]
    [InlineData(49, 249)]    // 2 x 49 = 98 %: 255 * 98 / 100
    [InlineData(50, 255)]    // 2 x 50 = 100 %
    [InlineData(51, 255)]    // 2 x 51 = 102 %: clamped to 100 %, not wrapped around
    [InlineData(100, 255)]
    [InlineData(0, 0)]
    public void Garch_InnerFill_IsTwiceTheOpacityButNeverAboveFull(int opacity, int expectedAlpha)
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using GarchVolatilityConeObject obj = Cone(candles);
        obj.ShowOuterBand = false;
        obj.FillOpacity = opacity;
        obj.Recalculate(candles);
        LinearCoordinateTransform transform = MakeTransform();

        using SKBitmap bitmap = RenderTransparent(obj, transform);

        Assert.InRange(PixelAt(bitmap, InsideInnerBand(obj, candles, transform)).Alpha, Math.Max(0, expectedAlpha - 1), Math.Min(255, expectedAlpha + 1));
    }

    [Fact]
    public void Garch_InnerFillAlpha_NeverDecreasesWhenTheOpacityGrows()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        LinearCoordinateTransform transform = MakeTransform();
        int previous = -1;
        foreach (int opacity in new[] { 0, 10, 25, 49, 50, 51, 75, 100 })
        {
            using GarchVolatilityConeObject obj = Cone(candles);
            obj.ShowOuterBand = false;
            obj.FillOpacity = opacity;
            obj.Recalculate(candles);
            using SKBitmap bitmap = RenderTransparent(obj, transform);
            int alpha = PixelAt(bitmap, InsideInnerBand(obj, candles, transform)).Alpha;
            Assert.True(alpha >= previous, $"opacity {opacity}: alpha {alpha} after {previous}");
            previous = alpha;
        }
    }

    [Fact]
    public void Garch_FillColor_DefaultsToTheDefaultDrawingColour_AndTheChosenColourIsUsedForTheFill()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using GarchVolatilityConeObject obj = Cone(candles);   // the line colour is blue, the fill colour stays the default drawing colour
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(15, obj.FillOpacity);
        obj.ShowOuterBand = false;
        obj.FillOpacity = 50;
        obj.Recalculate(candles);
        LinearCoordinateTransform transform = MakeTransform();
        global::Avalonia.Point point = InsideInnerBand(obj, candles, transform);

        using (SKBitmap byDefault = RenderTransparent(obj, transform))
        {
            SKColor p = PixelAt(byDefault, point);
            AColor expected = DrawingThemeContext.DefaultColor;
            Assert.True(Math.Abs(p.Red - expected.R) <= 2 && Math.Abs(p.Green - expected.G) <= 2 && Math.Abs(p.Blue - expected.B) <= 2, $"default drawing colour expected, got {p}");
        }

        obj.FillColor = Red;
        using (SKBitmap chosen = RenderTransparent(obj, transform))
        {
            SKColor p = PixelAt(chosen, point);
            Assert.True(p.Red > 200 && p.Blue < 40, $"chosen colour expected, got {p}");
        }
    }

    [Fact]
    public void Garch_PriceSource_DefaultsToCloseAndFeedsTheCone()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using GarchVolatilityConeObject obj = Cone(candles);
        Assert.Equal(PriceType.Close, obj.PriceSource);
        obj.Recalculate(candles);
        Assert.Equal(candles[100].Close, obj.Cone.CenterPrice);
        Assert.Equal(GarchVolatilityConeValues.Calculate(candles, 20, 100, obj.ForecastBars).Upper2, obj.Cone.Upper2);

        obj.PriceSource = PriceType.Median;
        obj.Recalculate(candles);
        Assert.Equal((candles[100].High + candles[100].Low) / 2m, obj.Cone.CenterPrice);
        Assert.Equal(GarchVolatilityConeValues.Calculate(candles, 20, 100, obj.ForecastBars, PriceType.Median).Upper2, obj.Cone.Upper2);
    }

    [Fact]
    public void Garch_ANonPositivePrice_ShowsTheStateAndRendersWithoutError()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        candles[60] = new CoreCandleData(candles[60].Timestamp, -4m, -4m, -4m, -4m, 1L);
        using GarchVolatilityConeObject obj = Cone(candles);
        obj.IsSelected = true;
        obj.Recalculate(candles);

        Assert.Equal(GarchConeStatus.NonPositivePrice, obj.Cone.Status);
        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[100].Timestamp, null);
        Assert.Equal("State", values[0].Key);
        Assert.Equal("Prices must be above zero", values[0].FormattedText);
        using SKBitmap bitmap = RenderTransparent(obj, MakeTransform());
        Assert.Contains(bitmap.Pixels, p => p.Alpha > 0);   // the range edges and the state text are drawn
    }

    [Fact]
    public void Garch_NewSettings_SurviveBothPersistencePaths_AndAnOldFileLoadsTheDefaults()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using GarchVolatilityConeObject obj = Cone(candles);
        obj.PriceSource = PriceType.HeikinAshiClose;
        obj.FillColor = Red;
        obj.FillOpacity = 33;

        string json = JsonSerializer.Serialize<IChartObject>(obj, JsonOptions);
        using var restored = Assert.IsType<GarchVolatilityConeObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        AssertSettings(restored);

        var manager = new ChartObjectManager();
        manager.AddObject(obj);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("GARCH", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        AssertSettings(Assert.IsType<GarchVolatilityConeObject>(materialized.Objects[ChartDrawingContextType.Standard].Single()));

        using GarchVolatilityConeObject plain = Cone(candles);
        string plainJson = JsonSerializer.Serialize<IChartObject>(plain, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(plainJson);
        var legacy = new Dictionary<string, JsonElement>(document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
        legacy.Remove("priceSource");
        legacy.Remove("fillColor");
        using var old = Assert.IsType<GarchVolatilityConeObject>(JsonSerializer.Deserialize<IChartObject>(JsonSerializer.Serialize(legacy), JsonOptions));
        Assert.Equal(PriceType.Close, old.PriceSource);
        Assert.Equal(DrawingThemeContext.DefaultColor, old.FillColor);

        static void AssertSettings(GarchVolatilityConeObject r)
        {
            Assert.Equal(PriceType.HeikinAshiClose, r.PriceSource);
            Assert.Equal(Red, r.FillColor);
            Assert.Equal(33, r.FillOpacity);
        }
    }

    // ----- Robust Regression Trend -----

    private static RobustRegressionTrendObject Robust(List<CoreCandleData> candles) => new(At(candles, 20), At(candles, 100)) { Color = Blue };

    /// <summary>A point inside the band at the middle of the fitted bars, half way from the line to the upper edge.</summary>
    private static global::Avalonia.Point InsideBand(RangeCurveObjectBase obj, LinearCoordinateTransform transform)
    {
        RangeCurve curve = obj.Curve;
        Assert.True(curve.Count >= 4 && curve.BandHalfWidth > 0m);
        int index = curve.FittedCount / 2;
        global::Avalonia.Point onLine = transform.ChartToScreen(new ChartPoint(curve.Times[index], curve.Prices[index]));
        global::Avalonia.Point point = transform.ChartToScreen(new ChartPoint(curve.Times[index], curve.Prices[index] + (curve.BandHalfWidth / 2m)));
        Assert.True(Math.Abs(point.Y - onLine.Y) >= 5.0, $"the band is too narrow to sample ({Math.Abs(point.Y - onLine.Y)} px)");
        return point;
    }

    /// <summary>A point in the same column as <see cref="InsideBand"/> but well above the band: only the faint range background lies there.</summary>
    private static global::Avalonia.Point AboveBand(RangeCurveObjectBase obj, LinearCoordinateTransform transform)
    {
        RangeCurve curve = obj.Curve;
        int index = curve.FittedCount / 2;
        return transform.ChartToScreen(new ChartPoint(curve.Times[index], curve.Prices[index] + (curve.BandHalfWidth * 2.5m)));
    }

    /// <summary>The alpha of a layer of alpha <paramref name="top"/> drawn over a layer of alpha <paramref name="below"/> (source-over).</summary>
    private static int Over(int top, int below) => (int)Math.Round(top + (below * (255 - top) / 255.0));

    [Fact]
    public void Robust_DefaultFillOpacityIs15_AndTheSiblingKeepsTheConstantAlpha28()
    {
        Assert.Equal(15, RobustRegressionTrendObject.DefaultFillOpacity);
        Assert.Equal(DrawingFillOpacity.DefaultPercent, RobustRegressionTrendObject.DefaultFillOpacity);
        Assert.Equal(38, DrawingFillOpacity.ToAlpha(RobustRegressionTrendObject.DefaultFillOpacity));

        List<CoreCandleData> candles = MakeCandles(200);
        LinearCoordinateTransform transform = MakeTransform();
        using var robust = Robust(candles);
        robust.Recalculate(candles);
        using var weighted = new WeightedRegressionTrendObject(At(candles, 20), At(candles, 100)) { Color = Blue };
        weighted.Recalculate(candles);

        using SKBitmap robustBitmap = RenderTransparent(robust, transform);
        using SKBitmap weightedBitmap = RenderTransparent(weighted, transform);
        // The band fill lies over the faint background of the range, which is measured next to the band.
        int robustBelow = PixelAt(robustBitmap, AboveBand(robust, transform)).Alpha;
        int weightedBelow = PixelAt(weightedBitmap, AboveBand(weighted, transform)).Alpha;
        Assert.InRange(PixelAt(robustBitmap, InsideBand(robust, transform)).Alpha, Over(38, robustBelow) - 1, Over(38, robustBelow) + 1);
        Assert.InRange(PixelAt(weightedBitmap, InsideBand(weighted, transform)).Alpha, Over(28, weightedBelow) - 1, Over(28, weightedBelow) + 1);   // unchanged: BandFillAlpha = 28
    }

    [Fact]
    public void Robust_FillOpacityAndColor_ChangeTheBand()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        LinearCoordinateTransform transform = MakeTransform();
        using var robust = Robust(candles);
        robust.Recalculate(candles);

        robust.FillOpacity = 0;
        using (SKBitmap none = RenderTransparent(robust, transform))
        {
            // The middle of the band carries no fill: only the faint range background that lies next to the band too.
            Assert.Equal(PixelAt(none, AboveBand(robust, transform)).Alpha, PixelAt(none, InsideBand(robust, transform)).Alpha);
        }

        robust.FillOpacity = 100;
        robust.FillColor = Red;
        using (SKBitmap full = RenderTransparent(robust, transform))
        {
            SKColor p = PixelAt(full, InsideBand(robust, transform));
            Assert.True(p.Alpha >= 254 && p.Red > 250 && p.Blue < 5, $"opaque red expected, got {p}");
        }

        robust.FillOpacity = 500;
        Assert.Equal(100, robust.FillOpacity);
    }

    [Fact]
    public void Robust_Period_DefaultsTo30_IsClamped_AndLimitsTheFittedBars()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using var robust = Robust(candles);
        Assert.Equal(30, robust.Period);
        robust.Recalculate(candles);
        Assert.Equal(30, robust.Fit.EffectiveBars);

        robust.Period = 12;
        robust.Recalculate(candles);
        Assert.Equal(12, robust.Fit.EffectiveBars);
        Assert.Equal(81, robust.Fit.RangeBars);
        Assert.Equal(RobustRegressionTrendValues.Calculate(candles, 20, 100, false, robust.StdDevMultiplier, false, 12).Slope, robust.Fit.Slope);

        robust.Period = 1;
        Assert.Equal(RobustRegressionMath.MinWindowLength, robust.Period);
        robust.Period = 99;
        Assert.Equal(99, robust.Period);   // above the old fixed 30: the tool maximum is the measured 500 (the default stays 30)
        robust.Period = RobustRegressionTrendValues.MaxBars + 1;
        Assert.Equal(RobustRegressionTrendValues.MaxBars, robust.Period);

        robust.Period = 2;
        robust.Recalculate(candles);
        Assert.True(robust.Fit.IsOk);
        Assert.Equal(2, robust.Fit.EffectiveBars);
        using SKBitmap bitmap = RenderTransparent(robust, MakeTransform());   // a possibly zero-width band renders without error
    }

    [Fact]
    public void Robust_Period_AboveThirty_FitsMoreBars_AndTheSettingsRangeFollowsTheToolMaximum()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using var robust = Robust(candles);   // the anchors span 81 bars
        robust.Period = 60;
        robust.Recalculate(candles);
        Assert.Equal(60, robust.Fit.EffectiveBars);
        Assert.Equal(81, robust.Fit.RangeBars);
        Assert.True(robust.Fit.IsTruncated);
        Assert.Equal(RobustRegressionTrendValues.Calculate(candles, 20, 100, false, robust.StdDevMultiplier, false, 60).Slope, robust.Fit.Slope);

        var range = (System.ComponentModel.DataAnnotations.RangeAttribute)typeof(RobustRegressionTrendObject).GetProperty(nameof(RobustRegressionTrendObject.Period))!
            .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RangeAttribute), false).Single();
        Assert.Equal(RobustRegressionTrendValues.MinBars, range.Minimum);
        Assert.Equal(RobustRegressionTrendValues.MaxBars, range.Maximum);
    }

    [Fact]
    public void Robust_PriceSource_DefaultsToCloseAndFeedsTheFit()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using var robust = Robust(candles);
        Assert.Equal(PriceType.Close, robust.PriceSource);
        robust.Recalculate(candles);
        decimal closeIntercept = robust.Fit.Intercept;

        robust.PriceSource = PriceType.Median;
        robust.Recalculate(candles);
        Assert.NotEqual(closeIntercept, robust.Fit.Intercept);
        Assert.Equal(RobustRegressionTrendValues.Calculate(candles, 20, 100, false, robust.StdDevMultiplier, false, 30, PriceType.Median).Intercept, robust.Fit.Intercept);
    }

    [Fact]
    public void Robust_NewSettings_SurviveBothPersistencePaths_AndAnOldFileLoadsTheDefaults()
    {
        List<CoreCandleData> candles = MakeCandles(200);
        using var robust = Robust(candles);
        robust.Period = 17;
        robust.PriceSource = PriceType.TrueHigh;
        robust.FillColor = Red;
        robust.FillOpacity = 42;

        string json = JsonSerializer.Serialize<IChartObject>(robust, JsonOptions);
        using var restored = Assert.IsType<RobustRegressionTrendObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        AssertSettings(restored);

        var manager = new ChartObjectManager();
        manager.AddObject(robust);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("ROB", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        AssertSettings(Assert.IsType<RobustRegressionTrendObject>(materialized.Objects[ChartDrawingContextType.Standard].Single()));

        using var plain = Robust(candles);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize<IChartObject>(plain, JsonOptions));
        var legacy = new Dictionary<string, JsonElement>(document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
        foreach (string key in new[] { "period", "priceSource", "fillColor", "fillOpacity" })
        {
            legacy.Remove(key);
        }

        using var old = Assert.IsType<RobustRegressionTrendObject>(JsonSerializer.Deserialize<IChartObject>(JsonSerializer.Serialize(legacy), JsonOptions));
        Assert.Equal(30, old.Period);
        Assert.Equal(PriceType.Close, old.PriceSource);
        Assert.Equal(DrawingThemeContext.DefaultColor, old.FillColor);
        Assert.Equal(15, old.FillOpacity);

        static void AssertSettings(RobustRegressionTrendObject r)
        {
            Assert.Equal(17, r.Period);
            Assert.Equal(PriceType.TrueHigh, r.PriceSource);
            Assert.Equal(Red, r.FillColor);
            Assert.Equal(42, r.FillOpacity);
        }
    }
}
