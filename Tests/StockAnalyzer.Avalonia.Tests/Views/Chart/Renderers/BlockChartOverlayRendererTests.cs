using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Moq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using Xunit;
using CoreCandle = StockAnalyzer.Core.Models.CoreCandleData;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

/// <summary>
/// TC-15 (draw style) and TC-16 (non-time-series main charts) of the block chart overlays. The draw tests render to a bitmap and read the pixels that the plan fixes:
/// the Close line is one step line in the up colour while IsUpTrend is 1 and in the down colour while it is 0, the Reversal / Update values are filled circles, Kagi and Three Line Break
/// have no Update dots, a single null bar (an invalid input bar) leaves a gap in the line, and on every non-time-series chart type nothing is drawn and the price axis ignores the overlays.
/// </summary>
public class BlockChartOverlayRendererTests
{
    private const int Width = 400;
    private const int Height = 200;
    private const int CandleCount = 20;
    private const double Thickness = 4.0;
    private const double DotSize = 12.0;   // diameter; radius 6
    private const float CandleWidth = (float)Width / CandleCount;
    private static readonly DateTime Start = new(2026, 1, 1);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly Rect Area = new(0, 0, Width, Height);

    private static readonly IndicatorColor Up = new(255, 0, 200, 0);
    private static readonly IndicatorColor Down = new(255, 220, 0, 0);
    private static readonly IndicatorColor Single = new(255, 0, 0, 220);
    private static readonly IndicatorColor ReversalColor = new(255, 255, 255, 0);
    private static readonly IndicatorColor UpdateColor = new(255, 255, 0, 255);

    private static SKColor Sk(IndicatorColor c) => new(c.R, c.G, c.B, c.A);

    private static List<CoreCandle> Candles() =>
        Enumerable.Range(0, CandleCount).Select(i => new CoreCandle(Start.AddDays(i), 100m, 105m, 95m, 100m, 1000)).ToList();

