using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="ScreenerSideFrames"/> (which sides read bars, which frames a condition reads), <see cref="ScreenerBarSet"/> and <see cref="ScreenerComparison"/>:
/// the definitions the scan routing, the bar loading and the evaluation share.
/// </summary>
public sealed class ScreenerSideFramesTests
{
    private static ScreenerConditionTreeCondition Tree(params ScreenerIndicatorEntry[] entries)
        => new(Group(LogicalOperator.And, entries.Select(Leaf).ToArray()));

    // ---- which sides read bars ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void IndicatorPriceAndCriteriaSidesReadBarsAColumnOnlyWhenItNamesABarValue()
    {
        Assert.True(ScreenerSideFrames.ReadsBars(Sma(5, TimeFrame.W1)));
        Assert.True(ScreenerSideFrames.ReadsBars(Close(TimeFrame.W1)));
        Assert.True(ScreenerSideFrames.ReadsBars(new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Criteria, OutputName = "BullishEngulfing" }));
        Assert.True(ScreenerSideFrames.ReadsBars(CloseColumn(TimeFrame.W1)));
        Assert.False(ScreenerSideFrames.ReadsBars(Fundamental(TimeFrame.W1)));
        Assert.False(ScreenerSideFrames.ReadsBars(null));
    }

    [Fact]
    public void TheFrameOfASideIsItsOwnWhenItReadsBarsAndD1WhenItDoesNot()
    {
        Assert.Equal(TimeFrame.W1, ScreenerSideFrames.FrameOf(Sma(5, TimeFrame.W1)));
        Assert.Equal(TimeFrame.MN1, ScreenerSideFrames.FrameOf(CloseColumn(TimeFrame.MN1)));
        Assert.Equal(TimeFrame.D1, ScreenerSideFrames.FrameOf(Fundamental(TimeFrame.MN1)));
    }

    // ---- the frames a condition reads --------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADailyConditionReadsD1Only()
    {
        ScreenerConditionTreeCondition tree = Tree(Versus(Close(), ComparisonOperator.GreaterThan, Sma(5)));

        Assert.Equal(new[] { TimeFrame.D1 }, ScreenerSideFrames.Collect(tree));
        Assert.False(ScreenerSideFrames.UsesNonDailyBars(tree));
    }

    [Fact]
    public void BothSidesCountAndTheFramesFollowTheOrderOfTheSingleDefinition()
    {
        ScreenerIndicatorEntry entry = Versus(Sma(5, TimeFrame.MN1), ComparisonOperator.GreaterThan, Close(TimeFrame.W1));

        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 }, ScreenerSideFrames.Collect(entry));
        Assert.True(ScreenerSideFrames.UsesNonDailyBars(entry));
        Assert.Equal(DataServiceTimeFrames.Supported, ScreenerSideFrames.Collect(entry));
    }

    [Fact]
    public void TheRightSideCountsOnlyWhenTheEntryComparesAgainstAnIndicator()
    {
        ScreenerIndicatorEntry numeric = Versus(Close(), ComparisonOperator.GreaterThan, 100m);
        numeric.RightHand = Sma(5, TimeFrame.W1); // stored but not evaluated in this target mode

        Assert.Equal(new[] { TimeFrame.D1 }, ScreenerSideFrames.Collect(numeric));
        Assert.False(ScreenerSideFrames.UsesNonDailyBars(numeric));
    }

    [Fact]
    public void AFundamentalColumnHasNoFrameButABarValueColumnHasOne()
    {
        Assert.Equal(new[] { TimeFrame.D1 }, ScreenerSideFrames.Collect(Versus(Fundamental(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m)));
        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1 }, ScreenerSideFrames.Collect(Versus(CloseColumn(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m)));
    }

    [Fact]
    public void InATreeOnlyTheEnabledLeavesCountButAnEntryGivenDirectlyAlwaysDoes()
    {
        ScreenerIndicatorEntry disabledWeekly = Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m, enabled: false);

        Assert.Equal(new[] { TimeFrame.D1 }, ScreenerSideFrames.Collect(Tree(Versus(Close(), ComparisonOperator.GreaterThan, 1m), disabledWeekly)));
        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1 }, ScreenerSideFrames.Collect(disabledWeekly)); // evaluated as given
    }

    [Fact]
    public void TheEntriesOfATemplateCountWhenEnabled()
    {
        var entries = new[]
        {
            Versus(Close(), ComparisonOperator.GreaterThan, 1m),
            Versus(Close(TimeFrame.MN1), ComparisonOperator.GreaterThan, 1m, enabled: false),
        };

        Assert.Equal(new[] { TimeFrame.D1 }, ScreenerSideFrames.Collect(entries));
        Assert.False(ScreenerSideFrames.UsesNonDailyBars(entries));

        entries[1].IsEnabled = true;
        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.MN1 }, ScreenerSideFrames.Collect(entries));
        Assert.True(ScreenerSideFrames.UsesNonDailyBars(entries));
    }

    [Fact]
    public void AFrameTheDataLayerDoesNotDefineFollowsTheDefinedOnesSoTheCallerCanRefuseIt()
    {
        ScreenerIndicatorEntry entry = Versus(Sma(5, TimeFrame.H1), ComparisonOperator.GreaterThan, Close(TimeFrame.W1));

        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.H1 }, ScreenerSideFrames.Collect(entry));
    }

    [Fact]
    public void AnyOtherConditionTypeAndNullReadD1Only()
    {
        Assert.Equal(new[] { TimeFrame.D1 }, ScreenerSideFrames.Collect((IScreeningCondition?)null));
        Assert.Equal(new[] { TimeFrame.D1 }, ScreenerSideFrames.Collect(new NeverCondition()));
    }

    private sealed class NeverCondition : IScreeningCondition
    {
        public bool IsMet(IReadOnlyList<CandleData> candles) => false;
    }

    // ---- the bar set -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ABarSetServesTheListsItWasBuiltWithAndTheirCoreForm()
    {
        ScreenerBarSet set = BarSet();

        Assert.Equal(30, set.Get(TimeFrame.D1).Count);
        Assert.Equal(4, set.Get(TimeFrame.W1).Count);
        IReadOnlyList<CoreCandleData> core = set.GetCore(TimeFrame.W1);
        Assert.Equal(4, core.Count);
        Assert.Equal(set.Get(TimeFrame.W1).Select(bar => (bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)),
            core.Select(bar => (bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)));
        Assert.Same(core, set.GetCore(TimeFrame.W1)); // converted once, not per call
        Assert.Equal(127m, core[^1].Close);
    }

    [Fact]
    public void AFrameThatWasNotLoadedGivesAnEmptyListNeverNull()
    {
        ScreenerBarSet set = BarSet();

        Assert.Empty(set.Get(TimeFrame.MN1));
        Assert.Empty(set.GetCore(TimeFrame.MN1));
        Assert.Empty(new ScreenerBarSet(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>()).Get(TimeFrame.D1));
    }

    [Fact]
    public void ABarSetRefusesANullDictionary()
        => Assert.Throws<System.ArgumentNullException>(() => new ScreenerBarSet(null!));

    // ---- the comparison ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.GreaterThan, 2, 1, true)]
    [InlineData(ComparisonOperator.GreaterThan, 1, 1, false)]
    [InlineData(ComparisonOperator.LessThan, 1, 2, true)]
    [InlineData(ComparisonOperator.LessThan, 2, 2, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 2, 2, true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 1, 2, false)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 2, 2, true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 3, 2, false)]
    [InlineData(ComparisonOperator.Equal, 2, 2, true)]
    [InlineData(ComparisonOperator.Equal, 2, 3, false)]
    [InlineData(ComparisonOperator.NotEqual, 2, 3, true)]
    [InlineData(ComparisonOperator.NotEqual, 2, 2, false)]
    public void TheSixValueComparisonsDecideAsTheirSymbolSays(ComparisonOperator op, int left, int right, bool expected)
        => Assert.Equal(expected, ScreenerComparison.Compare(op, left, right));

    [Fact]
    public void EveryOtherOperatorIsNeverMetAsInTheDailyEvaluation()
    {
        foreach (ComparisonOperator op in System.Enum.GetValues<ComparisonOperator>().Where(op => op is not (ComparisonOperator.GreaterThan or ComparisonOperator.LessThan
                     or ComparisonOperator.GreaterThanOrEqual or ComparisonOperator.LessThanOrEqual or ComparisonOperator.Equal or ComparisonOperator.NotEqual)))
        {
            Assert.False(ScreenerComparison.Compare(op, 2m, 1m));
            Assert.False(ScreenerComparison.Compare(op, 1m, 2m));
            Assert.False(ScreenerComparison.Compare(op, 1m, 1m));
        }
    }
}
