using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Plan task T5: with W1 or MN1 the breadth axis is the calendar period start, so one week (or month) that two markets date by different first trading
/// days is one axis point and not two half-filled ones.
/// </summary>
public class MarketBreadthPeriodAlignmentTests
{
    private static readonly DateTime FirstMonday = new(2024, 1, 1);

    private static CandleData Bar(DateTime date, decimal close) => new(date, close, close, close, close, 1000);

    /// <summary>Eight weekly bars rising each week; week <paramref name="shiftedWeek"/> is dated the Tuesday.</summary>
    private static List<CandleData> Weekly(decimal start, int shiftedWeek = -1)
        => Enumerable.Range(0, 8).Select(w => Bar(FirstMonday.AddDays(7 * w + (w == shiftedWeek ? 1 : 0)), start + 2m * w)).ToList();

    private static IReadOnlyDictionary<string, IReadOnlyList<CandleData>> Universe(IReadOnlyList<CandleData> a, IReadOnlyList<CandleData> b)
        => new Dictionary<string, IReadOnlyList<CandleData>> { ["A"] = a, ["B"] = b };

    [Fact]
    public void W1_PutsTheSameWeekOnOneAxisPoint()
    {
        var universe = Universe(Weekly(100m), Weekly(50m, shiftedWeek: 3));

        MarketBreadthResult w1 = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { AdRatioBars = 2, TimeFrame = TimeFrame.W1 });

        Assert.Equal(8, w1.DateCount);
        Assert.All(w1.Series.Dates, d => Assert.Equal(DayOfWeek.Monday, DateTime.Parse(d, System.Globalization.CultureInfo.InvariantCulture).DayOfWeek));
        Assert.Equal("2024-01-22", w1.Series.Dates[3]);
        // Both symbols rise every week, so every week after the first has two advancers and nobody is counted on a half-empty split row.
        Assert.Equal(new[] { 0, 2, 2, 2, 2, 2, 2, 2 }, w1.Series.Advancers);
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0 }, w1.Series.Decliners);
        Assert.Contains(ReturnStatistics.PeriodStartDateNote, w1.Notes);
    }

    [Fact]
    public void D1_DefaultKeepsTheExactDateAxis_SoTheShiftedWeekSplits()
    {
        var universe = Universe(Weekly(100m), Weekly(50m, shiftedWeek: 3));

        MarketBreadthResult exact = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { AdRatioBars = 2 });

        // This is what W1 used to give: the Monday and the Tuesday of the same week are two axis points, each with one advancer.
        Assert.Equal(9, exact.DateCount);
        Assert.DoesNotContain(ReturnStatistics.PeriodStartDateNote, exact.Notes);
    }

    [Fact]
    public void MN1_PutsTheSameMonthOnOneAxisPoint()
    {
        List<CandleData> Monthly(decimal start, int shiftedMonth = -1)
            => Enumerable.Range(0, 8).Select(m => Bar(new DateTime(2024, 1 + m, m == shiftedMonth ? 3 : 1), start + 2m * m)).ToList();

        MarketBreadthResult mn1 = MarketBreadthCalculator.Calculate(
            Universe(Monthly(100m), Monthly(50m, shiftedMonth: 4)),
            new MarketBreadthParameters { AdRatioBars = 2, TimeFrame = TimeFrame.MN1 });

        Assert.Equal(8, mn1.DateCount);
        Assert.Equal("2024-05-01", mn1.Series.Dates[4]);
        Assert.Equal(2, mn1.Series.Advancers[4]);
    }

    [Fact]
    public void ExplicitD1_EqualsTheDefaultParameters()
    {
        var universe = Universe(Weekly(100m), Weekly(50m, shiftedWeek: 3));

        MarketBreadthResult explicitD1 = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { AdRatioBars = 2, TimeFrame = TimeFrame.D1 });
        MarketBreadthResult defaulted = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { AdRatioBars = 2 });

        Assert.Equal(defaulted.Series.Dates, explicitD1.Series.Dates);
        Assert.Equal(defaulted.Series.Advancers, explicitD1.Series.Advancers);
        Assert.Equal(defaulted.Series.AdLine, explicitD1.Series.AdLine);
        Assert.Equal(defaulted.Notes, explicitD1.Notes);
    }
}
