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

    // ---- RenderViewport: the Point of Control bin takes the POC colour (precedence POC > Value Area > base colour) ----

    private static readonly SKColor BaseBlue = new(0, 0, 255, 255);
    private static readonly SKColor Yellow = new(255, 235, 59, 255);
    private static readonly SKColor Red = new(255, 0, 0, 255);

    private static List<VolumeBin> ThreeBins(long low, long mid, long high) => new()
    {
        new() { Price = 105m, LowerBound = 100m, UpperBound = 110m, TotalVolume = low },
        new() { Price = 115m, LowerBound = 110m, UpperBound = 120m, TotalVolume = mid },
        new() { Price = 125m, LowerBound = 120m, UpperBound = 130m, TotalVolume = high }
    };

    private static VolumeProfileViewportResult OneSegment(List<VolumeBin> bins, decimal? poc, decimal? vah, decimal? val) => new()
    {
        RequestKey = "poc-color",
        Status = VolumeProfileResultStatus.Success,
        Segments = new[] { new VolumeProfileSegment { StartIndex = 0, Count = 10, Bins = bins, POC = poc, VAH = vah, VAL = val } }
    };

    private static CoreIndicatorSettings SettingFor(CoreIndicatorParameterBase parameter, IndicatorType type) => new()
    {
        TypeEnum = type,
        Color = new IndicatorColor(255, 0, 0, 255), // base colour: blue
        ParameterObject = parameter
    };

    /// <summary>Renders into an 800x400 bitmap (chart area = the whole bitmap, price range 100..130) and returns the transform for pixel lookups.</summary>
    private SKBitmap RenderViewport(VolumeProfileViewportResult result, CoreIndicatorSettings setting, out GenericCoordinateTransform transform)
    {
        var bitmap = new SKBitmap(800, 400);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 800, 400);
        transform.UpdateRange(DateTime.Today, DateTime.Today.AddDays(10), 100m, 130m);

        _renderer.RenderViewport(canvas, new Rect(0, 0, 800, 400), result, transform, CreateConfig(), setting);
        return bitmap;
    }

    private static SKColor PixelAt(SKBitmap bitmap, GenericCoordinateTransform transform, decimal price, int x)
        => bitmap.GetPixel(x, (int)Math.Round(transform.GetYFromPrice(price)));

    private static void AssertColor(SKColor expected, SKColor actual, string because)
    {
        Assert.True(
            expected.Alpha == actual.Alpha && Math.Abs(expected.Red - actual.Red) <= 1 && Math.Abs(expected.Green - actual.Green) <= 1 && Math.Abs(expected.Blue - actual.Blue) <= 1,
            $"{because}: expected {expected} but was {actual}");
    }

    [Fact]
    public void RenderViewport_PocBinTakesThePocColor_AheadOfTheValueAreaColor_AndOtherBinsKeepTheirColors()
    {
        // The POC bin (index 1) lies inside the Value Area (bins 0 and 1); bin 2 is outside it.
        var param = new CoreVolumeProfileParameter { Opacity = 1.0, PocColor = new IndicatorColor(255, 0, 200, 0) };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(5000, 10000, 3000), 115m, 120m, 100m), SettingFor(param, IndicatorType.VolumeProfile), out var transform);

        AssertColor(new SKColor(0, 200, 0, 255), PixelAt(bitmap, transform, 115m, 30), "the POC bin");
        AssertColor(Yellow, PixelAt(bitmap, transform, 105m, 30), "a Value Area bin that is not the POC");
        AssertColor(BaseBlue, PixelAt(bitmap, transform, 125m, 30), "a bin outside the Value Area");
    }

    [Fact]
    public void RenderViewport_PocColorDefaultsToRed_WhenNeverSet()
    {
        var param = new CoreVolumeProfileParameter { Opacity = 1.0 };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(5000, 10000, 3000), 115m, 120m, 100m), SettingFor(param, IndicatorType.VolumeProfile), out var transform);

        AssertColor(Red, PixelAt(bitmap, transform, 115m, 30), "the POC bin");
    }

    [Fact]
    public void RenderViewport_NullPocColor_ResolvesToRed()
    {
        var param = new CoreVolumeProfileParameter { Opacity = 1.0, PocColor = null };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(5000, 10000, 3000), 115m, 120m, 100m), SettingFor(param, IndicatorType.VolumeProfile), out var transform);

        AssertColor(Red, PixelAt(bitmap, transform, 115m, 30), "the POC bin");
    }

    [Fact]
    public void RenderViewport_TimeAtPriceParameter_UsesItsOwnPocColor()
    {
        var param = new CoreTimeAtPriceParameter { Opacity = 1.0, PocColor = new IndicatorColor(255, 0, 200, 0) };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(5000, 10000, 3000), 115m, 120m, 100m), SettingFor(param, IndicatorType.TimeAtPrice), out var transform);

        AssertColor(new SKColor(0, 200, 0, 255), PixelAt(bitmap, transform, 115m, 30), "the TPOC bin");
        AssertColor(BaseBlue, PixelAt(bitmap, transform, 125m, 30), "a bin outside the Value Area");
    }

    [Fact]
    public void RenderViewport_WithoutAValueArea_StillPaintsOnlyThePocBinWithThePocColor()
    {
        var param = new CoreVolumeProfileParameter { Opacity = 1.0 };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(5000, 10000, 3000), 115m, null, null), SettingFor(param, IndicatorType.VolumeProfile), out var transform);

        AssertColor(Red, PixelAt(bitmap, transform, 115m, 30), "the POC bin");
        AssertColor(BaseBlue, PixelAt(bitmap, transform, 105m, 30), "another bin");
        AssertColor(BaseBlue, PixelAt(bitmap, transform, 125m, 30), "another bin");
    }

    [Fact]
    public void RenderViewport_TiedMaximum_PaintsOnlyTheLowestPricedBinAsPoc()
    {
        var param = new CoreVolumeProfileParameter { Opacity = 1.0 };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(10000, 4000, 10000), 105m, null, null), SettingFor(param, IndicatorType.VolumeProfile), out var transform);

        AssertColor(Red, PixelAt(bitmap, transform, 105m, 30), "the lower of the two tied bins");
        AssertColor(BaseBlue, PixelAt(bitmap, transform, 125m, 30), "the higher tied bin");
    }

    [Fact]
    public void RenderViewport_AllBinsWeighZero_PaintsNoPocBinAndNothingElse()
    {
        // Every weight is 0: the segment has no POC (null) and no bin may be coloured as the Point of Control (index 0 included).
        var param = new CoreVolumeProfileParameter { Opacity = 1.0 };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(0, 0, 0), null, null, null), SettingFor(param, IndicatorType.VolumeProfile), out _);

        AssertNoRedPixel(bitmap);
    }

    [Fact]
    public void RenderViewport_AllBinsWeighZero_AlsoForTimeAtPrice_PaintsNoPocBin()
    {
        var param = new CoreTimeAtPriceParameter { Opacity = 1.0 };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(0, 0, 0), null, null, null), SettingFor(param, IndicatorType.TimeAtPrice), out _);

        AssertNoRedPixel(bitmap);
    }

    private static void AssertNoRedPixel(SKBitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                Assert.False(c.Alpha > 0 && c.Red > 200 && c.Green < 50 && c.Blue < 50, $"a POC-coloured pixel at ({x}, {y}): {c}");
            }
        }
    }

    [Fact]
    public void RenderViewport_SideBoth_PaintsThePocBinOnBothSides()
    {
        var param = new CoreVolumeProfileParameter { Opacity = 1.0, Side = DisplaySide.Both };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(5000, 10000, 3000), 115m, 120m, 100m), SettingFor(param, IndicatorType.VolumeProfile), out var transform);

        AssertColor(Red, PixelAt(bitmap, transform, 115m, 30), "left side");
        AssertColor(Red, PixelAt(bitmap, transform, 115m, 770), "right side");
        AssertColor(BaseBlue, PixelAt(bitmap, transform, 125m, 770), "a non-POC bin on the right side");
    }

    [Fact]
    public void RenderViewport_PocBinUsesTheSameOpacityAsTheOtherBins()
    {
        var param = new CoreVolumeProfileParameter { Opacity = 0.5 };
        using var bitmap = RenderViewport(OneSegment(ThreeBins(5000, 10000, 3000), 115m, 120m, 100m), SettingFor(param, IndicatorType.VolumeProfile), out var transform);

        byte expectedAlpha = (byte)(0.5 * 255);
        Assert.Equal(expectedAlpha, PixelAt(bitmap, transform, 115m, 30).Alpha); // POC bin
        Assert.Equal(expectedAlpha, PixelAt(bitmap, transform, 105m, 30).Alpha); // Value Area bin
        Assert.Equal(expectedAlpha, PixelAt(bitmap, transform, 125m, 30).Alpha); // base bin
    }

    [Fact]
    public void RenderViewport_ChangingThePocColorBetweenCalls_IsPickedUpOnTheNextRender()
    {
        var param = new CoreVolumeProfileParameter { Opacity = 1.0 };
        var setting = SettingFor(param, IndicatorType.VolumeProfile);
        var result = OneSegment(ThreeBins(5000, 10000, 3000), 115m, 120m, 100m);

        using (var first = RenderViewport(result, setting, out var t1))
        {
            AssertColor(Red, PixelAt(first, t1, 115m, 30), "first render");
        }

        param.PocColor = new IndicatorColor(255, 0, 200, 0);
        using var second = RenderViewport(result, setting, out var t2);
        AssertColor(new SKColor(0, 200, 0, 255), PixelAt(second, t2, 115m, 30), "second render, same renderer instance");
    }
}
