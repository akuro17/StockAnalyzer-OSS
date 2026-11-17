using System;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Rendering;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The time to bar-position rule of the AI chart image (<see cref="AiChartTimeAxis"/>, Core) must be the rule of the application's chart: <see cref="GenericCoordinateTransform.GetFractionalIndex"/>
/// of the Gapless time axis. This compares the two on the same time maps, on a bar, between bars, over a weekend, before the first and after the last bar, and for maps that span no time.
/// </summary>
public sealed class AiChartTimeAxisParityTests
{
    private static DateTime[] WeekdayBars(int count)
    {
        var times = new System.Collections.Generic.List<DateTime>();
        DateTime day = new(2026, 1, 5);
        while (times.Count < count)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                times.Add(day);
            }

            day = day.AddDays(1);
        }

        return times.ToArray();
    }

    private static double Chart(DateTime[] map, DateTime time)
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 500);
        transform.SetTimeMap(map);
        return transform.GetFractionalIndex(time);
    }

    public static TheoryData<int> MapSizes => new() { 1, 2, 3, 5, 12, 60 };

    [Theory]
    [MemberData(nameof(MapSizes))]
    public void TheImageRuleEqualsTheChartRule_OnBars_BetweenBars_OverWeekends_AndOutsideTheMap(int count)
    {
        DateTime[] map = WeekdayBars(count);
        DateTime first = map[0];
        DateTime last = map[^1];

        var probes = map
            .Concat(new[] { last.AddHours(7), last.AddDays(12), first.AddMinutes(1) })
            .Concat(map.Select(t => t.AddHours(30)))
            .Concat(map.Select(t => t.AddMinutes(-90)))
            .Where(t => t > first)
            .Concat(new[] { first })
            .ToArray();

        foreach (DateTime time in probes)
        {
            Assert.Equal(Chart(map, time), AiChartTimeAxis.BarPosition(map, time), 9);
        }
    }

    [Theory]
    [MemberData(nameof(MapSizes))]
    public void ForATimeBeforeTheFirstBar_TheImageGivesTheNegativePosition_TheInverseOfTheChartsOwnTimeFromIndex(int count)
    {
        // GetFractionalIndex returns the mirrored (positive) value before the first bar (a sign slip, reported to the owner, not changed here); the image uses the inverse of GetTimeFromFractionalIndex.
        DateTime[] map = WeekdayBars(count);
        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 500);
        transform.SetTimeMap(map);

        foreach (DateTime time in new[] { map[0].AddHours(-5), map[0].AddDays(-9), map[0].AddDays(-40) })
        {
            double position = AiChartTimeAxis.BarPosition(map, time);
            Assert.True(position < 0);
            Assert.InRange(Math.Abs((transform.GetTimeFromFractionalIndex(position) - time).TotalSeconds), 0, 1);
        }
    }

    [Fact]
    public void ATimeMapThatSpansNoTime_UsesTheSameOneDayInterval()
    {
        DateTime[] equal = { new(2026, 1, 5), new(2026, 1, 5), new(2026, 1, 5) };

        foreach (DateTime time in new[] { new DateTime(2026, 1, 5), new DateTime(2026, 1, 8) })
        {
            Assert.Equal(Chart(equal, time), AiChartTimeAxis.BarPosition(equal, time), 9);
        }
    }
}
