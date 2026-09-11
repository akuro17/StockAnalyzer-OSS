using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>
/// Regression guard for the Seasonality plot freeze: <see cref="SeasonalityPolarPlotControl"/> and
/// <see cref="SeasonalityLinearPlotControl"/> draw through an <see cref="Avalonia.Rendering.SceneGraph.ICustomDrawOperation"/>,
/// whose Render runs on the render thread. Reading a StyledProperty there (YearPalette / AxisFontSize /
/// LegendFontSize) calls AvaloniaObject.VerifyAccess() and throws "Call from invalid thread", which
/// aborted every paint of a populated chart and wedged the compositor. RenderSkia and everything it
/// calls must consume only the snapshot values passed from the UI-thread Render.
/// </summary>
public sealed class SeasonalityPlotControlRenderThreadingTests
{
    /// <summary>Non-unset Data-tab sign colours so the legend exercises the sign-coloured annual-return draw path off the UI thread.</summary>
    private static readonly SeasonalityLegendSignColors SignColors = new(
        new SKColor(0x2E, 0x7D, 0x32), new SKColor(0xC6, 0x28, 0x28), new SKColor(0x75, 0x75, 0x75));

    [AvaloniaFact]
    public async Task PolarPlot_RenderSkia_OffTheUiThread_DoesNotReadStyledProperties()
    {
        // Styled properties carry non-default values and are set on the UI thread. If RenderSkia reads
        // any of them, VerifyAccess() throws on the Task.Run thread below and the assertion trips.
        var control = new SeasonalityPolarPlotControl
        {
            YearPalette = new[] { Colors.Red, Colors.Lime, Colors.Blue },
            AxisFontSize = 13d,
            LegendFontSize = 11d,
        };
        SeasonalityChartResult result = BuildResult();
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result, null, false);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(220, 220);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas, new Rect(0, 0, 220, 220), result, layout,
                    gridOpacityPercent: 60u, gridDashed: false,
                    palette: null, yearStates: null, axisTextSize: 13f, legendTextSize: 11f, legendScrollOffset: 0f,
                    legendSignColors: SignColors);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public async Task LinearPlot_RenderSkia_OffTheUiThread_DoesNotReadStyledProperties()
    {
        var control = new SeasonalityLinearPlotControl
        {
            YearPalette = new[] { Colors.Red, Colors.Lime, Colors.Blue },
            AxisFontSize = 13d,
            LegendFontSize = 11d,
        };
        SeasonalityChartResult result = BuildResult();
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(result, null);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(360, 220);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas, new Rect(0, 0, 360, 220), result, layout,
                    gridOpacityPercent: 60u, gridDashed: false,
                    palette: null, yearStates: null, axisTextSize: 13f, legendTextSize: 11f, legendScrollOffset: 0f,
                    legendSignColors: SignColors);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public async Task PolarPlot_RenderSkia_WithAnOverflowingScrolledLegendAndOffYears_DoesNotThrow()
    {
        // Decision D7: many more legend rows than fit -> the legend is clipped to a scrollable band.
        // Decision D6: some years OFF -> their legend rows stay, drawn dimmed. Both paths must render
        // without touching a StyledProperty or walking off the array.
        var control = new SeasonalityPolarPlotControl { AxisFontSize = 13d, LegendFontSize = 11d };
        SeasonalityChartResult result = BuildManyYearResult(24);
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result, null, false);
        IReadOnlyDictionary<int, SeasonalityYearDrawState> offEveryOther = BuildAlternatingYearStates(result);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(220, 180);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas, new Rect(0, 0, 220, 180), result, layout,
                    gridOpacityPercent: 60u, gridDashed: false,
                    palette: null, yearStates: offEveryOther, axisTextSize: 13f, legendTextSize: 11f,
                    legendScrollOffset: 5000f, // absurd offset: the renderer must clamp it
                    legendSignColors: SignColors);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public async Task LinearPlot_RenderSkia_WithAnOverflowingScrolledLegendAndOffYears_DoesNotThrow()
    {
        var control = new SeasonalityLinearPlotControl { AxisFontSize = 13d, LegendFontSize = 11d };
        SeasonalityChartResult result = BuildManyYearResult(24);
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(result, null);
        IReadOnlyDictionary<int, SeasonalityYearDrawState> offEveryOther = BuildAlternatingYearStates(result);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(360, 180);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas, new Rect(0, 0, 360, 180), result, layout,
                    gridOpacityPercent: 60u, gridDashed: false,
                    palette: null, yearStates: offEveryOther, axisTextSize: 13f, legendTextSize: 11f,
                    legendScrollOffset: 5000f,
                    legendSignColors: SignColors);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        Assert.Null(thrown);
    }

    [AvaloniaFact]
    public async Task PolarPlot_RenderSkia_InAViewportTooSmallForMonthLabels_DoesNotThrow()
    {
        // Decision C3: below the size where the twelve month labels can clear the data area the frame
        // is skipped rather than drawn at a degenerate radius. That early-out must stay exception-safe.
        var control = new SeasonalityPolarPlotControl { AxisFontSize = 16d, LegendFontSize = 11d };
        SeasonalityChartResult result = BuildResult();
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result, null, false);

        Exception? thrown = await Task.Run(() =>
        {
            try
            {
                using var bitmap = new SKBitmap(38, 38);
                using var canvas = new SKCanvas(bitmap);
                control.RenderSkia(
                    canvas, new Rect(0, 0, 38, 38), result, layout,
                    gridOpacityPercent: 60u, gridDashed: false,
                    palette: null, yearStates: null, axisTextSize: 16f, legendTextSize: 11f, legendScrollOffset: 0f,
                    legendSignColors: SignColors);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        Assert.Null(thrown);
    }

    private static SeasonalityChartResult BuildManyYearResult(int years)
    {
        var points = new List<SeasonalityPoint>();
        decimal value = 100m;
        int firstYear = 2024 - years + 1;
        for (int year = firstYear; year <= 2024; year++)
        {
            for (int month = 1; month <= 12; month++)
            {
                points.Add(new SeasonalityPoint(new DateTime(year, month, 1), value));
                value += month % 2 == 0 ? 3m : -1m;
            }
        }

        var series = new SeasonalitySeriesInput(
            seriesId: 0,
            label: "TEST",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: points);

        return SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters((uint)years));
    }

    private static IReadOnlyDictionary<int, SeasonalityYearDrawState> BuildAlternatingYearStates(SeasonalityChartResult result)
    {
        var map = new Dictionary<int, SeasonalityYearDrawState>(result.CalendarYears.Count);
        for (int index = 0; index < result.CalendarYears.Count; index++)
        {
            int year = result.CalendarYears[index];
            map[year] = new SeasonalityYearDrawState(year, index % 2 == 0, 1.5f);
        }

        return map;
    }

    private static SeasonalityChartResult BuildResult()
    {
        var series = new SeasonalitySeriesInput(
            seriesId: 0,
            label: "TEST",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: MonthlyPoints());

        return SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3));
    }

    private static IReadOnlyList<SeasonalityPoint> MonthlyPoints()
    {
        var points = new List<SeasonalityPoint>();
        decimal value = 100m;
        for (int year = 2021; year <= 2023; year++)
        {
            for (int month = 1; month <= 12; month++)
            {
                points.Add(new SeasonalityPoint(new DateTime(year, month, 1), value));
                value += month % 2 == 0 ? 3m : -1m;
            }
        }

        return points;
    }
}
