using System;
using System.Collections.Generic;
using Avalonia;
using Moq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

public class VolumeProfileRendererTests
{
    private readonly VolumeProfileRenderer _renderer = new();

    private static IChartRenderConfig CreateConfig()
    {
        var configMock = new Mock<IChartRenderConfig>();
        var themeManager = new ThemeManager();
        configMock.SetupGet(c => c.ThemeManager).Returns(themeManager);
        configMock.SetupGet(c => c.ChartType).Returns(ChartType.Candlestick);
        configMock.SetupGet(c => c.VisibleStartIndex).Returns(0);
        return configMock.Object;
    }

    private static List<VolumeBin> CreateSampleProfile()
    {
        return new List<VolumeBin>
        {
            new() { Price = 105m, LowerBound = 100m, UpperBound = 110m, TotalVolume = 5000, WidthPercent = 0.5 },
            new() { Price = 115m, LowerBound = 110m, UpperBound = 120m, TotalVolume = 10000, WidthPercent = 1.0 },
            new() { Price = 125m, LowerBound = 120m, UpperBound = 130m, TotalVolume = 3000, WidthPercent = 0.3 }
        };
    }

    [Fact]
    public void Render_WithValidProfileAndSetting_DrawsHistogramBars()
    {
        using var bitmap = new SKBitmap(800, 400);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 800, 400);
        transform.UpdateRange(DateTime.Today, DateTime.Today.AddDays(10), 100m, 130m);

        var chartArea = new Rect(0, 0, 800, 400);
        var config = CreateConfig();
        var profile = CreateSampleProfile();

        var setting = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.VolumeProfile,
            Color = new IndicatorColor(255, 0, 255, 0), // Green
            ParameterObject = new CoreVolumeProfileParameter
            {
                Opacity = 0.8,
                Side = DisplaySide.Left
            }
        };

        _renderer.Render(canvas, chartArea, profile, 100m, 130m, transform, isRightSide: false, config, setting);

        Assert.Equal(new SKColor(0, 255, 0, (byte)(0.8 * 255)), _renderer.BarPaint.Color);

        // Verify that pixels were actually rendered in the left region
        bool hasRenderedPixel = false;
        for (int y = 0; y < 400; y += 10)
        {
            for (int x = 0; x < 100; x += 5)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    hasRenderedPixel = true;
                    break;
                }
            }
            if (hasRenderedPixel) break;
        }

        Assert.True(hasRenderedPixel, "Volume profile histogram should render non-transparent pixels in the left region.");
    }

    [Fact]
    public void Render_InvertedPriceScale_RendersWithoutExceptionAndDrawsPixels()
    {
        using var bitmap = new SKBitmap(800, 400);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 800, 400)
        {
            PriceScale = PriceScaleType.Inverted
        };
        transform.UpdateRange(DateTime.Today, DateTime.Today.AddDays(10), 100m, 130m);

        var chartArea = new Rect(0, 0, 800, 400);
        var config = CreateConfig();
        var profile = CreateSampleProfile();

        var setting = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.VolumeProfile,
            Color = new IndicatorColor(255, 255, 128, 0),
            ParameterObject = new CoreVolumeProfileParameter { Opacity = 1.0 }
        };

        _renderer.Render(canvas, chartArea, profile, 100m, 130m, transform, isRightSide: false, config, setting);

        bool hasRenderedPixel = false;
        for (int y = 0; y < 400; y += 10)
        {
            for (int x = 0; x < 100; x += 5)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    hasRenderedPixel = true;
                    break;
                }
            }
            if (hasRenderedPixel) break;
        }

        Assert.True(hasRenderedPixel, "Volume profile should render properly even with Inverted PriceScale.");
    }

    [Fact]
    public void Render_NullOrEmptyProfile_DoesNotThrow()
    {
        using var bitmap = new SKBitmap(800, 400);
        using var canvas = new SKCanvas(bitmap);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 800, 400);
        var chartArea = new Rect(0, 0, 800, 400);
        var config = CreateConfig();

        _renderer.Render(canvas, chartArea, null!, 100m, 200m, transform, false, config);
        _renderer.Render(canvas, chartArea, new List<VolumeBin>(), 100m, 200m, transform, false, config);
    }
}
