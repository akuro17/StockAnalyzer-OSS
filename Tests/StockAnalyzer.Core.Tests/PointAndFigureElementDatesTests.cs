using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// ElementDates of Point and Figure columns: Start = date of the candle that created the column,
/// End = date of the candle that last moved its far end (Start when never extended).
/// The converter and the calculator are unified to the same SSoT calculation engine, so both produce identical results.
/// </summary>
public class PointAndFigureElementDatesTests
{
    private const decimal BoxSize = 1m;
    private const int ReversalAmount = 3;

    private static DateTime Day(int offset) => new DateTime(2025, 1, 1).AddDays(offset);

    // Box 1, reversal 3 boxes. Day 0 sets the baseline.
    //  d1 up column starts (100..103)   d2 extends it to 105
    //  d3 reversal to a down column      d4 extends it to 99
    //  d5 reversal to an up column
    private static CoreCandleData[] Series() => new[]
    {
        new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
        new CoreCandleData(Day(1), 100m, 103m, 99m, 102m, 1000),
        new CoreCandleData(Day(2), 103m, 105m, 103m, 105m, 1000),
        new CoreCandleData(Day(3), 105m, 105m, 101m, 101m, 1000),
        new CoreCandleData(Day(4), 101m, 100m, 99m, 99m, 1000),
        new CoreCandleData(Day(5), 99m, 103m, 99.5m, 103m, 1000),
    };

    [Fact]
    public void Converter_ElementDates_StartIsCreationAndEndIsLastExtension()
    {
        var columns = new List<CoreCandleData>();
        var dates = new List<ElementDates>();
        PointAndFigureConverter.Convert(Series(), BoxSize, ReversalAmount, columns, ChartRoundingMode.None, dates);

        Assert.Equal(3, columns.Count);
        Assert.Equal(new[]
        {
            new ElementDates(Day(1), Day(2)),
            new ElementDates(Day(3), Day(4)),
            new ElementDates(Day(5), Day(5)), // never extended: End == Start
        }, dates);
        for (int i = 0; i < columns.Count; i++)
            Assert.Equal(columns[i].Timestamp, dates[i].End); // existing Timestamp semantic: last update
    }

    [Fact]
    public void Converter_WithoutElementDatesBuffer_GivesTheSameColumns()
    {
        var withBuffer = new List<CoreCandleData>();
        var without = new List<CoreCandleData>();
        PointAndFigureConverter.Convert(Series(), BoxSize, ReversalAmount, withBuffer, ChartRoundingMode.None, new List<ElementDates>());
        PointAndFigureConverter.Convert(Series(), BoxSize, ReversalAmount, without, ChartRoundingMode.None);

        Assert.Equal(without, withBuffer);
    }

    [Fact]
    public void Converter_ReusedBuffer_IsClearedOnEveryCall()
    {
        var columns = new List<CoreCandleData>();
        var dates = new List<ElementDates> { new(Day(9), Day(9)) };
        PointAndFigureConverter.Convert(Series(), BoxSize, ReversalAmount, columns, ChartRoundingMode.None, dates);
        PointAndFigureConverter.Convert(Series(), BoxSize, ReversalAmount, columns, ChartRoundingMode.None, dates);

        Assert.Equal(columns.Count, dates.Count);
        Assert.Equal(3, dates.Count);
    }

    [Fact]
    public void Calculator_ElementDates_StartIsColumnCreationAndEndIsLastFarEndMove()
    {
        var source = Series().Select(c =>
            new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();
        var result = PointAndFigureCalculator.Calculate(source, new PointAndFigureParameters(BoxSize, ReversalAmount));

        Assert.Equal(3, result.Count);
        Assert.Equal(new[]
        {
            new ElementDates(Day(1), Day(2)),
            new ElementDates(Day(3), Day(4)),
            new ElementDates(Day(5), Day(5)),
        }, result.ElementDates.ToArray());
        Assert.Equal(result.Count, result.ElementDates.Length);
        Assert.Equal(Day(2), result.Timestamps.Span[0]); // Unified SSoT Timestamp semantic: last update (End)
        Assert.Equal(Day(4), result.Timestamps.Span[1]);
        Assert.Equal(Day(5), result.Timestamps.Span[2]);
    }
}
