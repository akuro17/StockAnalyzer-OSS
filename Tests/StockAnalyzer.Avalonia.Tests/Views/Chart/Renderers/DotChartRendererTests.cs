using System;
using System.Collections.Generic;
using Avalonia;
using Moq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

public class DotChartRendererTests : IDisposable
{
    private readonly DotChartRenderer _renderer = new();

    public void Dispose()
    {
        _renderer.Dispose();
    }

    [Theory]
    [InlineData(0.1f, 3.0f, 0.5f)]
    [InlineData(0.5f, 3.0f, 0.5f)]
    [InlineData(1.0f, 3.0f, 0.5f)]
    [InlineData(2.0f, 3.0f, 0.9f)]
    public void CalculateEffectiveRadius_ZoomOut_ClampsToMinRadius(float candleWidth, float baseRadius, float expectedRadius)
    {
        float r = DotChartRenderer.CalculateEffectiveRadius(baseRadius, candleWidth);
        Assert.Equal(expectedRadius, r, precision: 4);
        Assert.True(r >= 0.5f);
    }

    [Theory]
    [InlineData(20.0f, 3.0f, 3.0f)]
    [InlineData(50.0f, 5.0f, 5.0f)]
    [InlineData(10.0f, 0.5f, 0.5f)]
    public void CalculateEffectiveRadius_ZoomIn_ClampsToBaseRadius(float candleWidth, float baseRadius, float expectedRadius)
    {
        float r = DotChartRenderer.CalculateEffectiveRadius(baseRadius, candleWidth);
        Assert.Equal(expectedRadius, r, precision: 4);
    }

    [Fact]
    public void CalculateEffectiveRadius_SpacingMargin_RetainsTenPercentBuffer()
    {
        // candleWidth = 4.0, 4.0 * 0.45 = 1.80
        float r = DotChartRenderer.CalculateEffectiveRadius(5.0f, 4.0f);
        Assert.Equal(1.80f, r, precision: 4);

        // Diameter = 3.6, spacing margin = 4.0 - 3.6 = 0.4 (10% of 4.0)
        float diameter = r * 2f;
        Assert.Equal(3.60f, diameter, precision: 4);
    }

    [Fact]
    public void CalculateDiamondRadius_VisualAreaMatchesCircle()
    {
        float r = 4.0f;
        float diamondR = DotChartRenderer.CalculateDiamondRadius(r);

        // Circle Area: pi * r^2
        double circleArea = Math.PI * r * r;

        // Diamond (Rhombus) Area: 1/2 * (2 * diamondR) * (2 * diamondR) = 2 * diamondR^2
        double diamondArea = 2.0 * diamondR * diamondR;

        // Visual areas should match within 0.1% tolerance
        double ratio = diamondArea / circleArea;
        Assert.InRange(ratio, 0.999, 1.001);
    }

    [Fact]
    public void EvaluateDirection_CalculatesCorrectTriState()
    {
        // True initial bar (no preceding price) -> Neutral (0)
        Assert.Equal(0, DotChartRenderer.EvaluateDirection(100m, null));

        // Price increase -> Up (1)
        Assert.Equal(1, DotChartRenderer.EvaluateDirection(105m, 100m));

        // Price decrease -> Down (-1)
        Assert.Equal(-1, DotChartRenderer.EvaluateDirection(95m, 100m));

        // Price unchanged -> Neutral (0)
        Assert.Equal(0, DotChartRenderer.EvaluateDirection(100m, 100m));
    }

    private sealed class TestDotChartRenderConfig : IDotChartRenderConfig
    {
        public IThemeManager ThemeManager { get; } = new ThemeManager();
        public ChartType ChartType { get; set; } = ChartType.Dot;
        public double CurrentPrice => 100.0;
        public IndicatorColor BullishColor => new(255, 38, 166, 154);
        public IndicatorColor BearishColor => new(255, 239, 83, 80);
        public NamedColor ReversalLabelColor => new("Green", SKColors.Green);
        public NamedColor PriceLabelColor => new("Green", SKColors.Green);
        public int VisibleStartIndex => 0;
        public int VisibleCandleCount => 1000;
        public ICoordinateTransform? Transform { get; set; }
        public StockAnalyzer.Core.Models.Point MousePosition => default;
        public bool ShowMultiWavePatterns => false;
        public bool ShowGhostProjections => false;
        public float GhostProjectionFontSize => 12f;
        public bool ShowGhostLabelsOnHoverOnly => false;
        public double RenderScaling => 1.0;
        public bool IsSubWindowVisible => false;
        public bool InvertOscillator => false;
        public double DefaultDrawingThickness => 1.0;
        public bool CrosshairLabelVisible => false;
        public IndicatorColor NeutralColor => new(255, 178, 181, 190);
        public IndicatorColor DotUpColor { get; set; } = new(255, 38, 166, 154);
        public IndicatorColor DotDownColor { get; set; } = new(255, 239, 83, 80);
        public IndicatorColor DotNaturalColor { get; set; } = new(255, 178, 181, 190);
        public double DotBaseRadius { get; set; } = 3.0;
        public DotShapeType DotShape { get; set; } = DotShapeType.Circle;
        public PriceType DotPriceType { get; set; } = PriceType.Close;
        public int MultiWavePatternMaxLines => 0;
        public IndicatorColor? MultiWaveBullishColor => null;
        public IndicatorColor? MultiWaveBearishColor => null;
    }

