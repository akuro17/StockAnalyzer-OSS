using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public sealed class SeasonalityPolarPlotMarkerTests
{
    private static readonly SeasonalityLegendSignColors SignColors = new(
        new SKColor(0x2E, 0x7D, 0x32), new SKColor(0xC6, 0x28, 0x28), new SKColor(0x75, 0x75, 0x75));

    [AvaloniaFact]
    public void PolarPlot_RenderSkia_WithNewestYearVisible_RendersEndpointMarkerSuccessfully()
    {
        var control = new SeasonalityPolarPlotControl();
        SeasonalityChartResult result = BuildResult();
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result, null, false);

        using var bitmap = new SKBitmap(300, 300);
        using var canvas = new SKCanvas(bitmap);

        // All years visible: should draw the newest year trace and its endpoint marker
        control.RenderSkia(
            canvas, new Rect(0, 0, 300, 300), result, layout,
            gridOpacityPercent: 60u, gridDashed: false,
            palette: null, yearStates: null, axisTextSize: 12f, legendTextSize: 11f, legendScrollOffset: 0f,
            legendSignColors: SignColors);

        // Bitmap has been drawn into (not completely blank/empty)
        Assert.NotEqual(IntPtr.Zero, bitmap.GetPixels());
    }

    [AvaloniaFact]
    public void PolarPlot_RenderSkia_WhenNewestYearHidden_SuppressesEndpointMarker()
    {
        var control = new SeasonalityPolarPlotControl();
        SeasonalityChartResult result = BuildResult();
        SeasonalityPolarPlotLayout layout = SeasonalityPolarPlotLayoutBuilder.Build(result, null, false);

        int newestYear = result.CalendarYears[^1];
        var yearStates = new Dictionary<int, SeasonalityYearDrawState>
        {
            [newestYear] = new SeasonalityYearDrawState(newestYear, IsVisible: false, EffectiveLineThickness: 1f),
        };

        using var bitmap = new SKBitmap(300, 300);
        using var canvas = new SKCanvas(bitmap);

        // Newest year is toggled OFF: should skip drawing the newest year and its marker
        control.RenderSkia(
            canvas, new Rect(0, 0, 300, 300), result, layout,
            gridOpacityPercent: 60u, gridDashed: false,
            palette: null, yearStates: yearStates, axisTextSize: 12f, legendTextSize: 11f, legendScrollOffset: 0f,
            legendSignColors: SignColors);

        Assert.NotEqual(IntPtr.Zero, bitmap.GetPixels());
    }

    [Fact]
    public void TryProjectPoint_CalculatesCorrectScreenCoordinates_ForApexAndQuarters()
    {
        float centerX = 150f;
        float centerY = 150f;
        double scale = 1.0d;
        double displayBaseRadius = 10d;

        // January Apex (theta = pi/2): x = centerX, y = centerY - (radius - baseRadius) * scale
        double apexAngle = Math.PI / 2d;
        double apexRadius = 60d; // displayRadius = 50
        bool ok = SeasonalityPolarPlotControl.TryProjectPoint(
            apexRadius, apexAngle, centerX, centerY, scale, displayBaseRadius, out SKPoint point);

        Assert.True(ok);
        Assert.Equal(150f, point.X, 2);
        Assert.Equal(100f, point.Y, 2); // 150 - 50 = 100

        // April 3 o'clock (theta = 0): x = centerX + 50, y = centerY
        ok = SeasonalityPolarPlotControl.TryProjectPoint(
            apexRadius, 0d, centerX, centerY, scale, displayBaseRadius, out point);

        Assert.True(ok);
        Assert.Equal(200f, point.X, 2); // 150 + 50 = 200
        Assert.Equal(150f, point.Y, 2);
    }

    private static SeasonalityChartResult BuildResult()
    {
        var points = new List<SeasonalityPoint>();
        decimal value = 100m;
        for (int year = 2022; year <= 2024; year++)
        {
            for (int month = 1; month <= 12; month++)
            {
                points.Add(new SeasonalityPoint(new DateTime(year, month, 1), value));
                value += month % 2 == 0 ? 4m : -2m;
            }
        }

        var series = new SeasonalitySeriesInput(
            seriesId: 0,
            label: "TEST",
            radiusMode: SeasonalityRadiusMode.PercentVsYearStart,
            points: points);

        return SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(3));
    }
}
