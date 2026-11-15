using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;
using Xunit.Abstractions;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// WebAiExpansion plan 12 T5 (D-3 B, only when reproduced): on the main chart's GaplessTime axis (non-trading days are squeezed out of the time axis) every additional
/// line of a Pitchfork (half lines f = +-0.5, warning lines f = +-1.5 and +-2.0) must run parallel on the screen to the median line, as the outer lines do.
/// </summary>
public sealed class PitchforkGaplessParallelismTests
{
    /// <summary>The tolerance the other Pitchfork geometry tests use.</summary>
    private const double ToleranceInPixels = 0.5;

    private static readonly decimal[] Multipliers = { 0.5m, -0.5m, 1.5m, -1.5m, 2.0m, -2.0m };

    private readonly ITestOutputHelper _output;

    public PitchforkGaplessParallelismTests(ITestOutputHelper output) => _output = output;

    /// <summary>150 consecutive weekdays (Saturdays and Sundays skipped) from Monday 2025-01-06.</summary>
    private static List<DateTime> WeekdayTimeMap()
    {
        var map = new List<DateTime>(150);
        for (DateTime day = new(2025, 1, 6); map.Count < 150; day = day.AddDays(1))
        {
            if (day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday)
            {
                map.Add(day);
            }
        }

        return map;
    }

    private static GenericCoordinateTransform CreateGaplessTransform(IReadOnlyList<DateTime> timeMap)
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 500);
        transform.SetTimeMap(timeMap);
        transform.SetTimeRange(timeMap[0], timeMap[^1]);
        transform.SetPriceRange(0m, 500m);
        return transform;
    }

    [Fact]
    public void GaplessAxis_EveryAdditionalLine_RunsParallelOnTheScreenToTheMedianLine()
    {
        List<DateTime> timeMap = WeekdayTimeMap();
        GenericCoordinateTransform transform = CreateGaplessTransform(timeMap);
        var p1 = new ChartPoint(timeMap[20], 100m);
        var p2 = new ChartPoint(timeMap[60], 300m);
        var p3 = new ChartPoint(timeMap[100], 200m);

        double maxDeviation = 0;
        foreach (PitchforkType type in new[] { PitchforkType.Andrews, PitchforkType.Schiff, PitchforkType.ModifiedSchiff })
        {
            var shape = PitchforkObject.GetScreenShape(type, p1, p2, p3, transform);
            Point v = new(shape.Median.X - shape.Anchor.X, shape.Median.Y - shape.Anchor.Y);

            foreach (decimal factor in Multipliers)
            {
                var (origin, target) = PitchforkObject.GetParallelLineScreenPoints(type, p1, p2, p3, factor, transform);
                double dx = (target.X - origin.X) - v.X;
                double dy = (target.Y - origin.Y) - v.Y;
                double deviation = Math.Sqrt((dx * dx) + (dy * dy));
                maxDeviation = Math.Max(maxDeviation, deviation);
                _output.WriteLine($"{type} f={factor}: deviation {deviation:F3} px");
            }
        }

        _output.WriteLine($"maximum deviation {maxDeviation:F3} px (tolerance {ToleranceInPixels} px)");
        Assert.True(maxDeviation <= ToleranceInPixels, $"an additional line leaves the median direction by {maxDeviation:F3} px on the Gapless axis");
    }
}