    private static IDotChartRenderConfig CreateTestConfig(
        DotShapeType shape = DotShapeType.Circle,
        double baseRadius = 3.0,
        PriceType priceType = PriceType.Close,
        ICoordinateTransform? transform = null)
    {
        return new TestDotChartRenderConfig
        {
            DotShape = shape,
            DotBaseRadius = baseRadius,
            DotPriceType = priceType,
            Transform = transform
        };
    }

    [Fact]
    public void Render_ZeroAllocation_DuringRenderingLoop()
    {
        using var bitmap = new SKBitmap(1000, 600);
        using var canvas = new SKCanvas(bitmap);
        var chartArea = new Rect(0, 0, 1000, 600);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 600);
        var baseDate = new DateTime(2024, 1, 1);
        transform.UpdateRange(baseDate, baseDate.AddDays(1000), 50m, 150m);

        // Generate 1000 candles
        var candles = new List<CoreCandleData>(1000);
        for (int i = 0; i < 1000; i++)
        {
            decimal p = 100m + (decimal)Math.Sin(i * 0.1) * 20m;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), p, p + 1m, p - 1m, p, 1000));
        }

        var snapshot = new ChartDataSnapshot(
            candles,
            startIndex: 0,
            count: 1000,
            chartType: ChartType.Dot,
            allCandles: candles);

        var config = CreateTestConfig(DotShapeType.Diamond, 3.0, PriceType.Close, transform);

        // Warm up JIT and renderer
        _renderer.Render(canvas, chartArea, snapshot, config);

        // Measure allocations during hot path execution
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        _renderer.Render(canvas, chartArea, snapshot, config);
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        long allocated = afterAlloc - beforeAlloc;
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void Render_ScrolledViewport_DoesNotThrowAndRendersProperly()
    {
        using var bitmap = new SKBitmap(800, 400);
        using var canvas = new SKCanvas(bitmap);
        var chartArea = new Rect(0, 0, 800, 400);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 800, 400);
        var baseDate = new DateTime(2024, 1, 1);
        transform.UpdateRange(baseDate, baseDate.AddDays(100), 50m, 150m);

        var allCandles = new List<CoreCandleData>(100);
        for (int i = 0; i < 100; i++)
        {
            allCandles.Add(new CoreCandleData(baseDate.AddDays(i), 100m + i, 105m + i, 95m + i, 102m + i, 1000));
        }

        // Viewport starts at index 20 (scrolled)
        var snapshot = new ChartDataSnapshot(
            allCandles,
            startIndex: 20,
            count: 50,
            chartType: ChartType.Dot,
            allCandles: allCandles);

        var config = CreateTestConfig(DotShapeType.Circle, 3.0, PriceType.Close, transform);

        // Should render smoothly using preceding candle at index 19 without exceptions
        _renderer.Render(canvas, chartArea, snapshot, config);
    }

    [Theory]
    [InlineData(PriceType.HeikinAshiClose)]
    [InlineData(PriceType.HeikinAshiOpen)]
    [InlineData(PriceType.HeikinAshiHigh)]
    [InlineData(PriceType.HeikinAshiLow)]
    public void Render_WithHeikinAshiPriceType_TracksStateAndRendersSmoothly(PriceType haPriceType)
    {
        using var bitmap = new SKBitmap(800, 400);
        using var canvas = new SKCanvas(bitmap);
        var chartArea = new Rect(0, 0, 800, 400);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 800, 400);
        var baseDate = new DateTime(2024, 1, 1);
        transform.UpdateRange(baseDate, baseDate.AddDays(100), 50m, 150m);

        var allCandles = new List<CoreCandleData>(100);
        for (int i = 0; i < 100; i++)
        {
            allCandles.Add(new CoreCandleData(baseDate.AddDays(i), 100m + i, 105m + i, 95m + i, 102m + i, 1000));
        }

        // Viewport starts at index 30 (scrolled)
        var snapshot = new ChartDataSnapshot(
            allCandles,
            startIndex: 30,
            count: 50,
            chartType: ChartType.Dot,
            allCandles: allCandles);

        var config = CreateTestConfig(DotShapeType.Circle, 3.0, haPriceType, transform);

        // Should render smoothly with Heikin-Ashi warm up and recursive state chaining without exceptions
        _renderer.Render(canvas, chartArea, snapshot, config);
    }
}
