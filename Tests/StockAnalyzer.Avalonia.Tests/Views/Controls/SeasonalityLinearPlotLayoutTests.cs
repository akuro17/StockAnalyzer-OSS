using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public sealed class SeasonalityLinearPlotLayoutTests
{
    [Fact]
    public void Build_MonthFractions_AreYearProgressFromZeroAscendingBelowOne()
    {
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(BuildResult());

        Assert.Equal(SeasonalitySharedAxisFormatting.MonthsPerYear, layout.MonthFractions.Length);
        Assert.Equal(0d, layout.MonthFractions[0], 12);
        for (int index = 1; index < layout.MonthFractions.Length; index++)
        {
            Assert.True(layout.MonthFractions[index] > layout.MonthFractions[index - 1]);
        }

        Assert.All(layout.MonthFractions, u => Assert.InRange(u, 0d, 0.999999d));

        // 1 April is canonical index 91 of 366 (day 90, shifted +1 from March on).
        Assert.Equal(91d / 366d, layout.MonthFractions[3], 12);
    }

    [Fact]
    public void Build_MonthLabels_ComeFromTheSharedFormatter()
    {
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(BuildResult());

        Assert.Equal(SeasonalitySharedAxisFormatting.BuildMonthLabels(), layout.MonthLabels);
    }

    [Fact]
    public void Build_RhoRange_MatchesThePolarRadiusDerivationAndBracketsZero()
    {
        SeasonalityChartResult result = BuildResult();

        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(result);

        double expectedMin = (result.MinRadius - result.BaseRadius) / result.ScaleFactor;
        double expectedMax = (result.MaxRadius - result.BaseRadius) / result.ScaleFactor;
        Assert.Equal(expectedMin, layout.RhoMin, 9);
        Assert.Equal(expectedMax, layout.RhoMax, 9);
        Assert.True(layout.RhoMin <= 0d && layout.RhoMax >= 0d);
        Assert.True(layout.HasData);
    }

    [Fact]
    public void Build_RhoGrid_IsAscendingBoundedIncludesZeroAndCarriesPercentLabels()
    {
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(BuildResult());

        Assert.NotEmpty(layout.GridRhoValues);
        Assert.Equal(layout.GridRhoValues.Length, layout.GridRhoLabels.Length);
        Assert.True(layout.GridRhoValues.Length <= SeasonalitySharedAxisFormatting.MaximumGridCircleCount);
        for (int index = 1; index < layout.GridRhoValues.Length; index++)
        {
            Assert.True(layout.GridRhoValues[index] > layout.GridRhoValues[index - 1]);
        }

        Assert.Contains(0d, layout.GridRhoValues);
        Assert.All(layout.GridRhoValues, rho => Assert.InRange(rho, layout.RhoMin - 1e-9, layout.RhoMax + 1e-9));
        Assert.All(layout.GridRhoLabels, label => Assert.EndsWith("%", label));
    }

    [Fact]
    public void Build_RhoGrid_HonoursManualStep()
    {
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(BuildResult(), manualRhoStep: 0.05m);

        for (int index = 1; index < layout.GridRhoValues.Length; index++)
        {
            Assert.Equal(0.05d, layout.GridRhoValues[index] - layout.GridRhoValues[index - 1], 9);
        }
    }

    [Fact]
    public void Build_Legend_IsOneRowPerCalendarYearNewestFirst()
    {
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(BuildResult());

        Assert.Equal(new[] { 2023, 2022, 2021 }, layout.LegendEntries.Select(e => e.CalendarYear).ToArray());
        Assert.Equal(new[] { "2023", "2022", "2021" }, layout.LegendEntries.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void Build_Legend_CarriesTheSignedAnnualReturnLabelForEachYear()
    {
        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(BuildResult());

        Assert.All(
            layout.LegendEntries,
            e => Assert.Matches(@"^[+\-]?\d+(\.\d)?%$", e.AnnualReturnLabel));
    }

    [Fact]
    public void Build_EmptyResult_YieldsNoGridButKeepsTheMonthAxis()
    {
        var empty = new SeasonalityChartResult(
            new SeasonalityChartParameters(5),
            Array.Empty<SeasonalityYearTrace>(),
            baseRadius: 0d,
            scaleFactor: 0d,
            minRadius: 0d,
            maxRadius: 0d,
            calendarYears: Array.Empty<int>());

        SeasonalityLinearPlotLayout layout = SeasonalityLinearPlotLayoutBuilder.Build(empty);

        Assert.False(layout.HasData);
        Assert.Empty(layout.GridRhoValues);
        Assert.Empty(layout.GridRhoLabels);
        Assert.Empty(layout.LegendEntries);
        Assert.Equal(SeasonalitySharedAxisFormatting.MonthsPerYear, layout.MonthLabels.Length);
    }

    [Fact]
    public void TryProjectLinearPoint_YearStartMapsToLeftEdgeAndZeroRhoToTheBaseline()
    {
        // rhoMin -0.1, span mapped so yScale = 1000 -> rho 0 sits 100px above the bottom.
        bool projected = SeasonalityLinearPlotControl.TryProjectLinearPoint(
            yearFraction: 0d,
            rho: 0d,
            plotLeft: 40f,
            plotBottom: 300f,
            plotWidth: 400f,
            rhoMin: -0.1d,
            yScale: 1000d,
            out SKPoint point);

        Assert.True(projected);
        Assert.Equal(40f, point.X, 3);
        Assert.Equal(200f, point.Y, 3);
    }

    [Fact]
    public void TryProjectLinearPoint_YearEndMapsToTheRightEdgeAndRejectsNonFinite()
    {
        SeasonalityLinearPlotControl.TryProjectLinearPoint(
            yearFraction: 1d, rho: 0.2d, plotLeft: 0f, plotBottom: 100f, plotWidth: 500f, rhoMin: 0d, yScale: 100d,
            out SKPoint atRightEdge);
        Assert.Equal(500f, atRightEdge.X, 3);

        Assert.False(SeasonalityLinearPlotControl.TryProjectLinearPoint(
            double.NaN, 0d, 0f, 0f, 100f, 0d, 1d, out _));
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
