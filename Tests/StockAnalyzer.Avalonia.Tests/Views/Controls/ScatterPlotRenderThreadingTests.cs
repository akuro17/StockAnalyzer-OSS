using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
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
}
