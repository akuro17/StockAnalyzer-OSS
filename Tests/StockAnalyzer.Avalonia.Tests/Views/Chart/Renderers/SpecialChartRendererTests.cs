using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Avalonia;
using Moq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Utilities;
using Xunit;
using Xunit.Abstractions;
using CoreCandle = StockAnalyzer.Core.Models.CoreCandleData;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

/// <summary>
/// The Renko, Kagi and Point and Figure renderers draw geometry only: bricks, Kagi lines, X and O marks. The values of these charts (counts, reversal
/// prices) are shown in the Data tab, never as canvas text. The tests render a fixed data set to a bitmap and require that something is drawn and that
/// drawing twice gives the same pixels (a renderer must not depend on state left by the previous frame). The pixel digest is written to the test output
/// so a refactoring of a renderer can be compared against the digest of the code before it (digests differ between platforms and are not asserted).
/// </summary>
public class SpecialChartRendererTests
{
    private const int Width = 600;
    private const int Height = 300;
    private const int PointCount = 40;

    private readonly ITestOutputHelper _output;

    public SpecialChartRendererTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly IndicatorColor Bullish = IndicatorColor.FromRgb(0, 160, 0);
    private static readonly IndicatorColor Bearish = IndicatorColor.FromRgb(200, 0, 0);

    private static List<CoreCandle> ZigZag(int count, decimal step, Func<int, bool> isUp, Func<int, long> volume)
    {
        var candles = new List<CoreCandle>(count);
        decimal level = 100m;
        var start = new DateTime(2026, 1, 1);
        for (int i = 0; i < count; i++)
        {
            bool up = isUp(i);
            decimal open = level;
            decimal close = up ? level + step : level - step;
            level = close;
            candles.Add(new CoreCandle(start.AddDays(i), open, Math.Max(open, close), Math.Min(open, close), close, volume(i)));
        }

        return candles;
    }

    private static GenericCoordinateTransform CreateTransform(ChartDataSnapshot snapshot, int columns)
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Index, Width, Height);
        transform.UpdateCanvasSize(Width, Height, 0, 0, Width, Height);
        transform.SetIndexRange(0, columns);
        transform.SetPriceRange(snapshot.MinPrice, snapshot.MaxPrice);
        return transform;
    }

    private static Mock<T> BaseConfig<T>(ChartType type, ICoordinateTransform transform) where T : class, IChartRenderConfig
    {
        var config = new Mock<T>();
        config.SetupGet(c => c.ThemeManager).Returns(new ThemeManager());
        config.SetupGet(c => c.ChartType).Returns(type);
        config.SetupGet(c => c.Transform).Returns(transform);
        config.SetupGet(c => c.CurrentPrice).Returns(123.45);
        config.SetupGet(c => c.RenderScaling).Returns(1.0);
        return config;
    }

    private static string RenderDigest(Action<SKCanvas> render, out int drawnPixels)
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        render(canvas);

        byte[] pixels = bitmap.Bytes;
        drawnPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255) drawnPixels++;
        }

        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private (string Digest, int Drawn) RenderTwiceAndCompare(string name, Action<SKCanvas> render)
    {
        string first = RenderDigest(render, out int drawn);
        string second = RenderDigest(render, out _);

        _output.WriteLine($"{name}: drawn pixels {drawn}, SHA-256 {first}");
        Assert.True(drawn > 0, $"{name} drew nothing.");
        Assert.Equal(first, second);
        return (first, drawn);
    }

    [Fact]
    public void Renko_RendersBricksAndIsRepeatable()
    {
        var candles = ZigZag(PointCount, 2m, i => i % 7 < 4, _ => 0);
        var snapshot = new ChartDataSnapshot(coreCandles: candles, chartType: ChartType.Renko);
        var transform = CreateTransform(snapshot, PointCount);
        var config = BaseConfig<IRenkoRenderConfig>(ChartType.Renko, transform);
        config.SetupGet(c => c.RenkoBullishColor).Returns(Bullish);
        config.SetupGet(c => c.RenkoBearishColor).Returns(Bearish);
        config.SetupGet(c => c.RenkoBrickSize).Returns(2.0);
        using var renderer = new RenkoRenderer();

        RenderTwiceAndCompare("Renko", canvas => renderer.Render(canvas, new Rect(0, 0, Width, Height), snapshot, config.Object));
    }

    [Fact]
    public void Kagi_RendersLinesAndIsRepeatable()
    {
        // Volume carries the Yang (1) / Yin (0) state of a Kagi segment.
        var candles = ZigZag(PointCount, 3m, i => i % 5 < 3, i => i % 4 < 2 ? 1 : 0);
        var snapshot = new ChartDataSnapshot(coreCandles: candles, chartType: ChartType.Kagi);
        var transform = CreateTransform(snapshot, PointCount);
        var config = BaseConfig<IKagiRenderConfig>(ChartType.Kagi, transform);
        config.SetupGet(c => c.KagiBullishColor).Returns(Bullish);
        config.SetupGet(c => c.KagiBearishColor).Returns(Bearish);
        config.SetupGet(c => c.KagiReversalAmount).Returns(3.0);
        config.SetupGet(c => c.KagiReversalPercent).Returns(4.0);
        config.SetupGet(c => c.KagiLineThickness).Returns(2f);
        using var renderer = new KagiRenderer();

        RenderTwiceAndCompare("Kagi", canvas => renderer.Render(canvas, new Rect(0, 0, Width, Height), snapshot, config.Object));
    }

    [Fact]
    public void PointAndFigure_RendersMarksAndIsRepeatable()
    {
        var candles = ZigZag(12, 4m, i => i % 2 == 0, _ => 0);
        var analysis = new PnfAnalysisResult(new List<PnfSignal>(), new List<PnfTrendline>());
        var snapshot = new ChartDataSnapshot(coreCandles: candles, chartType: ChartType.PointAndFigure, pnfAnalysis: analysis, minBrickSize: 1m, maxBrickSize: 1m);
        var transform = CreateTransform(snapshot, candles.Count);
        var config = BaseConfig<IPnfRenderConfig>(ChartType.PointAndFigure, transform);
        config.SetupGet(c => c.PnfBullishColor).Returns(Bullish);
        config.SetupGet(c => c.PnfBearishColor).Returns(Bearish);
        config.SetupGet(c => c.PnfBoxSize).Returns(1m);
        config.SetupGet(c => c.PnfReversalAmount).Returns(3);
        using var renderer = new PointAndFigureRenderer();

        RenderTwiceAndCompare("PointAndFigure", canvas => renderer.Render(canvas, new Rect(0, 0, Width, Height), snapshot, config.Object));
    }
}