    private static GenericCoordinateTransform Transform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(Start, Start.AddDays(CandleCount), 90m, 130m);
        return transform;
    }

    private static float CenterX(GenericCoordinateTransform t, int i) => (float)t.ChartToScreen(new ChartPoint(Start.AddDays(i), 0)).X + CandleWidth / 2f;

    private static float PriceY(GenericCoordinateTransform t, decimal price) => (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, price)).Y;

    private static CoreIndicatorSettings Settings(bool upDown = true, double thickness = Thickness, double dotSize = DotSize) => new()
    {
        TypeEnum = IndicatorType.Renko,
        Thickness = thickness,
        DotSize = dotSize,
        Color = Single,
        UpColor = Up,
        DownColor = Down,
        UseUpDownColors = upDown,
    };

    private static decimal?[] Series(Func<int, decimal?> value) => Enumerable.Range(0, CandleCount).Select(value).ToArray();

    /// <summary>Close: null for bars 0-1, 100 for bars 2-5, 110 for bars 6-9, 104 from bar 10. IsUpTrend: 1 up to bar 9, 0 from bar 10 (null where Close is null).</summary>
    private static (decimal?[] Close, decimal?[] Flag) TwoColorSeries() => (
        Series(i => i < 2 ? null : i < 6 ? 100m : i < 10 ? 110m : 104m),
        Series(i => i < 2 ? null : i < 10 ? 1m : 0m));

    private static SKBitmap Draw(Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        draw(canvas);
        return bitmap;
    }

    private static SKBitmap RenderOverlay(CoreIndicatorSettings setting, decimal?[] close, decimal?[]? flag, decimal?[]? reversal, decimal?[]? update, BlockChartOverlayRenderer? renderer = null)
    {
        var transform = Transform();
        var candles = Candles();
        var own = renderer == null ? new BlockChartOverlayRenderer() : null;
        try
        {
            return Draw(c => (renderer ?? own!).Render(c, Area, close, flag, reversal, update, setting, Sk(ReversalColor), Sk(UpdateColor), CandleCount, 0, 0, candles, Interval, transform));
        }
        finally
        {
            own?.Dispose();
        }
    }

    private static SKColor PixelAt(SKBitmap bitmap, float x, float y) => bitmap.GetPixel((int)Math.Floor(x), (int)Math.Floor(y));

    private static int Count(SKBitmap bitmap, SKColor color)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) == color) count++;
        return count;
    }

    // ---------------------------------------------------------------- TC-15: draw style

    [Fact]
    public void CloseLine_IsOneStepLine_UpColourWhileIsUpTrendIsOne_DownColourWhileItIsZero()
    {
        var (close, flag) = TwoColorSeries();
        var t = Transform();
        using var bitmap = RenderOverlay(Settings(), close, flag, null, null);

        // The vertical part of the step into bar 6 (100 -> 110): the segment that ARRIVES at bar 6 has IsUpTrend 1.
        Assert.Equal(Sk(Up), PixelAt(bitmap, CenterX(t, 6), (PriceY(t, 100m) + PriceY(t, 110m)) / 2f));
        // The horizontal part into bar 8 (flat at 110) is up as well.
        Assert.Equal(Sk(Up), PixelAt(bitmap, (CenterX(t, 7) + CenterX(t, 8)) / 2f, PriceY(t, 110m)));
        // The step into bar 10 (110 -> 104) arrives at IsUpTrend 0: down colour, vertical and the horizontal run after it.
        Assert.Equal(Sk(Down), PixelAt(bitmap, CenterX(t, 10), (PriceY(t, 110m) + PriceY(t, 104m)) / 2f));
        Assert.Equal(Sk(Down), PixelAt(bitmap, (CenterX(t, 12) + CenterX(t, 13)) / 2f, PriceY(t, 104m)));
        // Nothing of the single colour and nothing before the first block (bars 0-1 are null).
        Assert.Equal(0, Count(bitmap, Sk(Single)));
        Assert.Equal(SKColors.Black, PixelAt(bitmap, CenterX(t, 0), PriceY(t, 100m)));
        Assert.Equal(SKColors.Black, PixelAt(bitmap, CenterX(t, 1), PriceY(t, 100m)));
    }

    [Fact]
    public void CloseLine_WithUpDownColoursOff_UsesTheSingleColourEverywhere()
    {
        var (close, flag) = TwoColorSeries();
        var t = Transform();
        using var bitmap = RenderOverlay(Settings(upDown: false), close, flag, null, null);

        Assert.Equal(Sk(Single), PixelAt(bitmap, CenterX(t, 6), (PriceY(t, 100m) + PriceY(t, 110m)) / 2f));
        Assert.Equal(Sk(Single), PixelAt(bitmap, CenterX(t, 10), (PriceY(t, 110m) + PriceY(t, 104m)) / 2f));
        Assert.Equal(0, Count(bitmap, Sk(Up)));
        Assert.Equal(0, Count(bitmap, Sk(Down)));
    }

    [Fact]
    public void ReversalAndUpdateValues_AreFilledCircles_ReversalAboveUpdate_DiameterFromTheDotSizeSetting()
    {
        var close = Series(i => 100m);
        var reversal = Series(i => i == 6 ? 120m : i == 16 ? 125m : null);
        var update = Series(i => i == 12 ? 115m : i == 16 ? 125m : null);   // bar 16: both at the same point -> the later (Reversal) wins
        var t = Transform();
        using var bitmap = RenderOverlay(Settings(dotSize: DotSize), close, Series(i => 1m), reversal, update);

        float radius = (float)(DotSize / 2.0);
        Assert.Equal(6f, radius);

        Assert.Equal(Sk(ReversalColor), PixelAt(bitmap, CenterX(t, 6), PriceY(t, 120m)));
        Assert.Equal(Sk(ReversalColor), PixelAt(bitmap, CenterX(t, 6) + radius - 1.5f, PriceY(t, 120m)));       // inside the circle
        Assert.Equal(SKColors.Black, PixelAt(bitmap, CenterX(t, 6) + radius + 2f, PriceY(t, 120m)));           // outside
        Assert.Equal(Sk(UpdateColor), PixelAt(bitmap, CenterX(t, 12), PriceY(t, 115m)));
        Assert.Equal(Sk(ReversalColor), PixelAt(bitmap, CenterX(t, 16), PriceY(t, 125m)));
        Assert.Equal(0, Count(bitmap, Sk(Single)));   // dots are not drawn in the line colour
    }

    [Fact]
    public void TheDotSizeSetting_ChangesTheDotNotTheLine_AndIsIndependentOfTheLineThickness()
    {
        var close = Series(i => 100m);
        var reversal = Series(i => i == 6 ? 120m : null);
        var t = Transform();
        float x = CenterX(t, 6);
        float y = PriceY(t, 120m);

        using var small = RenderOverlay(Settings(thickness: Thickness, dotSize: 4.0), close, null, reversal, null);
        using var large = RenderOverlay(Settings(thickness: Thickness, dotSize: 16.0), close, null, reversal, null);
        using var thinLineLargeDot = RenderOverlay(Settings(thickness: 0.5, dotSize: 16.0), close, null, reversal, null);

        // diameter 4 (radius 2): the point 5 px to the side is outside; diameter 16 (radius 8): it is inside. The line thickness does not matter.
        Assert.Equal(SKColors.Black, PixelAt(small, x + 5f, y));
        Assert.Equal(Sk(ReversalColor), PixelAt(large, x + 5f, y));
        Assert.Equal(Sk(ReversalColor), PixelAt(thinLineLargeDot, x + 5f, y));
        Assert.True(Count(large, Sk(ReversalColor)) > Count(small, Sk(ReversalColor)));
    }

    [Theory]
    [InlineData(0.0, 1.0)]                                                         // below the range: the smallest dot
    [InlineData(-5.0, 1.0)]
    [InlineData(1000.0, 10.0)]                                                     // above the range: the largest dot
    [InlineData(double.NaN, 2.25)]                                                 // not finite: the default
    [InlineData(double.PositiveInfinity, 2.25)]
    [InlineData(4.5, 2.25)]
    public void TheDotRadius_IsHalfTheSetting_KeptInsideTheRange(double dotSize, double expectedRadius)
    {
        Assert.Equal((float)expectedRadius, BlockChartOverlayRenderer.DotRadius(dotSize));
    }

    [Fact]
    public void TheDefaultDotSize_GivesTheDotOfTheFormerRule_SoTheDefaultLookIsUnchanged()
    {
        // The rule before the setting existed: radius = max(2, 1.5 x thickness) for the default thickness of the four overlays.
        const double formerMinRadius = 2.0;
        const double formerRadiusPerThickness = 1.5;
        double former = Math.Max(formerMinRadius, IndicatorDefaultConstants.DefaultOverlayThickness * formerRadiusPerThickness);

        Assert.Equal((float)former, BlockChartOverlayRenderer.DotRadius(IndicatorDefaultConstants.BlockChartOverlayDefaultDotSize));
        foreach (var type in BlockChartOverlayTypes.All)
        {
            Assert.Equal(IndicatorDefaultConstants.BlockChartOverlayDefaultDotSize, DefaultCoreIndicatorSettings.GetDefault().Single(d => d.TypeEnum == type).DotSize);
        }
    }

    [Fact]
    public void WithoutAnUpdateSeries_NoUpdateDotIsDrawn_ForKagiAndThreeLineBreak()
    {
        var close = Series(i => 100m);
        var reversal = Series(i => i % 4 == 0 ? 120m : null);
        using var bitmap = RenderOverlay(Settings(), close, null, reversal, null);

        Assert.Equal(0, Count(bitmap, Sk(UpdateColor)));
        Assert.True(Count(bitmap, Sk(ReversalColor)) > 0);
    }

    [Fact]
    public void ASingleNullBar_LeavesAGapInTheLine_AndNoSegmentSpansIt()
    {
        var t = Transform();
        var flag = Series(i => 1m);
        var continuous = Series(i => 100m);
        var withGap = Series(i => i == 8 ? null : 100m);

        using var whole = RenderOverlay(Settings(), continuous, flag, null, null);
        using var gap = RenderOverlay(Settings(), withGap, flag, null, null);

        float y = PriceY(t, 100m);
        // The continuous line is under bar 8; with one null bar there is nothing between bar 7 and bar 9 (the line of bar 7 ends at its centre, the line of bar 9 starts at its centre).
        Assert.Equal(Sk(Up), PixelAt(whole, CenterX(t, 8), y));
        Assert.Equal(SKColors.Black, PixelAt(gap, CenterX(t, 8), y));
        Assert.Equal(SKColors.Black, PixelAt(gap, CenterX(t, 8) - 6f, y));
        Assert.Equal(SKColors.Black, PixelAt(gap, CenterX(t, 8) + 6f, y));
        // Both sides of the gap are still drawn.
        Assert.Equal(Sk(Up), PixelAt(gap, (CenterX(t, 5) + CenterX(t, 6)) / 2f, y));
        Assert.Equal(Sk(Up), PixelAt(gap, (CenterX(t, 10) + CenterX(t, 11)) / 2f, y));
    }

    [Fact]
    public void AllNullSeries_DrawNothing_AndAMissingFlagFallsBackToTheSingleColour()
    {
        using var empty = RenderOverlay(Settings(), Series(_ => null), Series(_ => null), Series(_ => null), Series(_ => null));
        Assert.Equal(0, Count(empty, SKColors.Black) - Width * Height);

        var t = Transform();
        using var noFlag = RenderOverlay(Settings(), Series(i => i < 8 ? 100m : 110m), null, null, null);
        Assert.Equal(Sk(Single), PixelAt(noFlag, CenterX(t, 8), (PriceY(t, 100m) + PriceY(t, 110m)) / 2f));
    }

    [Fact]
    public void RepeatedCalls_DrawIdenticalPixels_AndALongerEarlierLineLeavesNothingBehind()
    {
        var (close, flag) = TwoColorSeries();
        using var renderer = new BlockChartOverlayRenderer();

        using var first = RenderOverlay(Settings(), close, flag, null, null, renderer);
        using var shorter = RenderOverlay(Settings(), close.Select((v, i) => i < 8 ? v : null).ToArray(), flag, null, null, renderer);
        using var again = RenderOverlay(Settings(), close, flag, null, null, renderer);

        Assert.Equal(first.Bytes, again.Bytes);
        Assert.NotEqual(first.Bytes, shorter.Bytes);
        using var fresh = RenderOverlay(Settings(), close.Select((v, i) => i < 8 ? v : null).ToArray(), flag, null, null);
        Assert.Equal(fresh.Bytes, shorter.Bytes);
    }

    // ---------------------------------------------------------------- through IndicatorRenderer with the real indicators

    private static List<CoreCandle> ZigZagCandles(int count)
    {
        // +1.5 per bar for 12 bars, -1.5 for 14, +1.5 for 12, ... : every overlay sees up and down moves, reversals and updates.
        var candles = new List<CoreCandle>(count);
        decimal level = 100m;
        for (int i = 0; i < count; i++)
        {
            bool up = i % 26 < 12;
            decimal open = level;
            level += up ? 1.5m : -1.5m;
            candles.Add(new CoreCandle(Start.AddDays(i), open, Math.Max(open, level) + 0.5m, Math.Min(open, level) - 0.5m, level, 1000));
        }

        return candles;
    }

    private static CoreIndicatorSettings Enabled(IndicatorType type)
    {
        var s = DefaultCoreIndicatorSettings.GetDefault().Single(d => d.TypeEnum == type);
        s.IsEnabled = true;
        s.Thickness = Thickness;
        s.UpColor = Up;
        s.DownColor = Down;
        s.Color = Single;
        foreach (var config in s.SeriesColors)
        {
            if (config.Name == nameof(CoreBlockChartOverlayIndicatorBase.Reversal)) config.Color = ReversalColor;
            if (config.Name == CoreBlockChartOverlayIndicatorBase.UpdateSeriesName) config.Color = UpdateColor;
        }

        switch (s.ParameterObject)
        {
            case CoreRenkoChartParameter r: r.SizingMode = ChartSizingMode.Fixed; r.ManualSize = 3m; break;
            case CorePointAndFigureChartParameter p: p.SizingMode = ChartSizingMode.Fixed; p.ManualSize = 2m; break;
            case CoreKagiChartParameter k: k.SizingMode = ChartSizingMode.Fixed; k.ManualSize = 3m; break;
        }

        return s;
    }

    private static Mock<IChartRenderConfig> Config(ChartType type, ICoordinateTransform transform)
    {
        var config = new Mock<IChartRenderConfig>();
        config.SetupGet(c => c.ThemeManager).Returns(new ThemeManager());
        config.SetupGet(c => c.ChartType).Returns(type);
        config.SetupGet(c => c.Transform).Returns(transform);
        config.SetupGet(c => c.RenderScaling).Returns(1.0);
        config.SetupGet(c => c.VisibleStartIndex).Returns(0);
        return config;
    }

    private static ChartDataSnapshot Snapshot(List<CoreCandle> candles, ChartType type, IReadOnlyDictionary<string, IIndicatorResult>? results, params CoreIndicatorSettings[] settings)
        => new(candles, "TEST", "D1", results, settings, startIndex: 0, count: candles.Count, chartType: type);

    private static IReadOnlyDictionary<string, IIndicatorResult> Calculate(List<CoreCandle> candles, params CoreIndicatorSettings[] settings)
        => settings.ToDictionary(s => s.Id, s => (IIndicatorResult)IndicatorFactory.CreateStatic(s.TypeEnum!.Value, s.ParameterObject!)!.Calculate(candles));

    private static SKBitmap RenderThroughIndicatorRenderer(ChartDataSnapshot snapshot, ChartType type, CoreIndicatorSettings setting)
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(snapshot.Candles[0].Timestamp, snapshot.Candles[^1].Timestamp.Add(Interval), snapshot.MinPrice, snapshot.MaxPrice);
        var config = Config(type, transform);
        using var renderer = new IndicatorRenderer();
        return Draw(c => renderer.RenderIndicator(c, Area, snapshot, setting, config.Object));
    }

    [Theory]
    [InlineData(IndicatorType.Renko, true)]
    [InlineData(IndicatorType.PointAndFigure, true)]
    [InlineData(IndicatorType.Kagi, false)]
    [InlineData(IndicatorType.ThreeLineBreak, false)]
    public void OnATimeSeriesChart_TheFourOverlaysAreDrawn_WithUpAndDownColours_AndDotsOnlyWhereTheyHaveThem(IndicatorType type, bool hasUpdate)
    {
        var candles = ZigZagCandles(CandleCount * 4);
        var setting = Enabled(type);
        var results = Calculate(candles, setting);
        var result = results[setting.Id];

        // The data set really contains both directions and reversal values (otherwise the pixel assertions below would prove nothing).
        Assert.Contains(result.GetSeries(IndicatorSeriesNames.UpTrendFlag), v => v == 1m);
        Assert.Contains(result.GetSeries(IndicatorSeriesNames.UpTrendFlag), v => v == 0m);
        Assert.Contains(result.GetSeries("Reversal"), v => v.HasValue);
        Assert.Equal(hasUpdate, result.HasSeries("Update"));

        var snapshot = Snapshot(candles, ChartType.Candlestick, results, setting);
        using var bitmap = RenderThroughIndicatorRenderer(snapshot, ChartType.Candlestick, setting);

        Assert.True(Count(bitmap, Sk(Up)) > 0, "up-coloured line");
        Assert.True(Count(bitmap, Sk(Down)) > 0, "down-coloured line");
        Assert.Equal(0, Count(bitmap, Sk(Single)));
        Assert.True(Count(bitmap, Sk(ReversalColor)) > 0, "Reversal dots");
        Assert.Equal(hasUpdate, Count(bitmap, Sk(UpdateColor)) > 0);
    }

    // ---------------------------------------------------------------- TC-16: non-time-series main charts

    public static IEnumerable<object[]> IndexBasedChartTypes() =>
        Enum.GetValues<ChartType>().Where(t => t.IsIndexBased()).Select(t => new object[] { t });

    [Fact]
    public void TheIndexBasedChartTypes_AreTheFourBlockChartsAtLeast()
    {
        var types = IndexBasedChartTypes().Select(o => (ChartType)o[0]).ToList();
        Assert.Contains(ChartType.Renko, types);
        Assert.Contains(ChartType.PointAndFigure, types);
        Assert.Contains(ChartType.Kagi, types);
        Assert.Contains(ChartType.ThreeLineBreak, types);
    }

    /// <summary>Overlay values far outside the candle range, so that a price axis that included them would be stretched visibly.</summary>
    private static (IReadOnlyDictionary<string, IIndicatorResult> Results, CoreIndicatorSettings[] Settings) FarOutsideOverlays(List<CoreCandle> candles)
    {
        var settings = BlockChartOverlayTypes.All.Select(Enabled).ToArray();
        var results = new Dictionary<string, IIndicatorResult>();
        foreach (var s in settings)
        {
            var series = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "Main", candles.Select(_ => (decimal?)5000m).ToArray() },
                { "Reversal", candles.Select(_ => (decimal?)9000m).ToArray() },
                { IndicatorSeriesNames.UpTrendFlag, candles.Select(_ => (decimal?)1m).ToArray() },
            };
            if (s.TypeEnum is IndicatorType.Renko or IndicatorType.PointAndFigure)
            {
                series.Add("Update", candles.Select(_ => (decimal?)7000m).ToArray());
            }

            results[s.Id] = IndicatorResult.Success(series);
        }

        return (results, settings);
    }

    [Theory]
    [MemberData(nameof(IndexBasedChartTypes))]
    public void OnANonTimeSeriesChart_TheOverlaysDrawNothing_AndTheyDoNotStretchThePriceAxis(ChartType type)
    {
        var candles = ZigZagCandles(CandleCount);
        var (results, settings) = FarOutsideOverlays(candles);

        var without = Snapshot(candles, type, null);
        var with = Snapshot(candles, type, results, settings);

        Assert.Equal(without.MinPrice, with.MinPrice);
        Assert.Equal(without.MaxPrice, with.MaxPrice);

        foreach (var setting in settings)
        {
            using var bitmap = RenderThroughIndicatorRenderer(with, type, setting);
            Assert.Equal(Width * Height, Count(bitmap, SKColors.Black));
        }
    }

    [Fact]
    public void OnATimeSeriesChart_TheSameOverlaysDoStretchThePriceAxis_SoTheGuardIsSpecificToNonTimeSeriesCharts()
    {
        var candles = ZigZagCandles(CandleCount);
        var (results, settings) = FarOutsideOverlays(candles);

        var without = Snapshot(candles, ChartType.Candlestick, null);
        var with = Snapshot(candles, ChartType.Candlestick, results, settings);

        Assert.Equal(without.MinPrice, with.MinPrice);
        Assert.True(with.MaxPrice >= 9000m, $"MaxPrice {with.MaxPrice}");   // the Reversal values are part of the axis; the IsUpTrend flag (1) is not
        Assert.True(without.MaxPrice < 200m);
    }
}
