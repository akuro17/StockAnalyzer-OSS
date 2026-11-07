using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Rendering;
using Xunit;

namespace StockAnalyzer.Core.Tests.Rendering;

/// <summary>Plan 15, T2 and T2b: overlay lines, oscillator panels, markers, the colour and pattern rule, the moving-average set and the choice of drawn series.</summary>
public sealed class AiChartOverlayRenderingTests
{
    private static List<CandleData> Candles(int count)
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

    private static decimal?[] Constant(int count, decimal value) => Enumerable.Repeat((decimal?)value, count).ToArray();

    private static AiChartOverlay Overlay(string id, IndicatorType type, int? period, params string[] seriesNames)
        => new(id, type, period, seriesNames.Select(n => new AiChartLineSeries(n, Constant(10, 1000m))).ToArray());

    // ---- the colour and pattern rule ----

    [Fact]
    public void TheMovingAveragesTakeTheShortMidAndLongColourByTheRankOfTheirPeriod_AndTheOthersTheAuxiliaryColours()
    {
        // Passed out of order: SMA 25, EMA 5, VWMA 75, then a Bollinger overlay with three lines.
        IReadOnlyList<AiChartOverlay> overlays = new[]
        {
            Overlay("sma25", IndicatorType.SMA, 25, "Main"),
            Overlay("ema5", IndicatorType.EMA, 5, "Main"),
            Overlay("vwma75", IndicatorType.VWMA, 75, "Main"),
            Overlay("bb", IndicatorType.BB, null, "Main", "Upper", "Lower"),
        };

        IReadOnlyList<AiChartStyledLine> lines = AiChartLineAssigner.AssignOverlays(overlays);

        Assert.Equal(new[] { "ema5", "sma25", "vwma75", "bb", "bb.Upper", "bb.Lower" }, lines.Select(l => l.Label));
        Assert.Equal(
            new[] { AiChartImageStyle.MovingAverageShortHex, AiChartImageStyle.MovingAverageMidHex, AiChartImageStyle.MovingAverageLongHex, AiChartImageStyle.AuxiliaryHex[0], AiChartImageStyle.AuxiliaryHex[1], AiChartImageStyle.AuxiliaryHex[2] },
            lines.Select(l => l.ColorHex));
        Assert.Equal(
            new[] { AiChartLineStyle.Solid, AiChartLineStyle.Dash, AiChartLineStyle.Dot, AiChartLineStyle.Solid, AiChartLineStyle.Dash, AiChartLineStyle.Dot },
            lines.Select(l => l.LineStyle));
        Assert.Equal(
            new[] { 2.5f, 2.5f, 2.5f, 1.5f, 1.5f, 1.5f },
            lines.Select(l => l.WidthPx));
    }

    [Fact]
    public void EqualPeriodsKeepTheOrderOfTheRequest_AndATypeWithoutAPeriodComesLast()
    {
        IReadOnlyList<AiChartOverlay> overlays = new[]
        {
            Overlay("mesa", IndicatorType.MESA, null, "Main"),
            Overlay("sma20", IndicatorType.SMA, 20, "Main"),
            Overlay("ema20", IndicatorType.EMA, 20, "Main"),
        };

        IReadOnlyList<AiChartStyledLine> lines = AiChartLineAssigner.AssignOverlays(overlays);

        Assert.Equal(new[] { "sma20", "ema20", "mesa" }, lines.Select(l => l.Label));
        Assert.Equal(
            new[] { AiChartImageStyle.MovingAverageShortHex, AiChartImageStyle.MovingAverageMidHex, AiChartImageStyle.MovingAverageLongHex },
            lines.Select(l => l.ColorHex));
    }

    [Fact]
    public void TheFourthMovingAverageAndTheSecondLineOfAMovingAverageTakeAuxiliaryColours()
    {
        IReadOnlyList<AiChartOverlay> overlays = new[]
        {
            Overlay("a", IndicatorType.SMA, 5, "Main"),
            Overlay("b", IndicatorType.SMA, 10, "Main"),
            Overlay("c", IndicatorType.SMA, 20, "Main"),
            Overlay("d", IndicatorType.SMA, 50, "Main"),
            Overlay("mesa", IndicatorType.MESA, null, "Main", "Fama"),
        };

        IReadOnlyList<AiChartStyledLine> lines = AiChartLineAssigner.AssignOverlays(overlays);

        Assert.Equal(new[] { "a", "b", "c", "d", "mesa", "mesa.Fama" }, lines.Select(l => l.Label));
        Assert.Equal(AiChartImageStyle.AuxiliaryHex[0], lines[3].ColorHex);
        Assert.Equal(AiChartImageStyle.AuxiliaryWidthPx, lines[3].WidthPx);
        Assert.Equal(AiChartImageStyle.AuxiliaryHex[1], lines[4].ColorHex);
        Assert.Equal(AiChartImageStyle.AuxiliaryHex[2], lines[5].ColorHex);
        Assert.Equal(AiChartLineStyle.Dash, lines[4].LineStyle);
    }

