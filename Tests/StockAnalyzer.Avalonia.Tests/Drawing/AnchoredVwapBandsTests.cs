using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public sealed class AnchoredVwapBandsTests
{
    private const int Width = 800;
    private const int Height = 600;
    private static readonly DateTime T0 = new(2026, 1, 1);
    private static readonly Color TestColor = Color.FromArgb(255, 64, 160, 240);

    private static List<CoreCandleData> BuildParityCandles()
    {
        var candles = new List<CoreCandleData>(60);
        for (int i = 0; i < 60; i++)
        {
            decimal c = 100m + (i % 7) * 2.5m + i * 0.25m;
            candles.Add(new CoreCandleData(T0.AddDays(i), c - 1m, c + 2m, c - 2m, c, 1000 + i * 50));
        }
        return candles;
    }

    private static GenericCoordinateTransform CreateTransform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(T0.AddDays(-5), T0.AddDays(60), 80m, 140m);
        return transform;
    }

    private static string RenderPngSha256(AnchoredVwapObject obj, ICoordinateTransform transform)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);
        using var image = surface.Snapshot();
        using var pngData = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToHexString(SHA256.HashData(pngData.AsSpan()));
    }

    [Fact]
    public void BandsHidden_RenderHashAndCalculatedValues_MatchOriginalBehavior()
    {
        var candles = new List<CoreCandleData>(10);
        for (int i = 0; i < 10; i++)
        {
            decimal p = 100m + i * 2m;
            candles.Add(new CoreCandleData(T0.AddDays(i), p, p + 3m, p - 2m, p + 1m, 1000 + i * 100));
        }
        using var obj = new AnchoredVwapObject(new ChartPoint(T0, 100m))
        {
            Color = TestColor,
            Thickness = 2.0
        };
        obj.Recalculate(candles);

        string hash = RenderPngSha256(obj, CreateTransform());
        Assert.Equal("2AC641451F163322E7882824C4585E113454B5E96587D13100FCAB677AFC86B1", hash);

        var values = obj.GetCalculatedValues(T0.AddDays(5));
        Assert.Equal(2, values.Count);
        Assert.Equal(new[] { "AnchorDate", "AnchoredVWAP" }, values.Select(v => v.Key).ToArray());
    }

    [Fact]
    public void CalculateAnchoredVwapWithDeviation_VwapMatchesExistingFunctionAcrossPriceTypes()
    {
        var candles = BuildParityCandles();
        foreach (PriceType priceType in new[] { PriceType.Typical, PriceType.Close, PriceType.Open })
        {
            var expected = VolumeAnalysis.CalculateAnchoredVwap(candles, priceType);
            var actual = VolumeAnalysis.CalculateAnchoredVwapWithDeviation(candles, priceType);

            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Time, actual[i].Time);
                Assert.Equal(expected[i].Vwap, actual[i].Vwap);
                Assert.NotNull(actual[i].StdDev);
            }
        }
    }

    [Fact]
    public void CalculateAnchoredVwapWithDeviation_TwoPointHandCalculation_MatchesPlan02T4()
    {
        // Same two candles as CoreAnchoredVwapDateAndBandsTests.Bands_HandCalculatedExample:
        // Day 0: price = 10, vol = 1 -> Vwap = 10, StdDev = 0
        // Day 1: price = 20, vol = 3 -> Vwap = 17.5, Sigma = sqrt(18.75) = 4.330127018922193...
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2026, 3, 2), 10m, 10m, 10m, 10m, 1),
            new(new DateTime(2026, 3, 3), 20m, 20m, 20m, 20m, 3)
        };

        using var vwap = new AnchoredVwapObject(new ChartPoint(candles[0].Timestamp, 10m))
        {
            ShowBand2 = true,
            Band2Multiplier = 2.0m
        };
        vwap.Recalculate(candles);

        var values = vwap.GetCalculatedValues(candles[1].Timestamp);
        Assert.Equal(new[] { "AnchorDate", "AnchoredVWAP", "Upper2", "Lower2" }, values.Select(v => v.Key).ToArray());
        Assert.Equal(17.5m, values[1].NumericValue);

        decimal expectedUpper2 = 17.5m + 2m * 4.330127018922193m;
        decimal expectedLower2 = 17.5m - 2m * 4.330127018922193m;
        Assert.True(Math.Abs(expectedUpper2 - values[2].NumericValue!.Value) < 1e-12m);
        Assert.True(Math.Abs(expectedLower2 - values[3].NumericValue!.Value) < 1e-12m);
    }

    [Fact]
    public void ZeroCumulativeVolumeBars_ProduceNullStdDev_AndBreakBandRenderingWithoutAllocations()
    {
        var candles = new List<CoreCandleData>
        {
            new(T0, 100m, 100m, 100m, 100m, 0),
            new(T0.AddDays(1), 102m, 102m, 102m, 102m, 0),
            new(T0.AddDays(2), 104m, 104m, 104m, 104m, 100),
            new(T0.AddDays(3), 108m, 108m, 108m, 108m, 100)
        };

        var series = VolumeAnalysis.CalculateAnchoredVwapWithDeviation(candles);
        Assert.Null(series[0].StdDev);
        Assert.Null(series[1].StdDev);
        Assert.NotNull(series[2].StdDev);
        Assert.NotNull(series[3].StdDev);

        using var obj = new AnchoredVwapObject(new ChartPoint(T0, 100m))
        {
            Color = Colors.White,
            Thickness = 1.0,
            ShowBand1 = true,
            ShowBand2 = true
        };
        obj.Recalculate(candles);

        // Bar 1 still has cumulative volume == 0 -> band numeric values remain null.
        var earlyValues = obj.GetCalculatedValues(T0.AddDays(1));
        Assert.Equal(6, earlyValues.Count);
        Assert.Null(earlyValues[2].NumericValue);
        Assert.Null(earlyValues[3].NumericValue);
        Assert.Null(earlyValues[4].NumericValue);
        Assert.Null(earlyValues[5].NumericValue);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        var transform = CreateTransform();
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        long bytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Recalculate_SortedBinarySearchMatchesUnsortedFallback_AndClampsMultipliers()
    {
        var sorted = BuildParityCandles();
        var shuffled = sorted.OrderByDescending(c => c.Timestamp).ToList();
        var anchor = new ChartPoint(sorted[15].Timestamp, 100m);

        using var fastObj = new AnchoredVwapObject(anchor) { ShowBand1 = true };
        using var fallbackObj = new AnchoredVwapObject(anchor) { ShowBand1 = true };

        fastObj.Recalculate(sorted);
        fallbackObj.Recalculate(shuffled);

        Assert.Equal(fallbackObj.VwapCurve, fastObj.VwapCurve);
        Assert.Equal(
            fallbackObj.GetCalculatedValues(sorted[40].Timestamp).Select(v => v.NumericValue),
            fastObj.GetCalculatedValues(sorted[40].Timestamp).Select(v => v.NumericValue));

        // Anchor past end clears curve safely.
        using var emptyAfterAnchor = new AnchoredVwapObject(new ChartPoint(T0.AddDays(500), 100m)) { ShowBand1 = true };
        emptyAfterAnchor.Recalculate(sorted);
        Assert.Empty(emptyAfterAnchor.VwapCurve);
        Assert.Empty(emptyAfterAnchor.GetCalculatedValues(T0.AddDays(500)));

        // Multiplier clamping [0.1, 10.0].
        fastObj.Band1Multiplier = 0.01m;
        fastObj.Band2Multiplier = 99m;
        Assert.Equal(0.1m, fastObj.Band1Multiplier);
        Assert.Equal(10.0m, fastObj.Band2Multiplier);
    }

    [Fact]
    public void GetCalculatedValues_ReturnsOnlyEnabledBandsInExactOrder()
    {
        var candles = BuildParityCandles();
        DateTime query = T0.AddDays(30);

        using var onlyBand1 = new AnchoredVwapObject(new ChartPoint(T0.AddDays(5), 100m)) { ShowBand1 = true };
        onlyBand1.Recalculate(candles);
        Assert.Equal(
            new[] { "AnchorDate", "AnchoredVWAP", "Upper1", "Lower1" },
            onlyBand1.GetCalculatedValues(query).Select(v => v.Key).ToArray());

        using var onlyBand2 = new AnchoredVwapObject(new ChartPoint(T0.AddDays(5), 100m)) { ShowBand2 = true };
        onlyBand2.Recalculate(candles);
        Assert.Equal(
            new[] { "AnchorDate", "AnchoredVWAP", "Upper2", "Lower2" },
            onlyBand2.GetCalculatedValues(query).Select(v => v.Key).ToArray());

        using var both = new AnchoredVwapObject(new ChartPoint(T0.AddDays(5), 100m)) { ShowBand1 = true, ShowBand2 = true };
        both.Recalculate(candles);
        Assert.Equal(
            new[] { "AnchorDate", "AnchoredVWAP", "Upper1", "Lower1", "Upper2", "Lower2" },
            both.GetCalculatedValues(query).Select(v => v.Key).ToArray());

        // Every text of the list (the date and all numbers, bands or not) uses DrawingValueFormat.Culture regardless of the calling thread culture.
        foreach (string cultureName in new[] { "fr-FR", "de-DE" })
        {
            AssertTextsAreCultureFixed(both, query, cultureName, expectedItems: 6);
            AssertTextsAreCultureFixed(onlyBand1, query, cultureName, expectedItems: 4);
            AssertTextsAreCultureFixed(onlyBand2, query, cultureName, expectedItems: 4);
        }
    }

    [Fact]
    public void GetCalculatedValues_WithoutBands_UsesTheFixedCultureForTheDateAndTheVwap()
    {
        using var plain = new AnchoredVwapObject(new ChartPoint(T0.AddDays(5), 100m));
        plain.Recalculate(BuildParityCandles());

        foreach (string cultureName in new[] { "fr-FR", "de-DE" })
        {
            AssertTextsAreCultureFixed(plain, T0.AddDays(30), cultureName, expectedItems: 2);
        }
    }

    /// <summary>AnchorDate is "yyyy/MM/dd" with a literal '/' (the custom-format '/' would be the culture's date separator, '.' in de-DE); every other item is a number with '.'.</summary>
    private static void AssertTextsAreCultureFixed(AnchoredVwapObject obj, DateTime query, string cultureName, int expectedItems)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var values = obj.GetCalculatedValues(query);

            Assert.Equal(expectedItems, values.Count);
            Assert.Equal(obj.Points[0].Time.ToString("yyyy/MM/dd", CultureInfo.GetCultureInfo("en-US")), values[0].FormattedText);
            Assert.Contains('/', values[0].FormattedText);
            for (int i = 1; i < values.Count; i++)
            {
                Assert.Contains('.', values[i].FormattedText);
                Assert.DoesNotContain(',', values[i].FormattedText);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    // ---- HitTest of the bands (WebAiPeerReview plan T5; owner decision E-3 = a: only the displayed bands) ----

    private const int ProbeBar = 30;
    private const double MinBandSeparationPx = 20.0;   // the centre line and the band must be far enough apart for a probe to tell them apart

    private static AnchoredVwapObject CreateBanded(bool showBand1, bool showBand2)
    {
        var obj = new AnchoredVwapObject(new ChartPoint(T0, 100m))
        {
            ShowBand1 = showBand1,
            ShowBand2 = showBand2,
            Band1Multiplier = 1.0m,
            Band2Multiplier = 2.0m
        };
        obj.Recalculate(BuildParityCandles());
        return obj;
    }

    /// <summary>Screen position of bar <see cref="ProbeBar"/> of the band line VWAP + signedMultiplier x sigma.</summary>
    private static global::Avalonia.Point BandPoint(ICoordinateTransform transform, decimal signedMultiplier, int bar = ProbeBar)
    {
        var series = VolumeAnalysis.CalculateAnchoredVwapWithDeviation(BuildParityCandles());
        var (time, vwap, stdDev) = series[bar];
        return transform.ChartToScreen(new ChartPoint(time, vwap + signedMultiplier * stdDev!.Value));
    }

    [Fact]
    public void HitTest_DisplayedBandsAreHit_HiddenBandsAreNot()
    {
        var transform = CreateTransform();
        global::Avalonia.Point upper2 = BandPoint(transform, 2.0m);
        global::Avalonia.Point lower1 = BandPoint(transform, -1.0m);
        global::Avalonia.Point centre = BandPoint(transform, 0m);
        Assert.True(Math.Abs(upper2.Y - centre.Y) > MinBandSeparationPx && Math.Abs(lower1.Y - centre.Y) > MinBandSeparationPx, "the test data must separate the lines");

        using var both = CreateBanded(showBand1: true, showBand2: true);
        using var only1 = CreateBanded(showBand1: true, showBand2: false);
        using var only2 = CreateBanded(showBand1: false, showBand2: true);
        using var none = CreateBanded(showBand1: false, showBand2: false);

        Assert.True(both.HitTest(upper2, transform));
        Assert.True(both.HitTest(lower1, transform));

        Assert.True(only2.HitTest(upper2, transform));
        Assert.False(only2.HitTest(lower1, transform));   // Band 1 is not displayed

        Assert.True(only1.HitTest(lower1, transform));
        Assert.False(only1.HitTest(upper2, transform));   // Band 2 is not displayed

        Assert.False(none.HitTest(upper2, transform));
        Assert.False(none.HitTest(lower1, transform));
        Assert.True(none.HitTest(centre, transform));     // the centre line is unchanged
    }

    [Fact]
    public void HitTest_FarFromEveryDisplayedLine_IsNotHit()
    {
        var transform = CreateTransform();
        using var both = CreateBanded(showBand1: true, showBand2: true);
        global::Avalonia.Point upper2 = BandPoint(transform, 2.0m);

        Assert.False(both.HitTest(new global::Avalonia.Point(upper2.X, upper2.Y - 40), transform));   // beyond the outer band
        Assert.False(both.HitTest(new global::Avalonia.Point(Width - 1, 5), transform));
    }

    [Fact]
    public void HitTest_BarsWithoutStdDev_DoNotBreakTheBandTest()
    {
        // Zero volume until bar 2: sigma is null for bars 0 and 1, so those bars have no band point.
        var candles = new List<CoreCandleData>
        {
            new(T0, 100m, 100m, 100m, 100m, 0),
            new(T0.AddDays(1), 102m, 102m, 102m, 102m, 0),
            new(T0.AddDays(2), 104m, 104m, 104m, 104m, 100),
            new(T0.AddDays(3), 110m, 110m, 110m, 110m, 100),
            new(T0.AddDays(4), 116m, 116m, 116m, 116m, 100)
        };
        using var obj = new AnchoredVwapObject(new ChartPoint(T0, 100m)) { ShowBand2 = true, Band2Multiplier = 2.0m };
        obj.Recalculate(candles);
        var transform = CreateTransform();
        var series = VolumeAnalysis.CalculateAnchoredVwapWithDeviation(candles);
        Assert.Null(series[0].StdDev);
        Assert.Null(series[1].StdDev);

        // A band vertex of a bar that has sigma is hit; a point at the position bar 1 would have (no sigma, no band point) is not.
        var onBar4 = transform.ChartToScreen(new ChartPoint(series[4].Time, series[4].Vwap + 2m * series[4].StdDev!.Value));
        Assert.True(obj.HitTest(onBar4, transform));

        var wouldBeBar1 = transform.ChartToScreen(new ChartPoint(series[1].Time, series[1].Vwap + 40m));
        Assert.False(obj.HitTest(wouldBeBar1, transform));
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_ExposesAllFourBandPropertiesWithAnalysisTag()
    {
        using var obj = new AnchoredVwapObject(new ChartPoint(T0, 100m));
        var metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(AnchoredVwapObject));

        foreach (string propName in new[]
        {
            nameof(AnchoredVwapObject.ShowBand1),
            nameof(AnchoredVwapObject.Band1Multiplier),
            nameof(AnchoredVwapObject.ShowBand2),
            nameof(AnchoredVwapObject.Band2Multiplier)
        })
        {
            var entry = Assert.Single(metadata, m => m.Prop.Name == propName);
            Assert.Contains(DrawingParameterTags.Analysis, entry.Tags);
        }

        var builder = new DrawingParameterViewBuilder();
        var hidden = new[] { DrawingParameterTags.Common, DrawingParameterTags.Geometry };
        var control = Assert.IsType<StackPanel>(builder.Build(obj, hidden));
        Assert.NotEmpty(control.Children.OfType<Border>());
    }
}
