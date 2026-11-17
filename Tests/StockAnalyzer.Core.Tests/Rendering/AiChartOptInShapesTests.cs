using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Rendering;
using Xunit;

namespace StockAnalyzer.Core.Tests.Rendering;

/// <summary>
/// Plan FU5 (owner decision U-14): the opt-in shapes of the AI chart image. A request that does not use them is drawn exactly as before; Parabolic SAR dots, the Kumo cloud, an oscillator
/// histogram and the saved drawings each have a pixel proof against the reported mapping (the image is compared with the same image without the extra, so the expected pixel is computed, not guessed).
/// </summary>
public sealed class AiChartOptInShapesTests
{
    private const int Bars = 30;
    private static readonly SKColor White = AiChartImageStyle.Color(AiChartImageStyle.BackgroundWhiteHex);

    private static List<CandleData> Candles(int count = Bars)
    {
        var candles = new List<CandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal open = 1000m + Math.Round((decimal)(10 * Math.Sin(i * 0.3)), 2);
            decimal close = open + (i % 3 == 0 ? 6m : -4m);
            candles.Add(new CandleData(new DateTime(2026, 1, 1).AddDays(i), open, Math.Max(open, close) + 3m, Math.Min(open, close) - 3m, close, 1000 + (i * 10)));
        }

