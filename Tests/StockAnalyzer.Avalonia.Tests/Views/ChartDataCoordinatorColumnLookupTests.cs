using System;
using System.Linq;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The Data tab describes the column under the pointer, so the column lookup of the coordinator has to return the column that is drawn under the pointer on
/// every chart that is not a time series (Renko, Point and Figure, Three Line Break, Kagi), at every zoom level and scroll position. The pointer position is
/// taken from the coordinate transform of the chart: the x of the centre of a drawn column.
/// </summary>
public class ChartDataCoordinatorColumnLookupTests
{
    private static readonly Rect ControlBounds = new(0, 0, 1200, 700);

    private static (ChartViewModel Vm, ChartDataCoordinator Coordinator) Arrange(ChartType chartType)
    {
        var vm = new ChartViewModel
        {
            Candles = SwingingCandles.Create(),
            EffectiveRenkoBrickSize = 1m,
            EffectivePnfBoxSize = 1m,
            EffectiveKagiReversalAmount = 2m,
            KagiReversalAmount = 2m,
            ChartType = chartType,
        };
        var coordinator = new ChartDataCoordinator();
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        return (vm, coordinator);
    }

    /// <summary>Shows the fraction [from, to] of the converted series (a zoom level and a scroll position) and takes a new snapshot.</summary>
    private static void ShowFraction(ChartViewModel vm, ChartDataCoordinator coordinator, int total, double from, double to)
    {
        if (from <= 0.0 && to >= 1.0)
        {
            return;
        }

        coordinator.Viewport.ForceVisibleRange(total * from, total * to);
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
    }

    private static double CentreX(ChartDataCoordinator coordinator, double logicalCentre) =>
        ((GenericCoordinateTransform)coordinator.Transform).GetXFromIndex(logicalCentre);

    // Renko and Three Line Break draw the column k from the left edge of slot k (centre k + 0.5); Point and Figure draws it around the centre k.
    private static double ColumnCentre(ChartType chartType, int absoluteColumn) =>
        chartType == ChartType.PointAndFigure ? absoluteColumn : absoluteColumn + 0.5;

    [Theory]
    [InlineData(ChartType.Renko, 0.0, 1.0)]
    [InlineData(ChartType.Renko, 0.2, 0.5)]
    [InlineData(ChartType.Renko, 0.6, 0.7)]
    [InlineData(ChartType.PointAndFigure, 0.0, 1.0)]
    [InlineData(ChartType.PointAndFigure, 0.2, 0.5)]
    [InlineData(ChartType.PointAndFigure, 0.6, 0.7)]
    [InlineData(ChartType.ThreeLineBreak, 0.0, 1.0)]
    [InlineData(ChartType.ThreeLineBreak, 0.2, 0.5)]
    [InlineData(ChartType.ThreeLineBreak, 0.6, 0.7)]
    public void GetCandleIndexAt_ReturnsThePositionInTheWindowOfTheColumnUnderThePointer(ChartType chartType, double from, double to)
    {
        var (vm, coordinator) = Arrange(chartType);
        int total = vm.CurrentSnapshot.AllCandles!.Count;
        Assert.True(total > 60, $"{chartType}: too few converted columns for the test: {total}");
        ShowFraction(vm, coordinator, total, from, to);

        var snapshot = vm.CurrentSnapshot;
        int checkedColumns = 0;
        for (int position = 1; position < snapshot.Candles.Count - 1; position++)
        {
            double x = CentreX(coordinator, ColumnCentre(chartType, snapshot.StartIndex + position));
            int index = coordinator.GetCandleIndexAt(x, ControlBounds);

            Assert.True(index == position, $"{chartType} start={snapshot.StartIndex}: the pointer on window position {position} resolved to {index}");
            checkedColumns++;
        }

        Assert.True(checkedColumns > 3);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.2, 0.5)]
    [InlineData(0.6, 0.7)]
    public void GetCandleIndexAt_OnKagi_ReturnsASegmentOfTheColumnUnderThePointer(double from, double to)
    {
        var (vm, coordinator) = Arrange(ChartType.Kagi);
        var columnMap = vm.CurrentSnapshot.KagiColumnMap;
        Assert.NotNull(columnMap);
        int columns = columnMap![^1] + 1;
        Assert.True(columns > 40, $"too few Kagi columns for the test: {columns}");
        ShowFraction(vm, coordinator, columns, from, to);

        int checkedColumns = 0;
        for (int column = (int)Math.Ceiling(coordinator.Viewport.VisibleStartUnit) + 1; column < (int)coordinator.Viewport.VisibleEndUnit - 1; column++)
        {
            int index = coordinator.GetCandleIndexAt(CentreX(coordinator, column), ControlBounds);

            Assert.InRange(index, 0, columnMap.Count - 1);
            Assert.True(columnMap[index] == column, $"Kagi: the pointer on column {column} resolved to a segment of column {columnMap[index]}");
            checkedColumns++;
        }

        Assert.True(checkedColumns > 3);
    }
}