    [Fact]
    public void TheAuxiliaryColoursCycleAndAnOscillatorPanelStartsThemAgain()
    {
        var overlays = Enumerable.Range(0, 8).Select(i => Overlay($"x{i}", IndicatorType.BB, null, "Main")).ToList();

        IReadOnlyList<AiChartStyledLine> lines = AiChartLineAssigner.AssignOverlays(overlays);

        Assert.Equal(AiChartImageStyle.AuxiliaryHex[0], lines[6].ColorHex);
        Assert.Equal(AiChartImageStyle.AuxiliaryHex[1], lines[7].ColorHex);

        var oscillator = new AiChartOscillator("macd", new[] { new AiChartLineSeries("Main", Constant(10, 1m)), new AiChartLineSeries("Signal", Constant(10, 2m)) }, null, null, true);
        IReadOnlyList<AiChartStyledLine> oscillatorLines = AiChartLineAssigner.AssignOscillator(oscillator);
        Assert.Equal(new[] { "macd", "macd.Signal" }, oscillatorLines.Select(l => l.Label));
        Assert.Equal(new[] { AiChartImageStyle.AuxiliaryHex[0], AiChartImageStyle.AuxiliaryHex[1] }, oscillatorLines.Select(l => l.ColorHex));
        Assert.Equal(new[] { AiChartLineStyle.Solid, AiChartLineStyle.Dash }, oscillatorLines.Select(l => l.LineStyle));
    }

    [Fact]
    public void TheLinePatternsAreGivenOutSolidDashDotAndStartAgain()
    {
        Assert.Equal(new[] { AiChartLineStyle.Solid, AiChartLineStyle.Dash, AiChartLineStyle.Dot }, AiChartImageStyle.LinePatterns);

        var overlays = Enumerable.Range(0, 7).Select(i => Overlay($"x{i}", IndicatorType.BB, null, "Main")).ToList();
        IReadOnlyList<AiChartStyledLine> lines = AiChartLineAssigner.AssignOverlays(overlays);

        Assert.Equal(
            Enumerable.Range(0, 7).Select(i => AiChartImageStyle.LinePatterns[i % AiChartImageStyle.LinePatterns.Count]),
            lines.Select(l => l.LineStyle));
        Assert.Equal(new[] { AiChartLineStyle.Solid, AiChartLineStyle.Dash, AiChartLineStyle.Dot, AiChartLineStyle.Solid }, lines.Take(4).Select(l => l.LineStyle));
    }

    // ---- the moving-average set ----

    [Fact]
    public void TheMovingAverageSetHoldsTheTwentyThreeDecidedTypesAndNotTheExcludedOnes()
    {
        IndicatorType[] expected =
        {
            IndicatorType.SMA, IndicatorType.EMA, IndicatorType.WMA, IndicatorType.HMA, IndicatorType.TMA, IndicatorType.DEMA, IndicatorType.TEMA, IndicatorType.VWMA,
            IndicatorType.VAMA, IndicatorType.SMMA, IndicatorType.LRMA, IndicatorType.ALMA, IndicatorType.ZLEMA, IndicatorType.LSMA, IndicatorType.Dma, IndicatorType.KAMA,
            IndicatorType.JMA, IndicatorType.FRAMA, IndicatorType.BSMA, IndicatorType.ClothoidMovingAverage, IndicatorType.MtfLaggedEma, IndicatorType.VIDYA, IndicatorType.MESA,
        };

        Assert.Equal(23, AiChartMovingAverageSet.Types.Count());
        Assert.Equal(expected.OrderBy(t => t.ToString()), AiChartMovingAverageSet.Types.OrderBy(t => t.ToString()));
        Assert.All(new[] { IndicatorType.GMA, IndicatorType.AMA, IndicatorType.VolumeMA, IndicatorType.MovingAverageCross, IndicatorType.MaDeviationRate, IndicatorType.MACD, IndicatorType.ARIMA, IndicatorType.BB },
            t => Assert.False(AiChartMovingAverageSet.Contains(t)));
    }

