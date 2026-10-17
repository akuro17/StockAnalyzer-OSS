using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>
/// Regression guard for the Scatter Analysis plot and Uniform Heatmap freeze/flicker:
/// <see cref="ScatterPlotControl"/> and <see cref="UniformHeatmapControl"/> draw through
/// <see cref="Avalonia.Rendering.SceneGraph.ICustomDrawOperation"/>, whose Render runs on the compositor
/// render thread. Reading any Avalonia StyledProperty off the UI thread invokes AvaloniaObject.VerifyAccess()
/// and throws "Call from invalid thread", which aborted every paint pass and caused 60+ FPS window flashing
/// and complete UI thread starvation. RenderSkia must consume only the snapshotted values.
/// </summary>
public sealed class ScatterPlotRenderThreadingTests
{
    [AvaloniaFact]
    public async Task ScatterPlot_RenderSkia_OffTheUiThread_DoesNotReadStyledProperties()
    {
        var points = new List<ScatterPoint>
        {
            new(1.0, 2.0, 1000L, 0xFF00FF00, 3.5f),
            new(2.0, 4.0, 2000L, 0xFF00FF00, 3.5f),
            new(3.0, 6.0, 3000L, 0xFF00FF00, 3.5f),
            new(4.0, 8.0, 4000L, 0xFF00FF00, 3.5f),
            new(5.0, 10.0, 5000L, 0xFF00FF00, 3.5f)
        };
        var dataset = new ScatterDataset("Test Dataset", "X Axis", "Y Axis", points, new[] { "P1", "P2", "P3", "P4", "P5" });
        var analysis = ScatterAnalysisEngine.Analyze(points.ToArray());

        // Styled properties are set on the UI thread.
        // If RenderSkia touches any of them, VerifyAccess() throws immediately on the background thread.
        var control = new ScatterPlotControl
        {
            Dataset = dataset,
            AnalysisResult = analysis,
            ShowRegressionLine = true,
            ShowConfidenceBand = true,
            ShowPredictionBand = true,
            SuppressOutliers = true,
            XScale = PriceScaleType.Linear,
            YScale = PriceScaleType.Linear,
            HoveredPointIndex = 0,
            SelectedPointIndex = 1
        };

        var transform = new ScatterCoordinateTransform(
            0.0, 6.0, 0.0, 12.0,
            55.0, 20.0, 500.0, 400.0, 600.0, 500.0);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(600, 500);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas,
                    new Rect(0, 0, 600, 500),
                    dataset,
                    analysis,
                    showRegressionLine: true,
                    showConfidenceBand: true,
                    showPredictionBand: true,
                    suppressOutliers: true,
                    xScale: PriceScaleType.Linear,
                    yScale: PriceScaleType.Linear,
                    hoveredPointIndex: 0,
                    selectedPointIndex: 1,
                    cursorPos: new global::Avalonia.Point(100, 100),
                    transform: transform);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public async Task UniformHeatmap_RenderSkia_OffTheUiThread_DoesNotReadStyledProperties()
    {
        var items = new List<UniformHeatmapItem>
        {
            new("AAPL", 0.035, "Apple Inc."),
            new("MSFT", -0.015, "Microsoft Corp."),
            new("NVDA", 0.082, "NVIDIA Corp."),
            new("GOOGL", double.NaN, "Alphabet Inc.")
        };

        // Styled properties are set on the UI thread.
        var control = new UniformHeatmapControl
        {
            Items = items,
            ScaleThreshold = 0.05,
            SelectedTicker = "AAPL",
            HoveredTicker = "MSFT",
            ColorScheme = HeatmapColorScheme.Classic
        };

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(800, 600);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas,
                    new Rect(0, 0, 800, 600),
                    items,
                    scaleThreshold: 0.05,
                    selectedTicker: "AAPL",
                    hoveredTicker: "MSFT",
                    colorScheme: HeatmapColorScheme.Classic);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public void UniformHeatmap_MeasureOverride_WithInfinityWidth_ReturnsFiniteSizeWhenEmpty()
    {
        var control = new UniformHeatmapControl();
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.True(double.IsFinite(control.DesiredSize.Width));
        Assert.True(double.IsFinite(control.DesiredSize.Height));
        Assert.Equal(0, control.DesiredSize.Width);
        Assert.Equal(0, control.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void ScatterPlot_ThemeChange_UpdatesBackgroundPaintColor()
    {
        var control = new ScatterPlotControl();
        var customTheme = ThemeColors.Dark with
        {
            ChartBackground = IndicatorColor.FromUInt(0xFF223344)
        };

        control.SetTheme(customTheme);

        Assert.Equal(new SKColor(0x22, 0x33, 0x44), control.BackgroundColor);
        Assert.Equal(new SKColor(0x22, 0x33, 0x44), control.PlotBackgroundColor);
    }

    [Fact]
    public void ScatterPlot_LayoutConstants_MatchStandardizedSpecification()
    {
        Assert.Equal(20.0f, ScatterPlotControl.MarginLeft);
        Assert.Equal(60.0f, ScatterPlotControl.MarginRight);
        Assert.Equal(20.0f, ScatterPlotControl.MarginTop);
        Assert.Equal(42.0f, ScatterPlotControl.MarginBottom);
        Assert.Equal(0.06, ScatterPlotControl.DataRangePaddingFraction);
    }

    [Fact]
    public void ScatterPlot_ResolveViewportDataBounds_LinearScale_AddsSixPercentPadding()
    {
        var points = new List<ScatterPoint>
        {
            new(0.0, 0.0, 1000L, 0xFF00FF00, 3.5f),
            new(100.0, 100.0, 2000L, 0xFF00FF00, 3.5f)
        };
        var dataset = new ScatterDataset("Test", "X", "Y", points, new[] { "P1", "P2" });
        var analysis = ScatterAnalysisEngine.Analyze(points.ToArray());

        var (minX, maxX, minY, maxY) = ScatterPlotControl.ResolveViewportDataBounds(
            dataset,
            analysis,
            suppressOutliers: false,
            PriceScaleType.Linear,
            PriceScaleType.Linear);

        // Raw span = 100.0, 6% padding = 6.0
        Assert.Equal(-6.0, minX, 4);
        Assert.Equal(106.0, maxX, 4);
        Assert.Equal(-6.0, minY, 4);
        Assert.Equal(106.0, maxY, 4);

        // Clearance verification: on a 500x400 canvas, points at (0,0) and (100,100)
        // must project with >= 7.25px clearance from plot boundaries (hover ring radius)
        float vpW = 500.0f - ScatterPlotControl.MarginLeft - ScatterPlotControl.MarginRight;
        float vpH = 400.0f - ScatterPlotControl.MarginTop - ScatterPlotControl.MarginBottom;
        var transform = new ScatterCoordinateTransform(
            minX, maxX, minY, maxY,
            ScatterPlotControl.MarginLeft, ScatterPlotControl.MarginTop,
            vpW, vpH, 500.0, 400.0);

        float sx0 = (float)transform.XToScreen(0.0);
        float sx100 = (float)transform.XToScreen(100.0);
        float sy0 = (float)transform.YToScreen(0.0);
        float sy100 = (float)transform.YToScreen(100.0);

        float plotLeft = ScatterPlotControl.MarginLeft;
        float plotRight = ScatterPlotControl.MarginLeft + vpW;
        float plotTop = ScatterPlotControl.MarginTop;
        float plotBottom = ScatterPlotControl.MarginTop + vpH;

        Assert.True(sx0 - plotLeft >= 7.25f, $"Left clearance should be >= 7.25px, got {sx0 - plotLeft}");
        Assert.True(plotRight - sx100 >= 7.25f, $"Right clearance should be >= 7.25px, got {plotRight - sx100}");
        Assert.True(plotBottom - sy0 >= 7.25f, $"Bottom clearance should be >= 7.25px, got {plotBottom - sy0}");
        Assert.True(sy100 - plotTop >= 7.25f, $"Top clearance should be >= 7.25px, got {sy100 - plotTop}");
    }

    [Fact]
    public void ScatterPlot_ResolveViewportDataBounds_LogScale_PositivePoints_AddsLogDecadePadding()
    {
        var points = new List<ScatterPoint>
        {
            new(1.0, 10.0, 1000L, 0xFF00FF00, 3.5f),
            new(100.0, 1000.0, 2000L, 0xFF00FF00, 3.5f)
        };
        var dataset = new ScatterDataset("Test", "X", "Y", points, new[] { "P1", "P2" });

        var (minX, maxX, minY, maxY) = ScatterPlotControl.ResolveViewportDataBounds(
            dataset,
            null,
            suppressOutliers: false,
            PriceScaleType.Log,
            PriceScaleType.Log);

        // logSpanX = 2.0, 6% = 0.12 decade padding -> min = 10^-0.12 ~ 0.7586, max = 10^2.12 ~ 131.8257
        Assert.True(minX < 1.0, "minX must be < 1.0");
        Assert.True(maxX > 100.0, "maxX must be > 100.0");
        Assert.True(minY < 10.0, "minY must be < 10.0");
        Assert.True(maxY > 1000.0, "maxY must be > 1000.0");
    }

    [Fact]
    public void ScatterPlot_ResolveViewportDataBounds_LogScale_WithNonPositivePoints_FiltersNonPositiveGracefully()
    {
        var points = new List<ScatterPoint>
        {
            new(-10.0, 0.0, 1000L, 0xFF00FF00, 3.5f),
            new(0.0, -5.0, 2000L, 0xFF00FF00, 3.5f),
            new(10.0, 20.0, 3000L, 0xFF00FF00, 3.5f),
            new(100.0, 200.0, 4000L, 0xFF00FF00, 3.5f)
        };
        var dataset = new ScatterDataset("Test", "X", "Y", points, new[] { "P1", "P2", "P3", "P4" });

        var (minX, maxX, minY, maxY) = ScatterPlotControl.ResolveViewportDataBounds(
            dataset,
            null,
            suppressOutliers: false,
            PriceScaleType.Log,
            PriceScaleType.Log);

        // Log bounds must strictly be positive without collapsing to 1e-12
        Assert.True(minX > 0.1, $"minX must be > 0.1, got {minX}");
        Assert.True(minX < 10.0, $"minX must be < 10.0, got {minX}");
        Assert.True(maxX > 100.0, $"maxX must be > 100.0, got {maxX}");
        Assert.True(minY > 0.1, $"minY must be > 0.1, got {minY}");
        Assert.True(maxY > 200.0, $"maxY must be > 200.0, got {maxY}");
    }

    [AvaloniaFact]
    public async Task ScatterPlot_RenderSkia_RendersAllComponentsWithoutThrowing()
    {
        var points = new List<ScatterPoint>
        {
            new(5.0, 15.0, 1000L, 0xFF00FF00, 3.5f),
            new(25.0, 35.0, 2000L, 0xFF00FF00, 3.5f),
            new(50.0, 75.0, 3000L, 0xFF00FF00, 3.5f)
        };
        var dataset = new ScatterDataset("Test Dataset", "X Axis Long Label", "Y Axis Long Label", points, new[] { "P1", "P2", "P3" });
        var analysis = ScatterAnalysisEngine.Analyze(points.ToArray());

        var control = new ScatterPlotControl
        {
            Dataset = dataset,
            AnalysisResult = analysis,
            ShowRegressionLine = true,
            ShowConfidenceBand = true,
            ShowPredictionBand = true,
            SuppressOutliers = false,
            XScale = PriceScaleType.Linear,
            YScale = PriceScaleType.Linear,
            HoveredPointIndex = 1,
            SelectedPointIndex = 0
        };

        var (minX, maxX, minY, maxY) = ScatterPlotControl.ResolveViewportDataBounds(
            dataset, analysis, false, PriceScaleType.Linear, PriceScaleType.Linear);

        float vpW = 600.0f - ScatterPlotControl.MarginLeft - ScatterPlotControl.MarginRight;
        float vpH = 500.0f - ScatterPlotControl.MarginTop - ScatterPlotControl.MarginBottom;
        var transform = new ScatterCoordinateTransform(
            minX, maxX, minY, maxY,
            ScatterPlotControl.MarginLeft, ScatterPlotControl.MarginTop,
            vpW, vpH, 600.0, 500.0);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(600, 500);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas,
                    new Rect(0, 0, 600, 500),
                    dataset,
                    analysis,
                    showRegressionLine: true,
                    showConfidenceBand: true,
                    showPredictionBand: true,
                    suppressOutliers: false,
                    xScale: PriceScaleType.Linear,
                    yScale: PriceScaleType.Linear,
                    hoveredPointIndex: 1,
                    selectedPointIndex: 0,
                    cursorPos: new global::Avalonia.Point(200, 200),
                    transform: transform);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public void ScatterPlot_TooltipFontSize_ReflectsFontSettingsManagerAndDynamicUpdates()
    {
        var control = new ScatterPlotControl();
        // Default without font settings manager returns FontDefaults.Tooltip
        Assert.Equal(FontDefaults.Tooltip, control.CurrentTooltipFontSize);

        var fontManager = new TestFontSettingsManager();
        fontManager.SetTooltipFontSize(18.0);

        control.SetFontSettingsManager(fontManager);
        Assert.Equal(18.0, control.CurrentTooltipFontSize);

        // Update font size dynamically -> triggers PropertyChanged -> visual invalidate -> CurrentTooltipFontSize updated
        fontManager.SetTooltipFontSize(22.0);
        Assert.Equal(22.0, control.CurrentTooltipFontSize);

        // Detaching font manager resets back to default or resource fallback
        control.SetFontSettingsManager(null);
        Assert.Equal(FontDefaults.Tooltip, control.CurrentTooltipFontSize);
    }

    [AvaloniaFact]
    public async Task ScatterPlot_RenderSkia_WithCustomTooltipFontSize_RendersTooltipOnBackgroundThreadWithoutThrowing()
    {
        var points = new List<ScatterPoint>
        {
            new(10.0, 20.0, 1000L, 0xFF00FF00, 3.5f),
            new(30.0, 40.0, 2000L, 0xFF00FF00, 3.5f)
        };
        var dataset = new ScatterDataset("Test Dataset", "X Axis", "Y Axis", points, new[] { "P1", "P2" });
        var analysis = ScatterAnalysisEngine.Analyze(points.ToArray());

        var control = new ScatterPlotControl
        {
            Dataset = dataset,
            AnalysisResult = analysis,
            HoveredPointIndex = 0
        };

        var transform = new ScatterCoordinateTransform(
            0.0, 50.0, 0.0, 50.0,
            20.0, 20.0, 520.0, 438.0, 600.0, 500.0);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(600, 500);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas,
                    new Rect(0, 0, 600, 500),
                    dataset,
                    analysis,
                    showRegressionLine: false,
                    showConfidenceBand: false,
                    showPredictionBand: false,
                    suppressOutliers: false,
                    xScale: PriceScaleType.Linear,
                    yScale: PriceScaleType.Linear,
                    hoveredPointIndex: 0,
                    selectedPointIndex: null,
                    cursorPos: new global::Avalonia.Point(100, 100),
                    transform: transform,
                    tooltipFontSize: 24.0f);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public void ScatterPlot_TooltipFontSize_WhenFontSettingsManagerReturnsNonFiniteOrNegative_FallsBackToDefault()
    {
        var control = new ScatterPlotControl();
        var fontManager = new TestFontSettingsManager();
        control.SetFontSettingsManager(fontManager);

        fontManager.SetTooltipFontSize(double.PositiveInfinity);
        Assert.Equal(FontDefaults.Tooltip, control.CurrentTooltipFontSize);

        fontManager.SetTooltipFontSize(double.NaN);
        Assert.Equal(FontDefaults.Tooltip, control.CurrentTooltipFontSize);

        fontManager.SetTooltipFontSize(-5.0);
        Assert.Equal(FontDefaults.Tooltip, control.CurrentTooltipFontSize);

        fontManager.SetTooltipFontSize(0.0);
        Assert.Equal(FontDefaults.Tooltip, control.CurrentTooltipFontSize);

        fontManager.SetTooltipFontSize(16.5);
        Assert.Equal(16.5, control.CurrentTooltipFontSize);
    }

    [AvaloniaFact]
    public async Task ScatterPlot_RenderSkia_WithNonFiniteTooltipFontSize_RendersWithoutThrowing()
    {
        var control = new ScatterPlotControl();
        var transform = new ScatterCoordinateTransform(
            0.0, 50.0, 0.0, 50.0,
            20.0, 20.0, 520.0, 438.0, 600.0, 500.0);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(600, 500);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas,
                    new Rect(0, 0, 600, 500),
                    dataset: null,
                    analysis: null,
                    showRegressionLine: false,
                    showConfidenceBand: false,
                    showPredictionBand: false,
                    suppressOutliers: false,
                    xScale: PriceScaleType.Linear,
                    yScale: PriceScaleType.Linear,
                    hoveredPointIndex: null,
                    selectedPointIndex: null,
                    cursorPos: null,
                    transform: transform,
                    tooltipFontSize: float.PositiveInfinity);

                control.RenderSkia(
                    canvas,
                    new Rect(0, 0, 600, 500),
                    dataset: null,
                    analysis: null,
                    showRegressionLine: false,
                    showConfidenceBand: false,
                    showPredictionBand: false,
                    suppressOutliers: false,
                    xScale: PriceScaleType.Linear,
                    yScale: PriceScaleType.Linear,
                    hoveredPointIndex: null,
                    selectedPointIndex: null,
                    cursorPos: null,
                    transform: transform,
                    tooltipFontSize: float.NaN);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        Assert.Null(thrown);
    }

    private sealed class TestFontSettingsManager : IFontSettingsManager
    {
        private double _tooltipFontSize = FontDefaults.Tooltip;

        public double BaseFontSize => 13.0;
        public double TitleFontSize => 16.0;
        public double DetailFontSize => 12.0;
        public double HelperFontSize => 11.0;
        public double TooltipFontSize
        {
            get => _tooltipFontSize;
            set
            {
                if (Math.Abs(_tooltipFontSize - value) > 0.001)
                {
                    _tooltipFontSize = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TooltipFontSize)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetBaseFontSize(double size) { }
        public void SetTitleFontSize(double size) { }
        public void SetDetailFontSize(double size) { }
        public void SetHelperFontSize(double size) { }
        public void SetTooltipFontSize(double size) => TooltipFontSize = size;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
    }
}

