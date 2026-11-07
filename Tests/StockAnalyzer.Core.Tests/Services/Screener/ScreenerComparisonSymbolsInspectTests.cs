using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// <see cref="ScreenerComparisonSymbols.Inspect(IScreeningCondition?)"/>: which comparison symbols the evaluable two-ticker sides of a Screener condition name,
/// and the first such side that reads a time frame other than D1. Real sides, entries and groups; no mocks.
/// </summary>
public sealed class ScreenerComparisonSymbolsInspectTests
{
    private static ScreenerIndicatorSideConfig Cross(IndicatorType type, object? symbol, TimeFrame frame = TimeFrame.D1)
    {
        var side = new ScreenerIndicatorSideConfig
        {
            IndicatorType = type,
            OutputName = IndicatorResult.MainSeriesName,
            TimeFrame = frame,
            Parameters = new Dictionary<string, object> { ["Period"] = 20 },
        };
        if (symbol is not null)
        {
            side.Parameters["ComparisonSymbol"] = symbol;
        }

        return side;
    }

    private static ScreenerIndicatorSideConfig Correlation(object? symbol, TimeFrame frame = TimeFrame.D1) => Cross(IndicatorType.Correlation, symbol, frame);

    private static ScreenerConditionTreeCondition Tree(params ScreenerIndicatorEntry[] entries)
        => new(Group(LogicalOperator.And, entries.Select(Leaf).ToArray()));

    private static ScreenerIndicatorEntry Above(ScreenerIndicatorSideConfig left, bool enabled = true) => Versus(left, ComparisonOperator.GreaterThan, 0m, enabled);

    // ---- nothing needed ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANullConditionAndAConditionOfAnotherTypeNeedNothing()
    {
        foreach (IScreeningCondition? condition in new IScreeningCondition?[] { null, new OtherCondition() })
        {
            ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(condition);

            Assert.Empty(requirement.Symbols);
            Assert.Empty(requirement.Spellings);
            Assert.Null(requirement.OffDailySide);
        }
    }

