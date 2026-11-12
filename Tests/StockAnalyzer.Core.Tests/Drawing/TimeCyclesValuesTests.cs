using System;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>
/// TimeCycles: a time on a cycle boundary starts a new cycle (phase 0%) and NextCycle is the first boundary strictly after the time
/// (owner decision D-1 B, 2026-11-12). The cycle is the interval between the two control points.
/// </summary>
public sealed class TimeCyclesValuesTests
{
    private static readonly DrawingAnchor P1 = new(new DateTime(2026, 1, 1), 100m);
    private static readonly DrawingAnchor P2 = new(new DateTime(2026, 1, 11), 150m);

    private static DateTime Parse(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DrawingCalculatedValue Value(System.Collections.Generic.IReadOnlyList<DrawingCalculatedValue> values, string key) => values.Single(v => v.Key == key);

    // time, expected NextCycle text, expected CycleProgress NumericValue (the progress formula is unchanged)
    public static TheoryData<string, string, decimal> Rows() => new()
    {
        { "2026-01-01 00:00:00", "2026-01-11 00:00", 0.0m },
        { "2026-01-06 12:00:00", "2026-01-11 00:00", 55.0m },
        { "2026-01-10 23:59:59", "2026-01-11 00:00", 100.0m },
        { "2026-01-11 00:00:00", "2026-01-21 00:00", 0.0m },
        { "2026-01-11 00:00:01", "2026-01-21 00:00", 0.0m },
        { "2026-01-21 00:00:00", "2026-01-31 00:00", 0.0m },
        { "2026-01-21 18:30:15", "2026-01-31 00:00", 7.7m },
        { "2025-12-25 06:00:00", "2026-01-01 00:00", 32.5m },
        { "2025-12-22 00:00:00", "2026-01-01 00:00", 0.0m },
        { "2025-12-21 23:59:59", "2025-12-22 00:00", 100.0m },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void NextCycle_IsTheFirstBoundaryStrictlyAfterTheTime_AndTheProgressIsUnchanged(string time, string expectedNextCycle, decimal expectedProgress)
    {
        var values = TimeCyclesValues.Calculate(P1, P2, Parse(time), IndicatorColor.Gray);

        Assert.Equal(expectedNextCycle, Value(values, "NextCycle").FormattedText);
        Assert.Equal(expectedProgress, Value(values, "CycleProgress").NumericValue);
        Assert.Equal(10.0m, Value(values, "Period").NumericValue);
    }

    [Fact]
    public void ReversedControlPoints_TheCycleStartsAtTheFirstPoint_AndABoundaryTimeGivesTheNextBoundary()
    {
        var values = TimeCyclesValues.Calculate(P2, P1, new DateTime(2026, 1, 11), IndicatorColor.Gray);

        Assert.Equal("2026-01-21 00:00", Value(values, "NextCycle").FormattedText);
        Assert.Equal(0.0m, Value(values, "CycleProgress").NumericValue);
    }

    [Fact]
    public void AnIntervalUnderOneSecond_GivesNoValues()
    {
        var values = TimeCyclesValues.Calculate(P1, new DrawingAnchor(P1.Time.AddMilliseconds(500), 150m), P1.Time, IndicatorColor.Gray);

        Assert.Empty(values);
    }
}