        return candles;
    }

    private static decimal?[] Constant(decimal value, int count = Bars) => Enumerable.Repeat((decimal?)value, count).ToArray();

    private static AiChartImageRequest Request(
        IReadOnlyList<AiChartOverlay>? overlays = null,
        IReadOnlyList<AiChartOscillator>? oscillators = null,
        IReadOnlyList<AiChartDrawing>? drawings = null,
        bool volume = false)
        => new(Candles(), 1200, 800, AiChartBackground.White, volume, overlays, oscillators, null, drawings);

    private static SKBitmap Decode(AiChartImageResult result) => SKBitmap.Decode(result.Png);

    private static double RowOf(AiChartImageResult image, decimal price)
    {
        AiChartAxes a = image.Axes;
        return a.PricePlotTopPx + ((a.PricePlotBottomPx - a.PricePlotTopPx) * (1 - (double)((price - a.PriceMin) / (a.PriceMax - a.PriceMin))));
    }

    private static SKColor PixelAt(SKBitmap bitmap, double x, double y) => bitmap.GetPixel((int)Math.Floor(x), (int)Math.Floor(y));

    private static SKColor Blend(SKColor background, SKColor foreground, byte alpha)
        => new(
            (byte)Math.Round(background.Red + ((foreground.Red - background.Red) * alpha / 255.0)),
            (byte)Math.Round(background.Green + ((foreground.Green - background.Green) * alpha / 255.0)),
            (byte)Math.Round(background.Blue + ((foreground.Blue - background.Blue) * alpha / 255.0)));

    private static void AssertClose(SKColor expected, SKColor actual, int tolerance = 2)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, tolerance);
    }

    private static bool IsDark(SKColor color) => color.Red < 90 && color.Green < 90 && color.Blue < 90;

    /// <summary>True when a dark pixel lies within one pixel of the position (a 1.5 px anti-aliased line covers two or three rows).</summary>
    private static bool HasDarkPixelNear(SKBitmap bitmap, double x, double y)
    {
        int cx = (int)Math.Floor(x);
        int cy = (int)Math.Floor(y);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (cx + dx >= 0 && cy + dy >= 0 && cx + dx < bitmap.Width && cy + dy < bitmap.Height && IsDark(bitmap.GetPixel(cx + dx, cy + dy)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- nothing changes unless the request asks ----

    [Fact]
    public void ARequestWithoutTheNewFields_GivesTheSamePixelsAsOneThatNamesThemEmpty_AndNoDrawingPlacements()
    {
        AiChartOverlay line = new("sma", IndicatorType.SMA, 5, new[] { new AiChartLineSeries("Main", Constant(1000m)) });
        AiChartOscillator oscillator = new("rsi", new[] { new AiChartLineSeries("Main", Constant(50m)) }, 0m, 100m, false);
        AiChartImageResult plain = AiChartImageRenderer.Render(Request(new[] { line }, new[] { oscillator }));
        AiChartImageResult named = AiChartImageRenderer.Render(Request(
            new[] { line with { Shape = AiChartLineShape.Line, Cloud = null } },
            new[] { oscillator with { HistogramValues = null } },
            Array.Empty<AiChartDrawing>()));

        Assert.Equal(plain.Png, named.Png);
        Assert.Null(plain.Drawings);
        Assert.Null(named.Drawings);
        Assert.All(plain.Legend, entry => Assert.Equal(AiChartLineShape.Line, entry.Shape));
    }

    // ---- Parabolic SAR dots ----

    [Fact]
    public void ADottedOverlayIsOneDotPerBar_NotALine_AndTheLegendSaysDots()
    {
        const decimal value = 900m;
        AiChartOverlay sar = new("sar", IndicatorType.ParabolicSAR, null, new[] { new AiChartLineSeries("Main", Constant(value)) }, AiChartLineShape.Dots);
        AiChartImageResult image = AiChartImageRenderer.Render(Request(new[] { sar }));
        using SKBitmap bitmap = Decode(image);
        double y = RowOf(image, value);
        SKColor dot = AiChartImageStyle.Color(AiChartImageStyle.AuxiliaryHex[0]);

        Assert.All(new[] { 0, 7, Bars - 1 }, i => Assert.Equal(dot, PixelAt(bitmap, image.Bars[i].XPx, y)));
        double between = (image.Bars[3].XPx + image.Bars[4].XPx) / 2;
        Assert.NotEqual(dot, PixelAt(bitmap, between, y));
        Assert.Equal(AiChartLineShape.Dots, Assert.Single(image.Legend).Shape);
    }

    [Fact]
    public void ADotIsAFilledCircleOfTheStyleRadius()
    {
        AiChartOverlay sar = new("sar", IndicatorType.ParabolicSAR, null, new[] { new AiChartLineSeries("Main", Constant(900m)) }, AiChartLineShape.Dots);
        AiChartImageResult image = AiChartImageRenderer.Render(Request(new[] { sar }));
        using SKBitmap bitmap = Decode(image);
        double y = RowOf(image, 900m);
        SKColor dot = AiChartImageStyle.Color(AiChartImageStyle.AuxiliaryHex[0]);
        int column = (int)Math.Floor(image.Bars[10].XPx);
        int row = (int)Math.Floor(y);

        // Two pixels either side of the centre are inside a circle of radius 2.5 (the dot is 5 px wide); four are outside.
        Assert.Equal(dot, bitmap.GetPixel(column + 1, row));
        Assert.Equal(dot, bitmap.GetPixel(column, row - 1));
        Assert.NotEqual(dot, bitmap.GetPixel(column + 4, row));
    }

    // ---- the Kumo cloud ----

    [Fact]
    public void TheCloudIsRedWhereSpanAIsAboveSpanB_AndBlueWhereItIsBelow_AtTheAlphaOfTheStyle()
    {
        // Span A above span B on the first half, below on the second half; they cross between bar 14 and bar 15.
        decimal?[] spanA = Enumerable.Range(0, Bars).Select(i => (decimal?)(i < 15 ? 1010m : 990m)).ToArray();
        decimal?[] spanB = Enumerable.Range(0, Bars).Select(i => (decimal?)(i < 15 ? 990m : 1010m)).ToArray();
        AiChartLineSeries[] series = { new("SenkouSpanA", spanA), new("SenkouSpanB", spanB) };
        AiChartOverlay withCloud = new("ichi", IndicatorType.Ichimoku, null, series, AiChartLineShape.Line, new AiChartCloud(spanA, spanB));
        AiChartOverlay without = withCloud with { Cloud = null };

        AiChartImageResult cloudy = AiChartImageRenderer.Render(Request(new[] { withCloud }));
        AiChartImageResult bare = AiChartImageRenderer.Render(Request(new[] { without }));
        using SKBitmap cloudBitmap = Decode(cloudy);
        using SKBitmap bareBitmap = Decode(bare);
        double y = RowOf(cloudy, 1000m);

        // Between two bars (no body or wick there), in the middle of the two boundaries.
        double leftX = (cloudy.Bars[3].XPx + cloudy.Bars[4].XPx) / 2;
        double rightX = (cloudy.Bars[24].XPx + cloudy.Bars[25].XPx) / 2;
        SKColor bareLeft = PixelAt(bareBitmap, leftX, y);
        SKColor bareRight = PixelAt(bareBitmap, rightX, y);

        AssertClose(Blend(bareLeft, AiChartImageStyle.Color(AiChartImageStyle.CloudBullishHex), AiChartImageStyle.CloudAlpha), PixelAt(cloudBitmap, leftX, y));
        AssertClose(Blend(bareRight, AiChartImageStyle.Color(AiChartImageStyle.CloudBearishHex), AiChartImageStyle.CloudAlpha), PixelAt(cloudBitmap, rightX, y));
        Assert.NotEqual(PixelAt(cloudBitmap, leftX, y), PixelAt(cloudBitmap, rightX, y));
    }

    [Fact]
    public void ABarWithoutAValueBreaksTheCloud()
    {
        decimal?[] spanA = Constant(1010m);
        decimal?[] spanB = Constant(990m);
        spanA[10] = null;
        AiChartLineSeries[] series = { new("SenkouSpanA", spanA), new("SenkouSpanB", spanB) };
        AiChartOverlay withCloud = new("ichi", IndicatorType.Ichimoku, null, series, AiChartLineShape.Line, new AiChartCloud(spanA, spanB));
        AiChartImageResult cloudy = AiChartImageRenderer.Render(Request(new[] { withCloud }));
        AiChartImageResult bare = AiChartImageRenderer.Render(Request(new[] { withCloud with { Cloud = null } }));
        using SKBitmap cloudBitmap = Decode(cloudy);
        using SKBitmap bareBitmap = Decode(bare);
        double y = RowOf(cloudy, 1000m);

        double beforeBreak = (cloudy.Bars[4].XPx + cloudy.Bars[5].XPx) / 2;
        double insideBreak = (cloudy.Bars[9].XPx + cloudy.Bars[10].XPx) / 2;
        Assert.NotEqual(PixelAt(bareBitmap, beforeBreak, y), PixelAt(cloudBitmap, beforeBreak, y));
        Assert.Equal(PixelAt(bareBitmap, insideBreak, y), PixelAt(cloudBitmap, insideBreak, y));
    }

    // ---- the oscillator histogram ----

    [Fact]
    public void AHistogramBarGoesFromZeroToTheValue_UpColourAboveZero_DownColourBelow_AndLeavesTheRangeAlone_WhenItLiesInsideIt()
    {
        // The line spans -10..+10 with a zero baseline, so the range does not change when the histogram (inside it) is added.
        decimal?[] main = Enumerable.Range(0, Bars).Select(i => (decimal?)(i % 2 == 0 ? 10m : -10m)).ToArray();
        decimal?[] histogram = Enumerable.Range(0, Bars).Select(i => (decimal?)(i % 2 == 0 ? 6m : -6m)).ToArray();
        AiChartOscillator plain = new("macd", new[] { new AiChartLineSeries("Main", main) }, null, null, true);
        AiChartOscillator withHistogram = plain with { HistogramValues = histogram };

        AiChartImageResult bare = AiChartImageRenderer.Render(Request(null, new[] { plain }));
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, new[] { withHistogram }));
        using SKBitmap bareBitmap = Decode(bare);
        using SKBitmap bitmap = Decode(image);
        AiChartPanelInfo panel = image.Panels.Single(p => p.Id == "macd");
        Assert.Equal(bare.Panels.Single(p => p.Id == "macd").ValueMin, panel.ValueMin);
        Assert.Equal(bare.Panels.Single(p => p.Id == "macd").ValueMax, panel.ValueMax);

        double Row(decimal value) => panel.TopPx + ((panel.BottomPx - panel.TopPx) * (1 - (double)((value - panel.ValueMin) / (panel.ValueMax - panel.ValueMin))));

        // Bar 2 is +6 (up colour above the zero row), bar 3 is -6 (down colour below it); the middle of the bar, away from the lines of the panel (which run between +-10).
        double upX = image.Bars[2].XPx;
        double downX = image.Bars[3].XPx;
        double upY = Row(3m);
        double downY = Row(-3m);
        AssertClose(Blend(PixelAt(bareBitmap, upX, upY), AiChartImageStyle.Color(AiChartImageStyle.HistogramNonNegativeHex), AiChartImageStyle.HistogramAlpha), PixelAt(bitmap, upX, upY));
        AssertClose(Blend(PixelAt(bareBitmap, downX, downY), AiChartImageStyle.Color(AiChartImageStyle.HistogramNegativeHex), AiChartImageStyle.HistogramAlpha), PixelAt(bitmap, downX, downY));

        // Beyond the value the bar does not reach: +8 for a +6 bar, and the other side of zero.
        Assert.Equal(PixelAt(bareBitmap, upX, Row(8m)), PixelAt(bitmap, upX, Row(8m)));
        Assert.Equal(PixelAt(bareBitmap, upX, Row(-3m)), PixelAt(bitmap, upX, Row(-3m)));
    }

    [Fact]
    public void AHistogramKeepsItsValuesAndZeroInsideTheRange_AlsoWithoutAZeroBaseline()
    {
        decimal?[] main = Constant(50m);
        decimal?[] histogram = Enumerable.Range(0, Bars).Select(i => (decimal?)(20m + i)).ToArray();
        AiChartOscillator oscillator = new("osc", new[] { new AiChartLineSeries("Main", main) }, null, null, false, histogram);

        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, new[] { oscillator }));

        AiChartPanelInfo panel = image.Panels.Single(p => p.Id == "osc");
        Assert.True(panel.ValueMin <= 0m);
        Assert.True(panel.ValueMax >= 49m);
    }

    [Fact]
    public void TheRangeRuleCountsTheHistogramAndZero_AndAnEmptyHistogramChangesNothing()
    {
        IReadOnlyList<decimal?>[] series = { new decimal?[] { 10m, 12m } };
        (decimal minWithout, decimal maxWithout) = AiChartPriceRange.ComputeOscillator(series, null, null, false);
        (decimal minWith, decimal maxWith) = AiChartPriceRange.ComputeOscillator(series, null, null, false, new decimal?[] { -4m, 30m });
        (decimal minEmpty, decimal maxEmpty) = AiChartPriceRange.ComputeOscillator(series, null, null, false, new decimal?[] { null, null });

        Assert.True(minWithout > 0m);
        Assert.True(minWith < -4m);
        Assert.True(maxWith > 30m);
        Assert.Equal((minWithout, maxWithout), (minEmpty, maxEmpty));
    }

    // ---- the saved drawings ----

    private static AiChartDrawing Drawing(AiChartDrawingKind kind, params (double Bar, decimal Price)[] points)
        => new(kind, points.Select(p => new AiChartDrawingPoint(p.Bar, p.Price)).ToArray());

    [Fact]
    public void AHorizontalLineIsDrawnAcrossThePanelAtItsPrice_InTheFixedColour_AndReportedWithItsPixels()
    {
        AiChartDrawing line = Drawing(AiChartDrawingKind.HorizontalLine, (10, 1005m));
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, null, new[] { line }));
        using SKBitmap bitmap = Decode(image);
        double y = RowOf(image, 1005m);

        AiChartDrawingPlacement placement = Assert.Single(image.Drawings!);
        Assert.True(placement.IsVisible);
        Assert.Equal(0, placement.Index);
        Assert.InRange(Math.Abs(placement.Points[0].YPx - y), 0, 0.02);
        double between = (image.Bars[20].XPx + image.Bars[21].XPx) / 2;
        Assert.True(HasDarkPixelNear(bitmap, between, y));
        Assert.True(HasDarkPixelNear(bitmap, image.Axes.PricePlotLeftPx + 3, y));
        Assert.True(HasDarkPixelNear(bitmap, image.Axes.PricePlotRightPx - 3, y));
    }

    [Fact]
    public void AVerticalLineIsDrawnFromTheTopToTheBottomOfThePricePanelAtItsBar()
    {
        AiChartDrawing line = Drawing(AiChartDrawingKind.VerticalLine, (12.5, 1000m));
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, null, new[] { line }));
        using SKBitmap bitmap = Decode(image);

        double expectedX = (image.Bars[12].XPx + image.Bars[13].XPx) / 2;
        AiChartDrawingPlacement placement = Assert.Single(image.Drawings!);
        Assert.InRange(Math.Abs(placement.Points[0].XPx - expectedX), 0, 0.02);
        Assert.True(HasDarkPixelNear(bitmap, expectedX, image.Axes.PricePlotTopPx + 5));
        Assert.True(HasDarkPixelNear(bitmap, expectedX, image.Axes.PricePlotBottomPx - 5));
    }

    [Fact]
    public void ATrendLineIsTheSegment_AndTheExtendModeProlongsIt()
    {
        AiChartDrawing segment = Drawing(AiChartDrawingKind.TrendLine, (5, 995m), (10, 1000m));
        AiChartDrawing extended = segment with { Extend = LineExtendMode.Right };
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, null, new[] { segment, extended }));
        using SKBitmap bitmap = Decode(image);

        // The slope is 1 per bar (+5 price over 5 bars); at bar 20 the prolonged line is at 1010, the segment ended at bar 10; half way between bar 7 and 8 it is at 997.5.
        double x = image.Bars[20].XPx;
        double y = RowOf(image, 1010m);
        double midX = (image.Bars[7].XPx + image.Bars[8].XPx) / 2;
        double midY = RowOf(image, 997.5m);

        Assert.True(image.Drawings![0].IsVisible);
        Assert.True(image.Drawings![1].IsVisible);
        Assert.True(HasDarkPixelNear(bitmap, midX, midY));
        Assert.True(HasDarkPixelNear(bitmap, x, y));

        // The segment alone does not reach bar 20.
        AiChartImageResult only = AiChartImageRenderer.Render(Request(null, null, new[] { segment }));
        using SKBitmap onlyBitmap = Decode(only);
        Assert.False(HasDarkPixelNear(onlyBitmap, x, y));
    }

    [Fact]
    public void AnExtendLeftLineRunsBackwardThroughTheFirstPointToo_WhateverTheOrderOfThePoints()
    {
        AiChartDrawing line = Drawing(AiChartDrawingKind.TrendLine, (20, 1010m), (10, 1000m)) with { Extend = LineExtendMode.Left };
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, null, new[] { line }));
        using SKBitmap bitmap = Decode(image);

        // Left of the left point (bar 10, 1000) the line continues down at 1 per bar: bar 4 is at 994.
        Assert.True(HasDarkPixelNear(bitmap, image.Bars[4].XPx, RowOf(image, 994m)));
        // Right of the right point (bar 20) it does not continue: bar 25 would be at 1015.
        Assert.False(HasDarkPixelNear(bitmap, image.Bars[25].XPx, RowOf(image, 1015m)));
    }

    [Fact]
    public void ARayStartsAtItsFirstPointAndRunsThroughTheSecondToTheBorder_NotBehindTheFirst()
    {
        AiChartDrawing ray = Drawing(AiChartDrawingKind.Ray, (10, 1000m), (12, 1001m));
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, null, new[] { ray }));
        using SKBitmap bitmap = Decode(image);

        // The slope is 0.5 per bar: bar 20 is at 1005, and bar 4 (behind the first point) would be at 997.
        Assert.True(HasDarkPixelNear(bitmap, image.Bars[20].XPx, RowOf(image, 1005m)));
        Assert.False(HasDarkPixelNear(bitmap, image.Bars[4].XPx, RowOf(image, 997m)));
    }

    [Fact]
    public void AParallelChannelIsTheBaseEdgeAndTheEdgeThroughTheThirdPoint_FlatOppositeIsHorizontal()
    {
        AiChartDrawing parallel = Drawing(AiChartDrawingKind.ParallelChannel, (5, 990m), (15, 1000m), (10, 1012m));
        AiChartDrawing flat = parallel with { ChannelMode = ChannelMode.FlatOpposite };
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, null, new[] { parallel }));
        AiChartImageResult flatImage = AiChartImageRenderer.Render(Request(null, null, new[] { flat }));
        using SKBitmap bitmap = Decode(image);
        using SKBitmap flatBitmap = Decode(flatImage);

        // Parallel: the opposite edge passes through the third point (bar 10, 1012) with the slope of the base edge (+1 per bar), so at bar 5 it is at 1007 and at bar 15 at 1017.
        Assert.True(HasDarkPixelNear(bitmap, image.Bars[5].XPx, RowOf(image, 1007m)));
        Assert.True(HasDarkPixelNear(bitmap, image.Bars[15].XPx, RowOf(image, 1017m)));
        // Flat: horizontal at 1012 between the bars of the first and the second point.
        Assert.True(HasDarkPixelNear(flatBitmap, image.Bars[5].XPx, RowOf(flatImage, 1012m)));
        Assert.True(HasDarkPixelNear(flatBitmap, image.Bars[15].XPx, RowOf(flatImage, 1012m)));
        Assert.False(HasDarkPixelNear(flatBitmap, image.Bars[15].XPx, RowOf(flatImage, 1017m)));
    }

    [Fact]
    public void ADrawingOutsideThePanelLeavesNoPixelsAndIsReportedNotVisible()
    {
        // The price range is about 990..1013 plus padding: 5000 is far outside; a vertical line 100 bars to the right is outside the bars.
        AiChartDrawing farPrice = Drawing(AiChartDrawingKind.HorizontalLine, (5, 5000m));
        AiChartDrawing farBar = Drawing(AiChartDrawingKind.VerticalLine, (130, 1000m));
        AiChartImageResult image = AiChartImageRenderer.Render(Request(null, null, new[] { farPrice, farBar }));
        AiChartImageResult bare = AiChartImageRenderer.Render(Request());

        Assert.All(image.Drawings!, placement => Assert.False(placement.IsVisible));
        Assert.Equal(bare.Png, image.Png);
    }

    [Theory]
    [InlineData(AiChartDrawingKind.HorizontalLine, 2)]
    [InlineData(AiChartDrawingKind.TrendLine, 1)]
    [InlineData(AiChartDrawingKind.ParallelChannel, 2)]
    public void ADrawingWithTheWrongNumberOfPointsIsRefused(AiChartDrawingKind kind, int pointCount)
    {
        var points = Enumerable.Range(0, pointCount).Select(i => new AiChartDrawingPoint(i, 1000m)).ToArray();

        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(null, null, new[] { new AiChartDrawing(kind, points) })));
    }

    [Fact]
    public void ACloudOrHistogramThatIsNotOneValuePerDrawnBarIsRefused()
    {
        AiChartLineSeries[] series = { new("SenkouSpanA", Constant(1010m)), new("SenkouSpanB", Constant(990m)) };
        AiChartOverlay shortCloud = new("ichi", IndicatorType.Ichimoku, null, series, AiChartLineShape.Line, new AiChartCloud(Constant(1010m, Bars - 1), Constant(990m)));
        AiChartOscillator shortHistogram = new("macd", new[] { new AiChartLineSeries("Main", Constant(1m)) }, null, null, true, Constant(1m, Bars + 1));

        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(new[] { shortCloud })));
        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(null, new[] { shortHistogram })));
    }

    // ---- the position of a time in drawn-bar units ----

    private static readonly DateTime[] BarTimes = { new(2026, 1, 1), new(2026, 1, 2), new(2026, 1, 5), new(2026, 1, 6) };

    [Fact]
    public void ATimeOnABarIsThatBarsIndex_BetweenBarsItIsLinearInTime()
    {
        Assert.Equal(0d, AiChartTimeAxis.BarPosition(BarTimes, new DateTime(2026, 1, 1)));
        Assert.Equal(2d, AiChartTimeAxis.BarPosition(BarTimes, new DateTime(2026, 1, 5)));
        Assert.Equal(3d, AiChartTimeAxis.BarPosition(BarTimes, new DateTime(2026, 1, 6)));

        // Jan 3 is a third of the way from Jan 2 (bar 1) to Jan 5 (bar 2): the weekend takes no bars.
        Assert.InRange(AiChartTimeAxis.BarPosition(BarTimes, new DateTime(2026, 1, 3)), 1d + (1d / 3d) - 1e-9, 1d + (1d / 3d) + 1e-9);
        Assert.Equal(1.5, AiChartTimeAxis.BarPosition(BarTimes, new DateTime(2026, 1, 3).AddHours(12)));
    }

    [Fact]
    public void BeforeTheFirstAndAfterTheLastBarThePositionContinuesWithTheAverageIntervalOfTheTimeMap()
    {
        // The map spans five days over four bars: the average interval is 1.25 days (the chart divides by the number of bars, not by the number of gaps).
        Assert.Equal(-1.6, AiChartTimeAxis.BarPosition(BarTimes, new DateTime(2025, 12, 30)), 9);
        Assert.Equal(4.6, AiChartTimeAxis.BarPosition(BarTimes, new DateTime(2026, 1, 8)), 9);
    }

    [Fact]
    public void AMapThatSpansNoTimeUsesOneDayAsTheInterval_AndAnEmptyMapIsRefused()
    {
        Assert.Equal(1d, AiChartTimeAxis.BarPosition(new[] { new DateTime(2026, 1, 1) }, new DateTime(2026, 1, 2)));
        Assert.Equal(-1d, AiChartTimeAxis.BarPosition(new[] { new DateTime(2026, 1, 1) }, new DateTime(2025, 12, 31)));
        Assert.Equal(0d, AiChartTimeAxis.BarPosition(new[] { new DateTime(2026, 1, 1) }, new DateTime(2026, 1, 1)));
        Assert.Equal(2.5, AiChartTimeAxis.BarPosition(new[] { new DateTime(2026, 1, 1), new DateTime(2026, 1, 1) }, new DateTime(2026, 1, 2).AddHours(12)), 9);
        Assert.Throws<ArgumentException>(() => AiChartTimeAxis.BarPosition(Array.Empty<DateTime>(), new DateTime(2026, 1, 1)));
    }

    // ---- the choice of series ----

    private static IIndicatorResult IchimokuLikeResult(int count)
    {
        IReadOnlyList<decimal?> Series(decimal value) => Enumerable.Repeat((decimal?)value, count).ToArray();
        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, Series(1m) },
            { CoreIchimokuIndicator.TenkanSenSeriesName, Series(1m) },
            { CoreIchimokuIndicator.KijunSenSeriesName, Series(2m) },
            { CoreIchimokuIndicator.SenkouSpanASeriesName, Series(3m) },
            { CoreIchimokuIndicator.SenkouSpanBSeriesName, Series(4m) },
            { CoreIchimokuIndicator.ChikouSpanSeriesName, Series(5m) },
        });
    }

    [Fact]
    public void ANamedSelectionKeepsTheFourCausalIchimokuLinesPastTheCap_AndNeverTheChikouSpan()
    {
        string[] names =
        {
            CoreIchimokuIndicator.TenkanSenSeriesName,
            CoreIchimokuIndicator.KijunSenSeriesName,
            CoreIchimokuIndicator.SenkouSpanASeriesName,
            CoreIchimokuIndicator.SenkouSpanBSeriesName,
        };

        IReadOnlyList<AiChartSelectedSeries> selected = AiChartSeriesSelector.SelectNamed(IchimokuLikeResult(60), names, 20, 30);

        Assert.Equal(names, selected.Select(s => s.Name));
        Assert.True(selected.Count > AiChartImageStyle.MaxSeriesPerIndicator);
        Assert.All(selected, s => Assert.Equal(30, s.Values.Count));
    }

    [Fact]
    public void ANamedSelectionCutsTheWindow_AndLeavesOutASeriesWithoutAValueInIt()
    {
        // Senkou spans are longer than the bars by the displacement; the window cuts the part beyond the last drawn bar. A series that is empty in the window is left out.
        var spanA = new decimal?[40];
        for (int i = 30; i < 40; i++)
        {
            spanA[i] = 9m;
        }

        IIndicatorResult result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { CoreIchimokuIndicator.SenkouSpanASeriesName, spanA },
            { CoreIchimokuIndicator.SenkouSpanBSeriesName, Enumerable.Repeat((decimal?)7m, 40).ToArray() },
        });

        IReadOnlyList<AiChartSelectedSeries> selected = AiChartSeriesSelector.SelectNamed(
            result,
            new[] { CoreIchimokuIndicator.SenkouSpanASeriesName, CoreIchimokuIndicator.SenkouSpanBSeriesName, "Missing" },
            0,
            20);

        Assert.Equal(new[] { CoreIchimokuIndicator.SenkouSpanBSeriesName }, selected.Select(s => s.Name));
        Assert.All(selected[0].Values, v => Assert.Equal(7m, v));
    }

    [Fact]
    public void TheHistogramOfAnOscillatorIsTheFirstUnsuppressedSeriesWithHistogramInItsName_ElseNull()
    {
        IReadOnlyList<decimal?> Series(decimal value) => Enumerable.Repeat((decimal?)value, 50).ToArray();
        IIndicatorResult macd = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, Series(1m) },
            { "Histogram", Series(2m) },
        });
        IIndicatorResult plain = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { IndicatorResult.MainSeriesName, Series(1m) } });

        IReadOnlyList<decimal?>? histogram = AiChartSeriesSelector.SelectHistogram(IndicatorType.MACD, macd, 20, 30);

        Assert.NotNull(histogram);
        Assert.Equal(30, histogram!.Count);
        Assert.All(histogram, v => Assert.Equal(2m, v));
        Assert.Null(AiChartSeriesSelector.SelectHistogram(IndicatorType.RSI, plain, 20, 30));
        // The same series stays out of the lines, as before.
        Assert.DoesNotContain(AiChartSeriesSelector.Select(IndicatorType.MACD, macd, 20, 30), s => s.Name == "Histogram");
    }

    [Fact]
    public void ParabolicSarAndIchimokuAreNoLongerUndrawable_TheOthersStayRefused()
    {
        Assert.False(AiChartUndrawableTypes.Contains(IndicatorType.ParabolicSAR));
        Assert.False(AiChartUndrawableTypes.Contains(IndicatorType.Ichimoku));
        Assert.True(AiChartUndrawableTypes.Contains(IndicatorType.VolumeProfile));
        Assert.True(AiChartUndrawableTypes.Contains(IndicatorType.Renko));
    }
}
