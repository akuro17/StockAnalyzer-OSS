using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="TrendDates.Describe"/>: reversal date (creation of the first element of the run), update date, and the calendar days between them.
/// The vectors are the ones of the implementation plan (dates are 2026-01-dd unless written).
/// </summary>
public class TrendDatesTests
{
    private static DateTime Jan(int day) => new(2026, 1, day);

    private static CoreCandleData Up(DateTime date) => new(date, 10m, 12m, 10m, 12m, 0);

    private static CoreCandleData Down(DateTime date) => new(date, 12m, 12m, 10m, 10m, 0);

    private static CoreCandleData Flat(DateTime date) => new(date, 10m, 10m, 10m, 10m, 0);

    private static void AssertSpan(TrendDateSpan span, DateTime? reversal, DateTime? update, int? days)
    {
        Assert.Equal(reversal, span.ReversalDate);
        Assert.Equal(update, span.UpdateDate);
        Assert.Equal(days, span.Days);
    }

    // ── Renko / Three Line Break: the date of an element is its Timestamp (never extended) ──

    private static CoreCandleData[] RunSeries() => new[]
    {
        Up(Jan(5)), Up(Jan(6)), Down(Jan(9)), Down(Jan(10)), Down(Jan(10)), Up(Jan(15)),
    };

    public static IEnumerable<object[]> RunExpectations() => new[]
    {
        new object[] { 0, null, Jan(5), null },
        new object[] { 1, null, Jan(6), null },
        new object[] { 2, Jan(9), Jan(9), 0 },
        new object[] { 3, Jan(9), Jan(10), 1 },
        new object[] { 4, Jan(9), Jan(10), 1 },
        new object[] { 5, Jan(15), Jan(15), 0 },
    };

    [Theory]
    [MemberData(nameof(RunExpectations))]
    public void Renko_ReversalIsTheFirstElementOfTheRun(int index, DateTime? reversal, DateTime? update, int? days)
    {
        AssertSpan(TrendDates.Describe(RunSeries(), null, index, ChartType.Renko), reversal, update, days);
    }

    [Theory]
    [MemberData(nameof(RunExpectations))]
    public void ThreeLineBreak_ReversalIsTheFirstElementOfTheRun(int index, DateTime? reversal, DateTime? update, int? days)
    {
        AssertSpan(TrendDates.Describe(RunSeries(), null, index, ChartType.ThreeLineBreak), reversal, update, days);
    }

    [Fact]
    public void Renko_DatesAreIgnored_TheTimestampOfTheElementIsUsed()
    {
        var wrongDates = RunSeries().Select(_ => new ElementDates(Jan(1), Jan(1))).ToArray();

        AssertSpan(TrendDates.Describe(RunSeries(), wrongDates, 3, ChartType.Renko), Jan(9), Jan(10), 1);
    }

    // ── Point and Figure: one column is one run; dates come from ElementDates ──

    private static CoreCandleData[] PnfSeries() => new[] { Up(Jan(8)), Down(Jan(14)), Up(Jan(20)) };

    private static ElementDates[] PnfDates() => new[]
    {
        new ElementDates(Jan(5), Jan(8)), new ElementDates(Jan(12), Jan(14)), new ElementDates(Jan(20), Jan(20)),
    };

    [Theory]
    [InlineData(0, null, 8, null)]
    [InlineData(1, 12, 14, 2)]
    [InlineData(2, 20, 20, 0)]
    public void PointAndFigure_ReversalIsTheCreationOfTheColumn_UpdateIsItsLastExtension(int index, int? reversalDay, int updateDay, int? days)
    {
        AssertSpan(
            TrendDates.Describe(PnfSeries(), PnfDates(), index, ChartType.PointAndFigure),
            reversalDay is { } d ? Jan(d) : null, Jan(updateDay), days);
    }

    // ── Kagi: a run is a column of KagiColumns; the flat anchor is no trend ──

    // Columns: {0 anchor} {1,2 up} {3,4 down} {5 up}
    private static CoreCandleData[] KagiSeries() => new[]
    {
        Flat(Jan(2)), Up(Jan(6)), Up(Jan(7)), Down(Jan(11)), Down(Jan(12)), Up(Jan(22)),
    };

    private static ElementDates[] KagiDates() => new[]
    {
        new ElementDates(Jan(2), Jan(2)), new ElementDates(Jan(5), Jan(6)), new ElementDates(Jan(7), Jan(7)),
        new ElementDates(Jan(10), Jan(11)), new ElementDates(Jan(12), Jan(12)), new ElementDates(Jan(20), Jan(22)),
    };