    [Fact]
    public void AConditionWithoutATwoTickerSideNeedsNothing()
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Versus(Close(), ComparisonOperator.GreaterThan, Sma(5))));

        Assert.Empty(requirement.Symbols);
        Assert.Empty(requirement.Spellings);
        Assert.Null(requirement.OffDailySide);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankComparisonSymbolOnDailyBarsNamesNothingAndStaysPriceVersusVolume(string? blank)
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation(blank))));

        Assert.Empty(requirement.Symbols);
        Assert.Empty(requirement.Spellings);
        Assert.Null(requirement.OffDailySide);
    }

    // ---- spelling and instrument ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASymbolIsTrimmedAndUpperCased()
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation(" spy "))));

        Assert.Equal(new[] { "SPY" }, requirement.Symbols);
        Assert.Equal(new[] { "SPY" }, requirement.Spellings);
    }

    [Fact]
    public void ASymbolNamedInTwoSpellingsOfOneCaseIsOne()
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation("SPY")), Above(Correlation("spy"))));

        Assert.Equal(new[] { "SPY" }, requirement.Symbols);
        Assert.Equal(new[] { "SPY" }, requirement.Spellings);
    }

    [Theory]
    [InlineData("7203.T", "7203-T")]
    [InlineData("^N225", "N225")]
    public void TwoSpellingsOfOneInstrumentAreOneSymbolWithBothSpellings(string first, string second)
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation(first)), Above(Correlation(second))));

        Assert.Equal(new[] { first }, requirement.Symbols);
        Assert.Equal(new[] { first, second }, requirement.Spellings);
    }

    [Fact]
    public void TheSymbolsFollowTheTraversalOrderAcrossNestedGroups()
    {
        var tree = new ScreenerConditionTreeCondition(Group(
            LogicalOperator.Or,
            Leaf(Above(Correlation("BBB"))),
            Group(LogicalOperator.And, Leaf(Above(Cross(IndicatorType.CrossCorrelation, "AAA"))), Leaf(Above(Correlation("CCC")))),
            Leaf(Above(Correlation("AAA")))));

        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(tree);

        Assert.Equal(new[] { "BBB", "AAA", "CCC" }, requirement.Symbols);
        Assert.Equal(new[] { "BBB", "AAA", "CCC" }, requirement.Spellings);
    }

    [Fact]
    public void ASymbolStoredAsAJsonStringIsRead()
    {
        object stored = JsonDocument.Parse("\"qqq\"").RootElement.Clone();

        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation(stored))));

        Assert.Equal(new[] { "QQQ" }, requirement.Symbols);
    }

    // ---- which sides count ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADisabledLeafIsIgnoredIncludingItsTimeFrame()
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation("SPY", TimeFrame.W1), enabled: false)));

        Assert.Empty(requirement.Symbols);
        Assert.Null(requirement.OffDailySide);
    }

    [Fact]
    public void AnEntryGivenDirectlyIsEvaluatedAsGivenWhetherOrNotItIsEnabled()
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Above(Correlation("SPY"), enabled: false));

        Assert.Equal(new[] { "SPY" }, requirement.Symbols);
    }

    [Fact]
    public void TheRightSideCountsOnlyWhenTheEntryComparesAgainstAnIndicator()
    {
        ScreenerIndicatorEntry numeric = Versus(Close(), ComparisonOperator.GreaterThan, 100m);
        numeric.RightHand = Correlation("SPY"); // stored but not evaluated in this target mode
        ScreenerIndicatorEntry versus = Versus(Close(), ComparisonOperator.GreaterThan, Correlation("QQQ"));

        Assert.Empty(ScreenerComparisonSymbols.Inspect(Tree(numeric)).Symbols);
        Assert.Equal(new[] { "QQQ" }, ScreenerComparisonSymbols.Inspect(Tree(versus)).Symbols);
    }

    [Fact]
    public void EveryTwoTickerTypeTheEligibilityRuleAllows_IsCollected_ExactlyLikeCorrelation()
    {
        // Until the Screener enabled the other seven two-ticker types this test pinned that a refused two-ticker side (RollingBeta) was ignored because it never had a value.
        // The one eligibility rule now allows every two-ticker type, so each of them (derived from the registry) is collected, and its off-daily frame is reported.
        IndicatorType[] types = IndicatorFactory.Default.GetRegisteredTypes()
            .Where(type => IndicatorFactory.CreateStatic(type) is ICrossTickerIndicator)
            .ToArray();
        Assert.True(types.Length >= 9);

        foreach (IndicatorType type in types)
        {
            ScreenerIndicatorSideConfig side = Cross(type, "SPY", TimeFrame.W1);

            Assert.True(ScreenerIndicatorEligibility.IsAllowed(type, side.OutputName), type.ToString());
            ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(side)));

            Assert.Equal(new[] { "SPY" }, requirement.Symbols);
            Assert.Same(side, requirement.OffDailySide);
        }
    }

    [Fact]
    public void ASideTheEligibilityRuleRefusesIsIgnoredBecauseItNeverHasAValue()
    {
        // A two-ticker side is refused only for a look-ahead series (none exists today), so the ignore rule is pinned with the rule itself: whatever
        // ScreenerIndicatorEligibility refuses is not collected. A look-ahead indicator that is not two-ticker is not collected either (it is not a two-ticker side at all).
        foreach (IndicatorType type in IndicatorFactory.Default.GetRegisteredTypes().Where(type => IndicatorFactory.CreateStatic(type) is ICrossTickerIndicator))
        {
            ScreenerIndicatorSideConfig side = Cross(type, "SPY");
            bool allowed = ScreenerIndicatorEligibility.IsAllowed(type, side.OutputName);

            Assert.Equal(allowed, ScreenerComparisonSymbols.Inspect(Tree(Above(side))).Symbols.Count == 1);
        }
    }

    [Fact]
    public void ASideThatIsNotATwoTickerIndicatorIsNeverCollectedEvenWithAComparisonSymbolParameter()
    {
        ScreenerIndicatorSideConfig sma = Sma(5, TimeFrame.W1);
        sma.Parameters["ComparisonSymbol"] = "SPY";

        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(sma)));

        Assert.Empty(requirement.Symbols);
        Assert.Null(requirement.OffDailySide);
    }

    [Fact]
    public void ANonIndicatorSideIsNeverCollected()
    {
        ScreenerIndicatorSideConfig column = CloseColumn(TimeFrame.W1);
        column.Parameters["ComparisonSymbol"] = "SPY";

        Assert.Empty(ScreenerComparisonSymbols.Inspect(Tree(Above(column))).Symbols);
    }

    // ---- the time frame ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TimeFrame.W1, "SPY")]
    [InlineData(TimeFrame.MN1, "SPY")]
    [InlineData(TimeFrame.W1, null)]
    [InlineData(TimeFrame.MN1, "")]
    public void ATwoTickerSideOnAnotherFrameThanDailyIsTheOffDailySideWhetherOrNotItNamesASymbol(TimeFrame frame, string? symbol)
    {
        ScreenerIndicatorSideConfig side = Correlation(symbol, frame);

        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(side)));

        Assert.Same(side, requirement.OffDailySide);
        Assert.Equal(string.IsNullOrEmpty(symbol) ? 0 : 1, requirement.Symbols.Count);
    }

    [Fact]
    public void TheOffDailySideIsTheFirstOneInTheTraversalOrder()
    {
        ScreenerIndicatorSideConfig first = Correlation("AAA", TimeFrame.MN1);
        ScreenerIndicatorSideConfig second = Correlation("BBB", TimeFrame.W1);

        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation("CCC")), Above(first), Above(second)));

        Assert.Same(first, requirement.OffDailySide);
        Assert.Equal(new[] { "CCC", "AAA", "BBB" }, requirement.Symbols);
    }

    [Fact]
    public void ASingleTickerSideOnAnotherFrameIsNotAnOffDailyTwoTickerSide()
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation("SPY")), Versus(Sma(5, TimeFrame.W1), ComparisonOperator.GreaterThan, 1m)));

        Assert.Null(requirement.OffDailySide);
        Assert.Equal(new[] { "SPY" }, requirement.Symbols);
    }

    // ---- independence between calls ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EachCallReturnsNewListsSoChangingOneDoesNotAffectTheNext()
    {
        ScreenerConditionTreeCondition tree = Tree(Above(Correlation("SPY")));

        var first = (List<string>)ScreenerComparisonSymbols.Inspect(tree).Symbols;
        first.Add("INJECTED");
        ScreenerComparisonRequirement second = ScreenerComparisonSymbols.Inspect(tree);

        Assert.Equal(new[] { "SPY" }, second.Symbols);
        Assert.Equal(new[] { "SPY" }, second.Spellings);
    }

    [Fact]
    public void TheTwoTickerTypesTheTabOffersAreExactlyTheTypesThatNameSymbols()
    {
        // The two types of the Screener catalog that compare two symbols; the type is judged by the interface, not by a list of names.
        foreach (IndicatorType type in new[] { IndicatorType.Correlation, IndicatorType.CrossCorrelation })
        {
            Assert.True(IndicatorFactory.CreateStatic(type) is ICrossTickerIndicator);
            Assert.Equal(new[] { "SPY" }, ScreenerComparisonSymbols.Inspect(Tree(Above(Cross(type, "SPY")))).Symbols);
        }

        Assert.False(IndicatorFactory.CreateStatic(IndicatorType.SMA) is ICrossTickerIndicator);
    }

    // ---- a side of a type that NEEDS a comparison symbol and names none (G-1 / G1-T5) ---------------------------------------------------

    private static IndicatorType[] TwoTickerTypes()
        => IndicatorFactory.Default.GetRegisteredTypes()
            .Where(type => IndicatorFactory.CreateStatic(type) is ICrossTickerIndicator)
            .OrderBy(type => type.ToString(), StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void ARequiredTypeWithABlankSymbolIsReported_AnOptionalTypeIsNot()
    {
        foreach (IndicatorType type in TwoTickerTypes())
        {
            bool optional = ScreenerComparisonSymbols.IsComparisonSymbolOptional(type);
            foreach (object? blank in new object?[] { null, string.Empty, "   " })
            {
                ScreenerIndicatorSideConfig side = Cross(type, blank);

                ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(side)));

                Assert.Same(optional ? null : side, requirement.SymbolRequiredSide);
                Assert.Empty(requirement.Symbols);
            }
        }

        // The two kinds exist among the registered types (the rule is not vacuous).
        Assert.Contains(TwoTickerTypes(), ScreenerComparisonSymbols.IsComparisonSymbolOptional);
        Assert.Contains(TwoTickerTypes(), type => !ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
    }

    [Fact]
    public void ANamedSymbolIsNeverReportedAsMissing()
    {
        foreach (IndicatorType type in TwoTickerTypes())
        {
            ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Cross(type, "SPY"))));

            Assert.Null(requirement.SymbolRequiredSide);
            Assert.Equal(new[] { "SPY" }, requirement.Symbols);
        }
    }

    [Fact]
    public void TheFirstRequiredSideWithoutASymbolIsReported_InTheOrderOfTheTraversal()
    {
        IndicatorType required = TwoTickerTypes().First(type => !ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
        IndicatorType otherRequired = TwoTickerTypes().Last(type => !ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
        ScreenerIndicatorSideConfig first = Cross(required, null);
        ScreenerIndicatorSideConfig second = Cross(otherRequired, null);

        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(Correlation("SPY")), Above(first), Above(second)));

        Assert.Same(first, requirement.SymbolRequiredSide);
        Assert.Equal(new[] { "SPY" }, requirement.Symbols);
    }

    [Fact]
    public void ADisabledLeafAndAnUnusedRightSideAreNotJudged()
    {
        IndicatorType required = TwoTickerTypes().First(type => !ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));

        // A disabled leaf is absent from its group.
        Assert.Null(ScreenerComparisonSymbols.Inspect(Tree(Above(Cross(required, null), enabled: false))).SymbolRequiredSide);

        // The right side is evaluated only when the entry compares against an indicator.
        ScreenerIndicatorEntry numeric = Versus(Close(), ComparisonOperator.GreaterThan, 0m);
        numeric.RightHand = Cross(required, null);
        Assert.Null(ScreenerComparisonSymbols.Inspect(Tree(numeric)).SymbolRequiredSide);

        ScreenerIndicatorSideConfig right = Cross(required, null);
        ScreenerIndicatorEntry versus = Versus(Close(), ComparisonOperator.GreaterThan, right);
        Assert.Same(right, ScreenerComparisonSymbols.Inspect(Tree(versus)).SymbolRequiredSide);
    }

    [Fact]
    public void ARequiredSideWithoutASymbolOnAnotherFrameIsReportedAsBoth_TheOffDailyFaultIsTheTabsFirstQuestion()
    {
        IndicatorType required = TwoTickerTypes().First(type => !ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
        ScreenerIndicatorSideConfig side = Cross(required, null, TimeFrame.W1);

        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(Tree(Above(side)));

        Assert.Same(side, requirement.OffDailySide);
        Assert.Same(side, requirement.SymbolRequiredSide);
    }

    [Fact]
    public void TheSidesOverloadReportsTheRequiredSideToo_AndTheOldConstructionStillCompiles()
    {
        IndicatorType required = TwoTickerTypes().First(type => !ScreenerComparisonSymbols.IsComparisonSymbolOptional(type));
        ScreenerIndicatorSideConfig side = Cross(required, null);

        Assert.Same(side, ScreenerComparisonSymbols.Inspect(new ScreenerIndicatorSideConfig?[] { null, side }).SymbolRequiredSide);

        // A requirement built with three arguments (every earlier caller) has no required side.
        var oldForm = new ScreenerComparisonRequirement(new[] { "SPY" }, new[] { "SPY" }, null);
        Assert.Null(oldForm.SymbolRequiredSide);
    }

    // ---- the sides overload (S-1: the Scatter evaluator hands over the sources of its metrics) -----------------------------------------

    [Fact]
    public void TheSidesOverload_CollectsTheSameSymbolsAsTheConditionForTheSameSides()
    {
        ScreenerIndicatorSideConfig a = Correlation("spy");
        ScreenerIndicatorSideConfig b = Cross(IndicatorType.CrossCorrelation, "7203.T");
        ScreenerIndicatorSideConfig c = Correlation("7203-T");

        ScreenerComparisonRequirement fromCondition = ScreenerComparisonSymbols.Inspect(Tree(Above(a), Above(b), Above(c)));
        ScreenerComparisonRequirement fromSides = ScreenerComparisonSymbols.Inspect(new ScreenerIndicatorSideConfig?[] { a, b, c });

        Assert.Equal(fromCondition.Symbols, fromSides.Symbols);
        Assert.Equal(fromCondition.Spellings, fromSides.Spellings);
        Assert.Equal(new[] { "SPY", "7203.T" }, fromSides.Symbols);
        Assert.Equal(new[] { "SPY", "7203.T", "7203-T" }, fromSides.Spellings);
    }

    [Fact]
    public void TheSidesOverload_IgnoresNullsBlankSymbolsAndSidesThatAreNotTwoTicker()
    {
        ScreenerComparisonRequirement requirement = ScreenerComparisonSymbols.Inspect(new ScreenerIndicatorSideConfig?[]
        {
            null,
            Correlation(null),
            Correlation("  "),
            Sma(5),
            Close(),
        });

        Assert.Empty(requirement.Symbols);
        Assert.Empty(requirement.Spellings);
        Assert.Null(requirement.OffDailySide);
    }

    [Fact]
    public void TheSidesOverload_ReportsTheFirstOffDailyTwoTickerSide_AndNullForAnEmptyList()
    {
        ScreenerIndicatorSideConfig weekly = Correlation("SPY", TimeFrame.W1);

        Assert.Same(weekly, ScreenerComparisonSymbols.Inspect(new ScreenerIndicatorSideConfig?[] { Correlation("QQQ"), weekly }).OffDailySide);
        Assert.Empty(ScreenerComparisonSymbols.Inspect(Array.Empty<ScreenerIndicatorSideConfig?>()).Symbols);
        Assert.Throws<ArgumentNullException>(() => ScreenerComparisonSymbols.Inspect((IEnumerable<ScreenerIndicatorSideConfig?>)null!));
    }

    private sealed class OtherCondition : IScreeningCondition
    {
        public bool IsMet(IReadOnlyList<CandleData> candles) => true;

        public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles, TickerMetadata metadata) => ValueTask.FromResult(true);
    }
}