    [Fact]
    public void TheRepresentativePeriodIsReadFromTheNamedParameter_AndMesaHasNone()
    {
        Assert.Equal(25, AiChartMovingAverageSet.RepresentativePeriod(IndicatorType.SMA, new CoreSmaParameter { Period = 25 }));
        Assert.Equal("SmoothPeriod", AiChartMovingAverageSet.RepresentativeParameter(IndicatorType.VIDYA));
        Assert.Equal("Period", AiChartMovingAverageSet.RepresentativeParameter(IndicatorType.KAMA));
        Assert.Null(AiChartMovingAverageSet.RepresentativeParameter(IndicatorType.MESA));
        Assert.Null(AiChartMovingAverageSet.RepresentativePeriod(IndicatorType.MESA, new CoreSmaParameter { Period = 25 }));
        Assert.Null(AiChartMovingAverageSet.RepresentativePeriod(IndicatorType.BB, new CoreSmaParameter { Period = 25 }));
        Assert.Null(AiChartMovingAverageSet.RepresentativePeriod(IndicatorType.SMA, null));
    }

    // ---- the choice of drawn series, on real indicator results ----

    private static IIndicatorResult Calculate(IndicatorType type, int bars)
    {
        var random = new Random(3);
        decimal price = 100m;
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < bars; i++)
        {
            decimal open = price;
            decimal close = open + Math.Round((decimal)((random.NextDouble() - 0.5) * 4), 2);
            price = close;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), open, Math.Max(open, close) + 1m, Math.Min(open, close) - 1m, close, 1000));
        }

        return IndicatorFactory.Default.Create(type)!.Calculate(candles);
    }

    [Theory]
    [InlineData(IndicatorType.SMA, new[] { "Main" })]
    [InlineData(IndicatorType.BB, new[] { "Main", "Upper", "Lower" })]
    [InlineData(IndicatorType.MACD, new[] { "Main", "Signal" })]
    [InlineData(IndicatorType.ClothoidMovingAverage, new[] { "Main" })]
    [InlineData(IndicatorType.MESA, new[] { "Main", "Fama" })]
    public void TheDrawnSeriesAreTheLinesOfTheResultWithoutAliasesHistogramsAndSuppressedSeries(IndicatorType type, string[] expectedNames)
    {
        IIndicatorResult result = Calculate(type, 400);

        IReadOnlyList<AiChartSelectedSeries> selected = AiChartSeriesSelector.Select(type, result, 300, 100);

        Assert.Equal(expectedNames, selected.Select(s => s.Name));
        Assert.All(selected, s => Assert.Equal(100, s.Values.Count));
        Assert.True(selected.Count <= AiChartImageStyle.MaxSeriesPerIndicator);
    }

    [Fact]
    public void ThePriceRangeNeverSeesTheWarmUpRegion()
    {
        IIndicatorResult result = Calculate(IndicatorType.SMA, 400);
        IReadOnlyList<decimal?> main = result.MainValues;

        AiChartSelectedSeries drawn = AiChartSeriesSelector.Select(IndicatorType.SMA, result, 300, 100).Single();

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(main[300 + i], drawn.Values[i]);
        }

        // A window that starts inside the warm-up has nulls there and nothing else.
        AiChartSelectedSeries early = AiChartSeriesSelector.Select(IndicatorType.SMA, result, 0, 50).SingleOrDefault()!;
        Assert.True(early is null || early.Values.Take(5).All(v => v is null));
    }

    [Fact]
    public void AWindowPastTheEndOfTheResultGivesNullsAndAFailedResultGivesNothing()
    {
        IIndicatorResult result = Calculate(IndicatorType.SMA, 100);

        IReadOnlyList<AiChartSelectedSeries> selected = AiChartSeriesSelector.Select(IndicatorType.SMA, result, 90, 20);

        Assert.Equal(20, selected.Single().Values.Count);
        Assert.All(selected.Single().Values.Skip(10), v => Assert.Null(v));
        Assert.Empty(AiChartSeriesSelector.Select(IndicatorType.SMA, result, 0, 0));
    }

    // ---- oscillator range ----

    [Fact]
    public void TheOscillatorRangeFollowsTheRuleOfTheApplicationSubPanel()
    {
        IReadOnlyList<decimal?>[] series = { new decimal?[] { null, 10m, 30m } };

        Assert.Equal((0m, 100m), AiChartPriceRange.ComputeOscillator(series, 0m, 100m, false));
        Assert.Equal((9m, 31m), AiChartPriceRange.ComputeOscillator(series, null, null, false));
        // No value at all: the application's range for a successful result without a value (0 to 1; owner decision E-2 = D1-B).
        Assert.Equal((0m, 1m), AiChartPriceRange.ComputeOscillator(new[] { (IReadOnlyList<decimal?>)new decimal?[] { null, null } }, null, null, false));
        // A fixed range that is not a range (max not above min) is ignored.
        Assert.Equal((9m, 31m), AiChartPriceRange.ComputeOscillator(series, 50m, 50m, false));
    }

    [Fact]
    public void TheOscillatorRangeIncludesZeroWhenAsked_AndWidensAFlatSeries()
    {
        IReadOnlyList<decimal?>[] positive = { new decimal?[] { 10m, 30m } };

        (decimal min, decimal max) = AiChartPriceRange.ComputeOscillator(positive, null, null, true);
        Assert.Equal((-1.5m, 31.5m), (min, max));

        Assert.Equal((90m, 110m), AiChartPriceRange.ComputeOscillator(new[] { (IReadOnlyList<decimal?>)new decimal?[] { 100m, 100m } }, null, null, false));
        Assert.Equal((-0.05m, 0.05m), AiChartPriceRange.ComputeOscillator(new[] { (IReadOnlyList<decimal?>)new decimal?[] { 0m, 0m } }, null, null, false));
        Assert.Equal((-110m, -90m), AiChartPriceRange.ComputeOscillator(new[] { (IReadOnlyList<decimal?>)new decimal?[] { -100m, -100m } }, null, null, false));
    }

    [Fact]
    public void TheOscillatorRangeAlwaysHasAPositiveWidth_AlsoForAnUnusableFixedRangeWithNoValueOrFlatValues()
    {
        decimal?[] empty = { null, null };
        decimal?[] flat = { 100m, 100m };
        decimal?[] flatZero = { 0m, 0m };
        decimal?[] spread = { 10m, 30m };
        decimal?[] smallFlat = { 5m, 5m };
        var cases = new (string Name, decimal?[] Values, decimal? FixedMin, decimal? FixedMax, bool Zero, decimal Min, decimal Max)[]
        {
            ("inverted fixed range, no value", empty, 80m, 20m, false, 0m, 1m),
            ("equal fixed range, no value", empty, 50m, 50m, false, 0m, 1m),
            ("inverted fixed range, flat values", flat, 80m, 20m, false, 90m, 110m),
            ("inverted fixed range, flat zero", flatZero, 80m, 20m, false, -0.05m, 0.05m),
            ("inverted fixed range, zero baseline asked", spread, 80m, 20m, true, -1.5m, 31.5m),
            ("only a minimum, no value", empty, 0m, null, false, 0m, 1m),
            ("only a maximum, flat values", smallFlat, null, 5m, false, 4.5m, 5.5m),
            ("zero baseline asked, no value", empty, null, null, true, 0m, 1m),
            ("a usable fixed range wins over no value", empty, 0m, 100m, false, 0m, 100m),
        };

        foreach ((string name, decimal?[] values, decimal? fixedMin, decimal? fixedMax, bool zero, decimal expectedMin, decimal expectedMax) in cases)
        {
            (decimal min, decimal max) = AiChartPriceRange.ComputeOscillator(new IReadOnlyList<decimal?>[] { values }, fixedMin, fixedMax, zero);

            Assert.True((min, max) == (expectedMin, expectedMax), $"{name}: expected {expectedMin}..{expectedMax}, got {min}..{max}");
            Assert.True(max > min, $"{name}: the range must have a positive width");
            Assert.Null(Record.Exception(() => new AiChartPlotTransform(0, 0, 100, 100, min, max, 1)));
        }
    }

    // ---- rendering ----

    private static AiChartImageRequest Request(IReadOnlyList<CandleData> candles, int height = 800, bool volume = true)
        => new(candles, 1200, height, AiChartBackground.White, volume);

    [Fact]
    public void AnOverlayLineIsDrawnAtTheRowOfItsValueInItsColour_AndWidensThePriceRange()
    {
        IReadOnlyList<CandleData> candles = Candles(80);
        var line = new AiChartOverlay("sma", IndicatorType.SMA, 20, new[] { new AiChartLineSeries("Main", Constant(80, 1030m)) });

        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles) with { Overlays = new[] { line } });
        using SKBitmap bitmap = SKBitmap.Decode(result.Png);

        Assert.True(result.Axes.PriceMax > 1030m);
        double ratio = (double)((1030m - result.Axes.PriceMin) / (result.Axes.PriceMax - result.Axes.PriceMin));
        int row = (int)Math.Floor(result.Axes.PricePlotTopPx + ((result.Axes.PricePlotBottomPx - result.Axes.PricePlotTopPx) * (1 - ratio)));
        int column = (int)Math.Floor(result.Bars[40].XPx);
        Assert.Equal(AiChartImageStyle.Color(AiChartImageStyle.MovingAverageShortHex), bitmap.GetPixel(column, row));
        Assert.Single(result.Legend);
        Assert.Equal("sma", result.Legend[0].Label);
    }

    [Fact]
    public void AMissingValueBreaksALineAndAWarmUpRegionOfNullsDrawsNothing()
    {
        IReadOnlyList<CandleData> candles = Candles(60);
        decimal?[] values = Constant(60, 1020m);
        for (int i = 0; i < 30; i++)
        {
            values[i] = null;
        }

        var overlay = new AiChartOverlay("sma", IndicatorType.SMA, 20, new[] { new AiChartLineSeries("Main", values) });
        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles) with { Overlays = new[] { overlay } });
        using SKBitmap bitmap = SKBitmap.Decode(result.Png);
        double ratio = (double)((1020m - result.Axes.PriceMin) / (result.Axes.PriceMax - result.Axes.PriceMin));
        int row = (int)Math.Floor(result.Axes.PricePlotTopPx + ((result.Axes.PricePlotBottomPx - result.Axes.PricePlotTopPx) * (1 - ratio)));
        SKColor color = AiChartImageStyle.Color(AiChartImageStyle.MovingAverageShortHex);

        Assert.NotEqual(color, bitmap.GetPixel((int)Math.Floor(result.Bars[10].XPx), row));
        Assert.Equal(color, bitmap.GetPixel((int)Math.Floor(result.Bars[50].XPx), row));
    }

    [Fact]
    public void AnOscillatorGetsAPanelOfItsOwnWithItsFixedRangeAndItsLine()
    {
        IReadOnlyList<CandleData> candles = Candles(80);
        var rsi = new AiChartOscillator("rsi", new[] { new AiChartLineSeries("Main", Constant(80, 50m)) }, 0m, 100m, false);

        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles, 900) with { Oscillators = new[] { rsi } });
        using SKBitmap bitmap = SKBitmap.Decode(result.Png);
        AiChartPanelInfo panel = result.Panels.Single(p => p.Kind == AiChartPanelKind.Oscillator);

        Assert.Equal(new[] { "price", "volume", "rsi" }, result.Panels.Select(p => p.Id));
        Assert.Equal((0m, 100m), (panel.ValueMin, panel.ValueMax));
        int row = (int)Math.Floor(panel.TopPx + ((panel.BottomPx - panel.TopPx) / 2));
        Assert.Equal(AiChartImageStyle.Color(AiChartImageStyle.AuxiliaryHex[0]), bitmap.GetPixel((int)Math.Floor(result.Bars[40].XPx), row));
        Assert.Equal("rsi", result.Legend.Single().Label);
    }

    [Fact]
    public void ATwoOscillatorImageNeedsMoreHeightAndRefusesAThirdPanelOrAShortImage()
    {
        IReadOnlyList<CandleData> candles = Candles(40);
        AiChartOscillator Oscillator(string id) => new(id, new[] { new AiChartLineSeries("Main", Constant(40, 1m)) }, null, null, true);

        int required = AiChartLayout.CheckSize(1200, 100, 40, true, 2).MinHeightPx;
        AiChartImageResult ok = AiChartImageRenderer.Render(Request(candles, required) with { Oscillators = new[] { Oscillator("a"), Oscillator("b") } });
        Assert.Equal(new[] { "price", "volume", "a", "b" }, ok.Panels.Select(p => p.Id));

        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(candles, required - 1) with { Oscillators = new[] { Oscillator("a"), Oscillator("b") } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiChartImageRenderer.Render(Request(candles, 1200) with { Oscillators = new[] { Oscillator("a"), Oscillator("b"), Oscillator("c") } }));
    }

    [Fact]
    public void ABuyMarkerPointsUpUnderTheLowAndASellMarkerPointsDownOverTheHigh()
    {
        IReadOnlyList<CandleData> candles = Candles(60);
        AiChartImageResult plain = AiChartImageRenderer.Render(Request(candles));
        int sellBar = Enumerable.Range(5, 50).First(i => (int)Math.Floor(plain.Bars[i].HighYPx) - 12 > plain.Axes.PricePlotTopPx + 2);
        int buyBar = Enumerable.Range(5, 50).First(i => (int)Math.Floor(plain.Bars[i].LowYPx) + 12 < plain.Axes.PricePlotBottomPx - 2 && i != sellBar);

        AiChartImageResult result = AiChartImageRenderer.Render(Request(candles) with { Markers = new[] { new AiChartMarker(buyBar, true), new AiChartMarker(sellBar, false) } });
        using SKBitmap bitmap = SKBitmap.Decode(result.Png);
        SKColor signal = AiChartImageStyle.Color(AiChartImageStyle.SignalHex);

        int buyRow = (int)Math.Floor(result.Bars[buyBar].LowYPx) + 1 + AiChartImageStyle.MarkerGapPx + 4;
        int sellRow = (int)Math.Floor(result.Bars[sellBar].HighYPx) - AiChartImageStyle.MarkerGapPx - 4;
        Assert.Equal(signal, bitmap.GetPixel((int)Math.Floor(result.Bars[buyBar].XPx), buyRow));
        Assert.Equal(signal, bitmap.GetPixel((int)Math.Floor(result.Bars[sellBar].XPx), sellRow));

        // Pointing up: near the apex row the triangle is narrower than near its base. Pointing down: the other way round.
        int buyApex = (int)Math.Floor(result.Bars[buyBar].LowYPx) + 1 + AiChartImageStyle.MarkerGapPx;
        int x = (int)Math.Floor(result.Bars[buyBar].XPx);
        Assert.NotEqual(signal, bitmap.GetPixel(x + 2, buyApex + 1));
        Assert.Equal(signal, bitmap.GetPixel(x + 2, buyApex + 5));
    }

    [Fact]
    public void AMarkerOutsideTheDrawnBarsOrASeriesOfTheWrongLengthIsRefused()
    {
        IReadOnlyList<CandleData> candles = Candles(30);

        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(candles) with { Markers = new[] { new AiChartMarker(30, true) } }));
        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(candles) with { Markers = new[] { new AiChartMarker(-1, true) } }));
        var shortSeries = new AiChartOverlay("sma", IndicatorType.SMA, 5, new[] { new AiChartLineSeries("Main", Constant(29, 1000m)) });
        Assert.Throws<ArgumentException>(() => AiChartImageRenderer.Render(Request(candles) with { Overlays = new[] { shortSeries } }));
    }

    [Fact]
    public void AFullImageWithOverlaysOscillatorsAndMarkersIsTheSameEveryTime()
    {
        IReadOnlyList<CandleData> candles = Candles(100);
        var overlays = new[]
        {
            new AiChartOverlay("ema", IndicatorType.EMA, 10, new[] { new AiChartLineSeries("Main", candles.Select(c => (decimal?)c.Close).ToArray()) }),
            new AiChartOverlay("bb", IndicatorType.BB, null, new[] { new AiChartLineSeries("Main", Constant(100, 1000m)), new AiChartLineSeries("Upper", Constant(100, 1015m)), new AiChartLineSeries("Lower", Constant(100, 985m)) }),
        };
        var oscillators = new[] { new AiChartOscillator("macd", new[] { new AiChartLineSeries("Main", candles.Select(c => (decimal?)(c.Close - 1000m)).ToArray()) }, null, null, true) };
        AiChartImageRequest request = Request(candles, 900) with { Overlays = overlays, Oscillators = oscillators, Markers = new[] { new AiChartMarker(20, true), new AiChartMarker(60, false) } };

        using SKBitmap first = SKBitmap.Decode(AiChartImageRenderer.Render(request).Png);
        using SKBitmap second = SKBitmap.Decode(AiChartImageRenderer.Render(request).Png);

        Assert.True(first.Bytes.AsSpan().SequenceEqual(second.Bytes));
    }
}
