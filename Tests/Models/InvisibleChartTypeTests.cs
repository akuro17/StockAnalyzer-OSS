using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Models;

/// <summary>
/// Tests validating the declaration, capability registration, profile resolution,
/// and renderer dispatch for <see cref="ChartType.Invisible"/>.
/// </summary>
public class InvisibleChartTypeTests
{
    [Fact]
    public void ChartTypeCapabilities_Invisible_ReturnsExpectedProperties()
    {
        // Act
        var capabilities = ChartTypeCapabilitiesRegistry.Get(ChartType.Invisible);

        // Assert
        Assert.NotNull(capabilities);
        Assert.True(capabilities.IsTimeBased);
        Assert.False(capabilities.IsIndexBased);
        Assert.False(capabilities.IsCompactType);
        Assert.True(capabilities.HasStandardHeader);
        Assert.True(capabilities.SupportsIndicators);
        Assert.True(capabilities.CanToggleIndicators);
    }

    [Fact]
    public void ChartTypeExtensions_Invisible_DelegatesToCapabilitiesRegistry()
    {
        // Act & Assert
        Assert.True(ChartType.Invisible.IsTimeBased());
        Assert.False(ChartType.Invisible.IsIndexBased());
        Assert.False(ChartType.Invisible.IsCompactType());
        Assert.True(ChartType.Invisible.HasStandardHeader());
        Assert.True(ChartType.Invisible.SupportsIndicators());
    }

    [Fact]
    public void ChartTypeProfileRegistry_Invisible_ReturnsInvisibleProfile()
    {
        // Act
        var profile = ChartTypeProfileRegistry.Get(ChartType.Invisible);

        // Assert
        Assert.NotNull(profile);
        Assert.IsType<InvisibleProfile>(profile);
        Assert.Equal(ChartType.Invisible, profile.Type);
        Assert.IsType<StandardPriceRangeCalculator>(profile.PriceRangeCalculator);
        Assert.IsType<StandardChartLayoutProvider>(profile.LayoutProvider);
    }

    [Fact]
    public void InvisibleProfile_CreateRenderer_ReturnsNoOpRenderer()
    {
        // Arrange
        var profile = ChartTypeProfileRegistry.Get(ChartType.Invisible);

        // Act
        var renderer = profile.CreateRenderer();

        // Assert
        Assert.NotNull(renderer);
        Assert.IsType<ChartRendererRegistry.NoOpRenderer>(renderer);
    }

    [Fact]
    public void ChartRendererRegistry_GetRenderer_ReturnsNoOpRendererForInvisible()
    {
        // Arrange
        using var registry = new ChartRendererRegistry();

        // Act
        var renderer = registry.GetRenderer(ChartType.Invisible);

        // Assert
        Assert.NotNull(renderer);
        Assert.IsType<ChartRendererRegistry.NoOpRenderer>(renderer);
    }

    [Fact]
    public void NoOpRenderer_Render_ExecutesWithoutException()
    {
        // Arrange
        var renderer = new ChartRendererRegistry.NoOpRenderer();
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        var rect = new global::Avalonia.Rect(0, 0, 100, 100);
        var snapshot = ChartDataSnapshot.Empty;
        var config = new CandlestickRenderConfig();

        // Act & Assert
        var exception = Record.Exception(() => renderer.Render(canvas, rect, snapshot, config));
        Assert.Null(exception);
    }
}