    [Theory]
    [InlineData(0, null, 2, null)]
    [InlineData(1, null, 7, null)]
    [InlineData(2, null, 7, null)]
    [InlineData(3, 10, 12, 2)]
    [InlineData(4, 10, 12, 2)]
    [InlineData(5, 20, 22, 2)]
    public void Kagi_ReversalIsTheFirstSegmentOfTheColumn_UpdateIsTheLastSegmentOfTheColumn(int index, int? reversalDay, int updateDay, int? days)
    {
        AssertSpan(
            TrendDates.Describe(KagiSeries(), KagiDates(), index, ChartType.Kagi),
            reversalDay is { } d ? Jan(d) : null, Jan(updateDay), days);
    }

    // ── Not defined ──

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void IndexOutOfRange_IsNone(int index)
    {
        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(RunSeries(), null, index, ChartType.Renko));
    }

    [Fact]
    public void NullOrEmptySeries_IsNone()
    {
        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(null, null, 0, ChartType.Renko));
        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(Array.Empty<CoreCandleData>(), null, 0, ChartType.Renko));
    }

    [Fact]
    public void SingleElement_HasAnUpdateDateOnly()
    {
        AssertSpan(TrendDates.Describe(new[] { Up(Jan(5)) }, null, 0, ChartType.Renko), null, Jan(5), null);
        AssertSpan(
            TrendDates.Describe(new[] { Up(Jan(5)) }, new[] { new ElementDates(Jan(3), Jan(5)) }, 0, ChartType.PointAndFigure),
            null, Jan(5), null);
    }

    [Theory]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.Kagi)]
    public void PointAndFigureAndKagi_WithoutDatesOrWithAnotherCount_AreNone(ChartType chartType)
    {
        var series = chartType == ChartType.Kagi ? KagiSeries() : PnfSeries();
        var dates = chartType == ChartType.Kagi ? KagiDates() : PnfDates();

        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(series, null, series.Length - 1, chartType));
        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(series, dates.Take(dates.Length - 1).ToArray(), series.Length - 1, chartType));
        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(series, dates.Append(dates[0]).ToArray(), series.Length - 1, chartType));
    }

    [Theory]
    [InlineData(ChartType.Candlestick)]
    [InlineData(ChartType.HeikinAshi)]
    public void ChartsThatAreTimeSeries_AreNone(ChartType chartType)
    {
        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(RunSeries(), null, 3, chartType));
        Assert.Equal(TrendDateSpan.None, TrendDates.Describe(KagiSeries(), KagiDates(), 3, chartType));
    }

    // ── Days are calendar days ──

    [Theory]
    [InlineData("2026-01-30", "2026-02-02", 3)]
    [InlineData("2028-02-28", "2028-03-01", 2)] // leap year
    [InlineData("2026-12-31", "2027-01-01", 1)]
    [InlineData("2026-03-02 15:00", "2026-03-02 15:30", 0)]
    [InlineData("2026-03-01 23:59", "2026-03-02 00:01", 1)] // two minutes apart, but the next calendar day
    public void Days_AreCalendarDays_OfTheDateParts(string reversal, string update, int days)
    {
        var series = new[] { Up(Jan(1)), Down(Jan(2)) };
        var dates = new[]
        {
            new ElementDates(Jan(1), Jan(1)),
            new ElementDates(DateTime.Parse(reversal, System.Globalization.CultureInfo.InvariantCulture), DateTime.Parse(update, System.Globalization.CultureInfo.InvariantCulture)),
        };

        var span = TrendDates.Describe(series, dates, 1, ChartType.PointAndFigure);

        Assert.Equal(days, span.Days);
    }

    [Fact]
    public void UpdateBeforeReversal_KeepsTheDates_AndLeavesTheDaysEmpty()
    {
        var dates = new[] { new ElementDates(Jan(5), Jan(8)), new ElementDates(Jan(20), Jan(12)), new ElementDates(Jan(20), Jan(20)) };

        AssertSpan(TrendDates.Describe(PnfSeries(), dates, 1, ChartType.PointAndFigure), Jan(20), Jan(12), null);
    }

    // ── Purity ──

    [Fact]
    public void CalledTwice_DoesNotChangeItsInput_AndGivesTheSameResult()
    {
        var series = KagiSeries();
        var dates = KagiDates();
        var seriesBefore = series.ToArray();
        var datesBefore = dates.ToArray();

        var first = TrendDates.Describe(series, dates, 4, ChartType.Kagi);
        var second = TrendDates.Describe(series, dates, 4, ChartType.Kagi);

        Assert.Equal(first, second);
        Assert.Equal(seriesBefore, series);
        Assert.Equal(datesBefore, dates);
    }
}
