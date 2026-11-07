using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using Xunit;
using StockAnalyzer.Core.Rendering;

namespace StockAnalyzer.Core.Tests.Rendering;

/// <summary>Plan 15, T1: the headless AI chart image (style, layout, price mapping, candles, volume) proved on decoded pixels and on the returned mapping.</summary>
public sealed class AiChartImageRendererTests
{
    private static readonly SKColor Up = AiChartImageStyle.Color(AiChartImageStyle.UpHex);
    private static readonly SKColor Down = AiChartImageStyle.Color(AiChartImageStyle.DownHex);
    private static readonly SKColor Doji = AiChartImageStyle.Color(AiChartImageStyle.DojiHex);
    private static readonly SKColor Volume = AiChartImageStyle.Color(AiChartImageStyle.VolumeHex);

    private static List<CandleData> Candles(int count, decimal basePrice = 1000m, int dojiAt = -1)
    {
        var candles = new List<CandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal open = basePrice + Math.Round((decimal)(10 * Math.Sin(i * 0.3)), 2);
            decimal close = i == dojiAt ? open : open + (i % 3 == 0 ? 6m : -4m);
            decimal high = Math.Max(open, close) + 3m;
            decimal low = Math.Min(open, close) - 3m;
            candles.Add(new CandleData(new DateTime(2026, 1, 1).AddDays(i), open, high, low, close, 1000 + (i * 10)));
        }

