using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="ScreenerIndicatorEntry.IsMetOnBars"/>: every side reads the bars of its own time frame. Bars: see <see cref="ScreenerSideFrameFixtures"/> (daily close 129 on
/// the last of 30 days; weekly closes 106, 113, 120, 127 for the four complete weeks).
/// </summary>
public sealed class ScreenerIndicatorEntryOnBarsTests
{
    private static bool OnBars(ScreenerIndicatorEntry entry) => entry.IsMetOnBars(BarSet(), default);

    // ---- with daily sides only, the answer is the one of the original evaluation ------------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.GreaterThan, 128, true)]
    [InlineData(ComparisonOperator.GreaterThan, 129, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 129, true)]
    [InlineData(ComparisonOperator.LessThan, 130, true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 128, false)]
    [InlineData(ComparisonOperator.Equal, 129, true)]
    [InlineData(ComparisonOperator.NotEqual, 129, false)]
    public void WithDailySidesOnlyTheAnswerEqualsTheOriginalEvaluation(ComparisonOperator op, int right, bool expected)
    {
        ScreenerIndicatorEntry entry = Versus(Close(), op, right);

        Assert.Equal(expected, entry.IsMet(Daily(30), default));
        Assert.Equal(expected, OnBars(entry));
    }

    [Fact]
    public void WithDailyIndicatorSidesOnBothSidesTheAnswerEqualsTheOriginalEvaluation()
    {
        List<CandleData> daily = Daily(30);

        foreach (ScreenerIndicatorEntry entry in new[]
                 {
                     Versus(Close(), ComparisonOperator.GreaterThan, Sma(5)),
                     Versus(Close(), ComparisonOperator.LessThan, Sma(5)),
                     Versus(Sma(3, offset: 2), ComparisonOperator.LessThan, Sma(10, offset: 1)),
                     Versus(Close(offset: 40), ComparisonOperator.GreaterThan, Sma(5)), // a bar that does not exist
                 })
        {
            Assert.Equal(entry.IsMet(daily, default), entry.IsMetOnBars(BarSet(), default));
        }
    }

    // ---- each side reads its own frame -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADailyPriceCanBeComparedWithAWeeklyAverage()
    {
        ScreenerIndicatorSideConfig weeklyAverage = Sma(2, TimeFrame.W1); // (120 + 127) / 2 = 123.5

        Assert.True(OnBars(Versus(Close(), ComparisonOperator.GreaterThan, weeklyAverage))); // 129 > 123.5
        Assert.False(OnBars(Versus(Close(), ComparisonOperator.LessThan, weeklyAverage)));
    }

    [Fact]
    public void AWeeklySideReadsTheLastCompleteWeekAndItsOffsetCountsWeeks()
    {
        Assert.True(OnBars(Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m)));
        Assert.True(OnBars(Versus(Close(TimeFrame.W1, offset: 1), ComparisonOperator.Equal, 120m)));
        Assert.False(OnBars(Versus(Close(TimeFrame.W1, offset: 1), ComparisonOperator.Equal, 127m)));
    }

    [Fact]
    public void AnOffsetBeyondTheFirstBarOfTheSidesOwnFrameGivesNoMatchWhateverTheDailyBarsHold()
    {
        // 30 daily bars are enough for Offset 4, but the weekly list has four bars: index -1 does not exist, and the latest bar is never substituted.
        ScreenerIndicatorEntry entry = Versus(Close(TimeFrame.W1, offset: 4), ComparisonOperator.GreaterThan, 0m);

        Assert.False(OnBars(entry));
        Assert.True(OnBars(Versus(Close(offset: 4), ComparisonOperator.GreaterThan, 0m)));
    }

    [Fact]
    public void ASideOnAFrameWithNoBarGivesNoMatch()
    {
        var withoutWeeks = new ScreenerBarSet(new Dictionary<TimeFrame, IReadOnlyList<CandleData>> { [TimeFrame.D1] = Daily(30) });

        Assert.False(Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 0m).IsMetOnBars(withoutWeeks, default));
        Assert.True(Versus(Close(), ComparisonOperator.GreaterThan, 0m).IsMetOnBars(withoutWeeks, default));
    }

    [Fact]
    public void WithoutDailyBarsNothingMatches()
    {
        var noBars = new ScreenerBarSet(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>());

        Assert.False(Versus(Close(), ComparisonOperator.GreaterThan, 0m).IsMetOnBars(noBars, default));
    }

    [Fact]
    public void ASideOnAFrameTheDataLayerDoesNotDefineGivesNoMatch()
    {
        var bars = new ScreenerBarSet(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.D1] = Daily(30),
            [TimeFrame.H1] = Daily(30), // even if somebody built a list for it
        });

        Assert.False(Versus(Close(TimeFrame.H1), ComparisonOperator.GreaterThan, 0m).IsMetOnBars(bars, default));
    }

    [Fact]
    public void AFundamentalColumnIgnoresItsFrameAndNeedsOnlyTheDailyBars()
    {
        // No metadata is given, so a fundamental has no value; the point is that the weekly frame (not loaded here) is not asked for it.
        var dailyOnly = new ScreenerBarSet(new Dictionary<TimeFrame, IReadOnlyList<CandleData>> { [TimeFrame.D1] = Daily(30) });
        ScreenerIndicatorEntry entry = Versus(Fundamental(TimeFrame.W1), ComparisonOperator.GreaterThan, 0m);

        Assert.Equal(entry.IsMet(Daily(30), default), entry.IsMetOnBars(dailyOnly, default));
    }

    [Fact]
    public void AnEntryOfARightSideThatIsNotEvaluatedIgnoresIt()
    {
        ScreenerIndicatorEntry entry = Versus(Close(), ComparisonOperator.GreaterThan, 100m);
        entry.RightHand = Close(TimeFrame.MN1, offset: 99); // stored, not evaluated in the numeric target mode

        Assert.True(OnBars(entry));
    }

    [Fact]
    public async Task TheAsyncFormGivesTheSameAnswerAndAConditionWithoutItRefusesInsteadOfFallingBackToDaily()
    {
        ScreenerIndicatorEntry entry = Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m);

        Assert.True(await entry.IsMetOnBarsAsync(BarSet(), default));

        IScreeningCondition dailyOnly = new DailyOnlyCondition();
        await Assert.ThrowsAsync<NotSupportedException>(() => dailyOnly.IsMetOnBarsAsync(BarSet(), default).AsTask());
    }

    [Fact]
    public void ABarSetIsRequired()
        => Assert.Throws<ArgumentNullException>(() => Versus(Close(), ComparisonOperator.GreaterThan, 0m).IsMetOnBars(null!, default));

    private sealed class DailyOnlyCondition : IScreeningCondition
    {
        public bool IsMet(IReadOnlyList<CandleData> candles) => true;
    }
}
