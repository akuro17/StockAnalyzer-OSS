using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The Data-tab value of the regression trend at a bar time uses the bar's position in the regression range (the x of the fit),
/// not a ratio of elapsed seconds, so non-trading days do not shift it.
/// </summary>
public sealed class RegressionTrendBarIndexTests
{
    private static readonly decimal[] Closes = { 100, 103, 101, 108, 112, 110, 119, 121, 118, 130, 128, 135, 141, 138, 150 };

    // 15 weekday bars starting on a Monday: the series crosses two weekends.
    private static List<CoreCandleData> WeekdayCandles()
    {
        var candles = new List<CoreCandleData>();
        var day = new DateTime(2026, 1, 5);
        foreach (var close in Closes)
        {
            while (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) day = day.AddDays(1);
            candles.Add(new CoreCandleData(day, close, close, close, close, 1000));
            day = day.AddDays(1);
        }
        return candles;
    }

    // The same closes on consecutive calendar days: elapsed seconds and bar numbers agree here.
    private static List<CoreCandleData> ConsecutiveDayCandles()
    {
        var first = new DateTime(2026, 1, 5);
        return Closes.Select((close, i) => new CoreCandleData(first.AddDays(i), close, close, close, close, 1000)).ToList();
    }

    private static RegressionTrendObject Fit(List<CoreCandleData> candles)
    {
        var obj = new RegressionTrendObject(new ChartPoint(candles[0].Timestamp, 0m), new ChartPoint(candles[^1].Timestamp, 0m));
        obj.Recalculate(candles);
        return obj;
    }

    private static decimal Center(RegressionTrendObject obj, DateTime time)
        => Assert.Single(obj.GetCalculatedValues(time), v => v.Key == "RegressionCenter").NumericValue!.Value;

    [Fact]
    public void CenterAtABarTime_EqualsTheFitValueOfThatBarNumber_AcrossWeekends()
    {
        var weekday = WeekdayCandles();
        var consecutive = ConsecutiveDayCandles();
        Assert.Equal(2, Enumerable.Range(1, weekday.Count - 1).Count(i => (weekday[i].Timestamp - weekday[i - 1].Timestamp).Days > 1));

        using var withGaps = Fit(weekday);
        using var withoutGaps = Fit(consecutive);

        for (int k = 0; k < weekday.Count; k++)
        {
            Assert.Equal(Center(withoutGaps, consecutive[k].Timestamp), Center(withGaps, weekday[k].Timestamp));
        }
    }

    [Fact]
    public void CenterBetweenTwoBars_UsesTheEarlierBar()
    {
        var weekday = WeekdayCandles();
        using var fit = Fit(weekday);

        // Saturday after the first Friday bar (index 4): the newest bar at or before the time is bar 4.
        var saturday = weekday[4].Timestamp.AddDays(1);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);
        Assert.Equal(Center(fit, weekday[4].Timestamp), Center(fit, saturday));
    }

    [Fact]
    public void CenterOutsideTheRange_KeepsTheElapsedTimeExtrapolation()
    {
        var consecutive = ConsecutiveDayCandles();
        using var fit = Fit(consecutive);

        // Consecutive days: the elapsed-time ratio and the bar number agree, so one day after the end is x = Count.
        var center = Center(fit, consecutive[^1].Timestamp.AddDays(1));
        var slope = Assert.Single(fit.GetCalculatedValues(consecutive[^1].Timestamp), v => v.Key == "Slope").NumericValue!.Value;
        Assert.Equal(Center(fit, consecutive[^1].Timestamp) + slope, center, 6);
    }
}