        return candles;
    }

    private static AiChartImageRequest Request(IReadOnlyList<CandleData> candles, int width = 1200, int height = 800, bool volume = true, AiChartBackground background = AiChartBackground.White)
        => new(candles, width, height, background, volume);

    private static SKBitmap Decode(AiChartImageResult result) => SKBitmap.Decode(result.Png);

    [Fact]
    public void TheSameRequestGivesTheSameDecodedPixels()
    {
        AiChartImageRequest request = Request(Candles(120, dojiAt: 5));

        using SKBitmap first = Decode(AiChartImageRenderer.Render(request));
        using SKBitmap second = Decode(AiChartImageRenderer.Render(request));

        Assert.Equal(request.WidthPx, first.Width);
        Assert.Equal(request.HeightPx, first.Height);
        Assert.True(first.Bytes.AsSpan().SequenceEqual(second.Bytes));
    }

    [Fact]
    public void ThePngHasTheRequestedSizeAndTheChosenBackground()
    {
        foreach ((AiChartBackground background, string hex) in new[] { (AiChartBackground.White, "#FFFFFF"), (AiChartBackground.LightGray, "#F5F5F5") })
        {
            AiChartImageResult result = AiChartImageRenderer.Render(Request(Candles(30), 640, 480, background: background));
            using SKBitmap bitmap = Decode(result);

            Assert.Equal(640, bitmap.Width);
            Assert.Equal(480, bitmap.Height);
            Assert.Equal(SKColor.Parse(hex), bitmap.GetPixel(0, 0));
        }
    }

    [Fact]
    public void TheCloseRowOfEveryBarHasTheUpDownOrDojiColourAtTheReportedPosition()
    {
        IReadOnlyList<CandleData> candles = Candles(150, dojiAt: 7);
        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles));
        using SKBitmap bitmap = Decode(result);

        for (int i = 0; i < candles.Count; i++)
        {
            AiChartBarPosition bar = result.Bars[i];
            SKColor expected = candles[i].Close > candles[i].Open ? Up : candles[i].Close < candles[i].Open ? Down : Doji;
            Assert.Equal(expected, bitmap.GetPixel((int)Math.Floor(bar.XPx), (int)Math.Floor(bar.CloseYPx)));
            Assert.Equal(expected, bitmap.GetPixel((int)Math.Floor(bar.XPx), (int)Math.Floor(bar.OpenYPx)));
        }
    }

    [Fact]
    public void TheCloseYOfTheReturnedMappingEqualsTheValueRecomputedFromTheAxes()
    {
        IReadOnlyList<CandleData> candles = Candles(100);
        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles));
        AiChartAxes axes = result.Axes;

        for (int i = 0; i < candles.Count; i++)
        {
            double ratio = (double)((candles[i].Close - axes.PriceMin) / (axes.PriceMax - axes.PriceMin));
            double expected = axes.PricePlotTopPx + ((axes.PricePlotBottomPx - axes.PricePlotTopPx) * (1 - ratio));
            Assert.InRange(Math.Abs(result.Bars[i].CloseYPx - expected), 0, 0.005 + 1e-9);
            double expectedX = axes.PricePlotLeftPx + ((i + 0.5) * (axes.PricePlotRightPx - axes.PricePlotLeftPx) / candles.Count);
            Assert.InRange(Math.Abs(result.Bars[i].XPx - expectedX), 0, 0.005 + 1e-9);
        }
    }

    [Fact]
    public void AValueGoesToAPixelAndBackWithinOnePixelOfPrice()
    {
        var transform = new AiChartPlotTransform(12, 20, 1116, 300, 900m, 1100m, 100);
        decimal onePixel = (1100m - 900m) / 300m;

        for (decimal price = 900m; price <= 1100m; price += 3.7m)
        {
            double y = transform.YPx(price);
            Assert.InRange(Math.Abs(transform.ValueAt(y) - price), 0m, onePixel / 1000m);
            Assert.InRange(Math.Abs(transform.ValueAt(AiChartPlotTransform.Snap(y)) - price), 0m, onePixel);
        }

        Assert.Equal(20.0, transform.YPx(1100m), 9);
        Assert.Equal(320.0, transform.YPx(900m), 9);
    }

    [Fact]
    public void TheBodyIsWiderThanTheWickAndNeverLessThanThreeOddPixels()
    {
        Assert.Equal(3, AiChartImageRenderer.BodyWidthPx(3.2));
        Assert.Equal(3, AiChartImageRenderer.BodyWidthPx(4.46));
        Assert.Equal(5, AiChartImageRenderer.BodyWidthPx(6.4));
        Assert.Equal(3, AiChartImageRenderer.BodyWidthPx(2.0));
        Assert.All(new[] { 3.0, 3.2, 4.0, 4.46, 5.5, 6.4, 9.9, 16.0 }, w => Assert.Equal(1, AiChartImageRenderer.BodyWidthPx(w) % 2));
    }

    [Fact]
    public void TheWickIsOnePixelWideAndTheBodyIsWiderAtTheBodyRows()
    {
        IReadOnlyList<CandleData> candles = Candles(60);
        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles));
        using SKBitmap bitmap = Decode(result);
        int checkedBars = 0;

        for (int i = 0; i < candles.Count; i++)
        {
            AiChartBarPosition bar = result.Bars[i];
            SKColor color = candles[i].Close > candles[i].Open ? Up : Down;
            int column = (int)Math.Floor(bar.XPx);
            int highRow = (int)Math.Floor(bar.HighYPx);
            int bodyTop = (int)Math.Floor(Math.Min(bar.OpenYPx, bar.CloseYPx));
            int bodyBottom = (int)Math.Floor(Math.Max(bar.OpenYPx, bar.CloseYPx));
            if (bodyTop - highRow < 2)
            {
                continue;
            }

            checkedBars++;
            Assert.Equal(color, bitmap.GetPixel(column, highRow));
            Assert.NotEqual(color, bitmap.GetPixel(column - 1, highRow));
            Assert.NotEqual(color, bitmap.GetPixel(column + 1, highRow));
            int middle = (bodyTop + bodyBottom) / 2;
            Assert.Equal(color, bitmap.GetPixel(column - 1, middle));
            Assert.Equal(color, bitmap.GetPixel(column + 1, middle));
        }

        Assert.True(checkedBars > 20);
    }

    [Fact]
    public void OneBarIsCentredInThePlotArea()
    {
        AiChartImageResult result = AiChartImageRenderer.Render(Request(Candles(1)));

        Assert.Single(result.Bars);
        AiChartAxes axes = result.Axes;
        Assert.Equal(axes.PricePlotRightPx - axes.PricePlotLeftPx, axes.BarSpacingPx, 2);
        Assert.Equal(axes.PricePlotLeftPx + ((axes.PricePlotRightPx - axes.PricePlotLeftPx) / 2), axes.FirstBarCenterXPx, 2);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(0)]
    [InlineData(-10)]
    public void AFlatPriceRangeGetsARangeWithPositiveWidthAndTheBarsSitInTheMiddle(int price)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 20; i++)
        {
            candles.Add(new CandleData(new DateTime(2026, 1, 1).AddDays(i), price, price, price, price, 0));
        }

        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles));

        Assert.True(result.Axes.PriceMin < result.Axes.PriceMax);
        Assert.True(result.Axes.PriceMin < price && price < result.Axes.PriceMax);
        double middle = (result.Axes.PricePlotTopPx + result.Axes.PricePlotBottomPx) / 2;
        Assert.All(result.Bars, bar => Assert.InRange(Math.Abs(bar.CloseYPx - middle), 0, 1.0));
        AiChartPanelInfo volume = result.Panels.Single(p => p.Kind == AiChartPanelKind.Volume);
        Assert.Equal((0m, 1m), (volume.ValueMin, volume.ValueMax));
    }

    [Fact]
    public void ThePriceRangeCoversTheCandlesAndTheDrawnSeriesAndPadsByPixelsOfThePanel()
    {
        var candles = new List<CandleData> { new(new DateTime(2026, 1, 1), 150m, 200m, 100m, 160m, 1) };

        (decimal min, decimal max) = AiChartPriceRange.Compute(candles, null, 200);
        // price per pixel = 100 / 200 = 0.5; padding 10 px = 5.
        Assert.Equal((95m, 205m), (min, max));

        var series = new List<IReadOnlyList<decimal?>> { new decimal?[] { null, 90m, 120m, null } };
        (decimal min2, decimal max2) = AiChartPriceRange.Compute(candles, series, 200);
        // range 110 -> 0.55 per pixel -> padding 5.5.
        Assert.Equal((84.5m, 205.5m), (min2, max2));
    }

    [Fact]
    public void TheDensityLimitUsesThePlotWidthAndTellsTheNumbers()
    {
        // width 1200 - 12 - 72 = 1116 plot pixels: 279 bars at exactly 4.0 px each fit, 280 do not.
        AiChartSizeCheck fits = AiChartLayout.CheckSize(1200, 800, 279, true, 0);
        AiChartSizeCheck tooMany = AiChartLayout.CheckSize(1200, 800, 280, true, 0);

        Assert.True(fits.IsValid);
        Assert.False(tooMany.IsValid);
        Assert.True(tooMany.WidthTooNarrow);
        Assert.Equal(279, tooMany.MaxBarsForWidth);
        Assert.Equal((280 * 4) + 12 + 72, tooMany.MinWidthPx);
        Assert.True(AiChartLayout.CheckSize(tooMany.MinWidthPx, 800, 280, true, 0).IsValid);
        Assert.False(AiChartLayout.CheckSize(tooMany.MinWidthPx - 1, 800, 280, true, 0).IsValid);
    }

    [Fact]
    public void ARequestOverTheDensityLimitIsRefusedByTheRenderer()
    {
        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(Candles(280), 1200, 800)));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    public void TheRequiredHeightIsTheLowestHeightThatHoldsThePanels(bool volume, int oscillators)
    {
        int required = AiChartLayout.CheckSize(1200, 100, 10, volume, oscillators).MinHeightPx;

        Assert.True(required >= AiChartImageStyle.MinImageHeightPx);
        Assert.True(AiChartLayout.CheckSize(1200, required, 10, volume, oscillators).IsValid);
        AiChartLayout layout = AiChartLayout.Create(1200, required, volume, oscillators);
        Assert.All(layout.Panels, p => Assert.True(p.HeightPx >= (p.Kind == AiChartPanelKind.Price ? AiChartImageStyle.MinPricePanelHeightPx : AiChartImageStyle.MinSubPanelHeightPx)));
        if (required > AiChartImageStyle.MinImageHeightPx)
        {
            AiChartSizeCheck below = AiChartLayout.CheckSize(1200, required - 1, 10, volume, oscillators);
            Assert.True(below.HeightTooShort);
            Assert.Throws<ArgumentException>(() => AiChartLayout.Create(1200, required - 1, volume, oscillators));
        }
    }

    [Fact]
    public void MorePanelsNeedMoreHeight_AndTheTwoOscillatorsWithVolumeNeedTheMost()
    {
        int volumeOnly = AiChartLayout.CheckSize(1200, 100, 10, true, 0).MinHeightPx;
        int one = AiChartLayout.CheckSize(1200, 100, 10, true, 1).MinHeightPx;
        int two = AiChartLayout.CheckSize(1200, 100, 10, true, 2).MinHeightPx;

        Assert.True(volumeOnly <= one);
        Assert.True(one < two);
        Assert.Equal(AiChartImageStyle.MinImageHeightPx, volumeOnly);
    }

    [Fact]
    public void ThePanelsStackWithFixedGapsAndFillTheInnerHeight()
    {
        AiChartLayout layout = AiChartLayout.Create(1200, 800, true, 2);

        Assert.Equal(AiChartImageStyle.MarginTopPx, layout.Panels[0].TopPx);
        for (int i = 1; i < layout.Panels.Count; i++)
        {
            Assert.Equal(AiChartImageStyle.PanelGapPx, layout.Panels[i].TopPx - layout.Panels[i - 1].BottomPx);
        }

        Assert.Equal(800 - AiChartImageStyle.MarginBottomPx, layout.Panels[^1].BottomPx);
    }

    [Fact]
    public void ThePlotAreaDoesNotChangeWithThePriceDigitsOrTheText()
    {
        AiChartImageResult shortPrices = AiChartImageRenderer.Render(Request(Candles(90, basePrice: 10m)));
        AiChartImageResult longPrices = AiChartImageRenderer.Render(Request(Candles(90, basePrice: 99999.99m)));

        Assert.Equal(shortPrices.Axes with { PriceMin = 0, PriceMax = 0 }, longPrices.Axes with { PriceMin = 0, PriceMax = 0 });
        Assert.Equal(shortPrices.Panels.Select(p => (p.TopPx, p.BottomPx)), longPrices.Panels.Select(p => (p.TopPx, p.BottomPx)));
    }

    [Fact]
    public void TheVolumeBarOfTheLargestVolumeStandsOnTheFloorOfTheVolumePanel()
    {
        IReadOnlyList<CandleData> candles = Candles(40);
        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles));
        using SKBitmap bitmap = Decode(result);
        AiChartPanelInfo volume = result.Panels.Single(p => p.Kind == AiChartPanelKind.Volume);
        AiChartBarPosition last = result.Bars[^1];

        Assert.Equal(candles.Max(c => c.Volume), volume.ValueMax);
        Assert.Equal(Volume, bitmap.GetPixel((int)Math.Floor(last.XPx), (int)volume.BottomPx - 2));
    }

    [Fact]
    public void WithoutVolumeThereIsOnePanelOnly()
    {
        AiChartImageResult result = AiChartImageRenderer.Render(Request(Candles(30), volume: false));

        Assert.Single(result.Panels);
        Assert.Equal(AiChartPanelKind.Price, result.Panels[0].Kind);
    }

    [Fact]
    public void TheLegendIsCappedAtTheStyleLimitAndShowsAsciiOnly()
    {
        IReadOnlyList<CandleData> candles = Candles(60);
        decimal?[] values = candles.Select(c => (decimal?)c.Close).ToArray();
        var overlays = Enumerable.Range(0, 4)
            .Select(i => new AiChartOverlay(
                $"o{i}",
                IndicatorType.BB,
                null,
                new[] { new AiChartLineSeries("Main", values), new AiChartLineSeries("Upper", values), new AiChartLineSeries("Lower", values) }))
            .ToList();

        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles) with { Overlays = overlays });

        // 4 indicators x 3 lines = 12 legend entries in the result; the picture shows the first 8.
        Assert.Equal(12, result.Legend.Count);
        Assert.Equal(AiChartImageStyle.MaxLegendEntries, result.LegendEntriesDrawn);
        Assert.Equal("??ma", AiChartImageRenderer.Ascii("\u65E5\u672Cma"));
        AiChartImageResult plain = AiChartImageRenderer.Render(Request(candles));
        Assert.Empty(plain.Legend);
        Assert.Equal(0, plain.LegendEntriesDrawn);
    }

    [Fact]
    public void ACancelledTokenStopsTheDrawing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => AiChartImageRenderer.Render(Request(Candles(30)), cts.Token));
    }

    [Fact]
    public void TheDateLabelsAreEvenlySpreadWithTheFirstBarAlwaysLabelled()
    {
        IReadOnlyList<int> wide = AiChartImageRenderer.DateLabelIndices(1116, 200);
        IReadOnlyList<int> narrow = AiChartImageRenderer.DateLabelIndices(236, 200);

        Assert.Equal(0, wide[0]);
        Assert.Equal(AiChartImageStyle.MaxDateLabels, wide.Count);
        Assert.Equal(199, wide[^1]);
        Assert.Equal(2, narrow.Count);
        Assert.Equal(new[] { 0 }, AiChartImageRenderer.DateLabelIndices(1116, 1));
    }

    [Fact]
    public void TheStyleNamesItselfAndItsDojiColourIsTheApplicationNeutralColour()
    {
        Assert.Equal("AiRecognitionV1", AiChartImageStyle.Name);
        Assert.Equal(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultNeutralColor, AiChartImageStyle.DojiHex);
        Assert.Equal(6, AiChartImageStyle.AuxiliaryHex.Count);
    }
}
