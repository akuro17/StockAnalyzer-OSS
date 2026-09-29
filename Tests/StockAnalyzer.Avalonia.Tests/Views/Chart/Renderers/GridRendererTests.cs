using System;
using Avalonia;
using Moq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

public class GridRendererTests : IDisposable
{
    private readonly GridRenderer _renderer = new();

    public void Dispose()
    {
        _renderer.Dispose();
    }

    private static IChartRenderConfig CreateConfig()
    {
        var configMock = new Mock<IChartRenderConfig>();
        var themeManager = new ThemeManager();
        configMock.SetupGet(c => c.ThemeManager).Returns(themeManager);
        configMock.SetupGet(c => c.ChartType).Returns(ChartType.Candlestick);
        configMock.SetupGet(c => c.IsSubWindowVisible).Returns(true);
        return configMock.Object;
    }

    [Fact]
    public void RenderPanel_WithCorrelationFixedRange_RendersWithoutException()
    {
        using var bitmap = new SKBitmap(800, 200);
        using var canvas = new SKCanvas(bitmap);

        var panelArea = new Rect(0, 0, 800, 200);
        var config = CreateConfig();

        // Correlation fixed range: -1.0 to 1.0 (minVal = -1.0m, priceRange = 2.0m)
        // This exercises the 0.0 baseline line and -1.00, 0.00, 1.00 labels
        _renderer.RenderPanel(canvas, panelArea, -1.0m, 2.0m, totalCandles: 50, config: config, isFixedRange: true);

        Assert.NotNull(bitmap);
    }

    [Fact]
    public void RenderPanel_WithGranvilleLawFixedRange_RendersWithoutException()
    {
        using var bitmap = new SKBitmap(800, 200);
        using var canvas = new SKCanvas(bitmap);

        var panelArea = new Rect(0, 0, 800, 200);
        var config = CreateConfig();

        // Granville Law fixed range: -4 to 4 (minVal = -4.0m, priceRange = 8.0m)
        _renderer.RenderPanel(canvas, panelArea, -4.0m, 8.0m, totalCandles: 50, config: config, isFixedRange: true);

        Assert.NotNull(bitmap);
    }

    [Fact]
    public void RenderPanel_WithRsiFixedRange_RendersWithoutException()
    {
        using var bitmap = new SKBitmap(800, 200);
        using var canvas = new SKCanvas(bitmap);

        var panelArea = new Rect(0, 0, 800, 200);
        var config = CreateConfig();

        // RSI fixed range: 0 to 100 (minVal = 0.0m, priceRange = 100.0m)
        _renderer.RenderPanel(canvas, panelArea, 0.0m, 100.0m, totalCandles: 50, config: config, isFixedRange: true);

        Assert.NotNull(bitmap);
    }

    [Fact]
    public void RenderPanel_WithIfftInstantaneousPhaseFixedRange_RendersWithoutException()
    {
        using var bitmap = new SKBitmap(800, 200);
        using var canvas = new SKCanvas(bitmap);

        var panelArea = new Rect(0, 0, 800, 200);
        var config = CreateConfig();

        // IFFT Instantaneous Phase's SineWave/LeadSine fixed range: -1.0 to 1.0
        // (minVal = -1.0m, priceRange = 2.0m). Same shape as the Correlation fixed range above,
        // so it exercises the same 0.0 baseline (minVal < 0 && maxVal > 0) branch.
        _renderer.RenderPanel(canvas, panelArea, -1.0m, 2.0m, totalCandles: 50, config: config, isFixedRange: true);

        Assert.NotNull(bitmap);
    }
}
