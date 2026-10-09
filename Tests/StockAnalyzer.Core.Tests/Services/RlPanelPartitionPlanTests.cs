using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Partition plans of the panel catalog on hand-computed calendars: the default plan equals the existing short-history rule, the block-count
/// geometry is end-aligned, and the minimum calendar lengths follow from the plan. Every expected number is written out in the test.
/// </summary>
public sealed class RlPanelPartitionPlanTests
{
    private static readonly int S = RlContract.SupportLength;
    private static readonly int W = RlContract.WindowLength;

    private static ReadOnlyMemory<int> Prefix(int length, Func<int, bool> present)
    {
        var prefix = new int[length + 1];
        for (int t = 0; t < length; t++) prefix[t + 1] = prefix[t] + (present(t) ? 1 : 0);
        return prefix;
    }

    private static ImmutableArray<bool> Groups(params bool[] holdout) => holdout.ToImmutableArray();

    private static RlPanelPartitionPlan Plan(RlPortfolioPartitionMode mode, int train, int validation, int test)
        => new(mode, new RlPanelBlockRequirement(train, validation, test));

    private static RlPanelPartitionPlan BlockCount(int train = 1, int validation = 1, int test = 1) => Plan(RlPortfolioPartitionMode.BlockCount, train, validation, test);
    private static RlPanelPartitionPlan Ratio(int train = 1, int validation = 1, int test = 1) => Plan(RlPortfolioPartitionMode.Ratio, train, validation, test);

    private static RlPanelCatalog Full(int length, RlPanelPartitionPlan plan, out ImmutableArray<RlPanelCellCount> shortCells, int symbols = 2)
        => RlPanelCatalogBuilder.EvaluatePlan(length, Enumerable.Range(0, symbols).Select(_ => Prefix(length, _ => true)).ToArray(),
            Groups(new bool[symbols]), RlPanelEvaluationMode.ShortHistory, plan, out shortCells);

    private static string Describe(RlPanelCatalog catalog, ImmutableArray<RlPanelCellCount> shortCells)
        => string.Join("|", new[]
        {
            $"cuts={catalog.TrainCut},{catalog.ValidationCut}",
            "train=" + string.Join(",", catalog.Train.Select(DescribeBlock)),
            "validation=" + string.Join(",", catalog.Validation.Select(DescribeBlock)),
            "oos=" + string.Join(",", catalog.Oos.Select(DescribeBlock)),
            "excluded=" + string.Join(",", catalog.Excluded),
            "cells=" + string.Join(",", catalog.Cells.Select(c => $"{c.Partition}/{c.Group}:{c.BlockCount}/{c.RequiredBlocks}")),
            "short=" + string.Join(",", shortCells.Select(c => $"{c.Partition}/{c.Group}")),
        });

    private static string DescribeBlock(RlPanelBlock b) => $"{b.Id}[{string.Join(" ", b.EligibleSymbols)}]";

    private static int[] Starts(IEnumerable<RlPanelBlock> blocks) => blocks.Select(b => b.StartIndex).ToArray();

    // ----- block requirement and plan values -----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(191, 1)]
    [InlineData(192, 1)]
    [InlineData(193, 2)]
    [InlineData(250, 2)]
    [InlineData(384, 2)]
    [InlineData(385, 3)]
    public void MinimumBarsRoundUpToWholeBlocks(int bars, int blocks)
        => Assert.Equal(blocks, RlPanelBlockRequirement.BlocksFor(bars));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveMinimumIsRejected(int bars)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPanelBlockRequirement.BlocksFor(bars));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPanelBlockRequirement.FromBars(bars, 192, 192));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RlPanelBlockRequirement(1, 0, 1));
    }

    [Fact]
    public void ThePlanOfDefaultSettingsIsTheDefaultPlan()
    {
        var plan = RlPanelPartitionPlan.FromSettings(new RlPortfolioSettings());
        Assert.True(plan.IsDefault);
        Assert.Equal(RlPortfolioPartitionMode.Ratio, plan.Mode);
        Assert.Equal(new RlPanelBlockRequirement(1, 1, 1), plan.Requirement);
        Assert.Equal(new RlPanelMinimumBars(S, S, S), plan.Bars);
        Assert.Equal(RlContract.SupportLength, RlPortfolioSettings.DefaultMinimumBars);
    }

    [Fact]
    public void ThePlanFollowsTheThreeMinimumBarsAndTheMode()
    {
        var plan = RlPanelPartitionPlan.FromSettings(new RlPortfolioSettings
        { PartitionMode = RlPortfolioPartitionMode.BlockCount, MinimumTrainBars = 500, MinimumValidationBars = 192, MinimumTestBars = 193 });
        Assert.False(plan.IsDefault);
        Assert.Equal(new RlPanelBlockRequirement(3, 1, 2), plan.Requirement);
        Assert.False(Ratio(1, 2, 1).IsDefault);
        Assert.False(BlockCount().IsDefault);
        Assert.True(Ratio().IsDefault);
    }

    // ----- default equivalence -----

    [Fact]
    public void TheDefaultPlanEqualsTheShortHistoryRuleForEveryLengthUpTo3000()
    {
        var plan = Ratio();
        for (int length = 1; length <= 3000; length++)
        {
            // Full presence of two symbols, plus a third symbol listed at index 300 and a hole in the second.
            var presence = new[] { Prefix(length, _ => true), Prefix(length, t => t != 100), Prefix(length, t => t >= 300) };
            var holdout = Groups(false, false, false);
            var expected = RlPanelCatalogBuilder.Evaluate(length, presence, holdout, RlPanelEvaluationMode.ShortHistory, out var expectedShort);
            var actual = RlPanelCatalogBuilder.EvaluatePlan(length, presence, holdout, RlPanelEvaluationMode.ShortHistory, plan, out var actualShort);
            Assert.Equal(Describe(expected, expectedShort), Describe(actual, actualShort));
        }
    }

    [Fact]
    public void TheDefaultPlanEqualsTheShortHistoryRuleWithAHoldoutGroup()
    {
        foreach (int length in new[] { 1000, 1278, 1400, 2000 })
        {
            var presence = new[] { Prefix(length, _ => true), Prefix(length, _ => true), Prefix(length, t => t >= 1000), Prefix(length, t => t >= 1000) };
            var holdout = Groups(false, false, true, true);
            var expected = RlPanelCatalogBuilder.Evaluate(length, presence, holdout, RlPanelEvaluationMode.ShortHistory, out var expectedShort);
            var actual = RlPanelCatalogBuilder.EvaluatePlan(length, presence, holdout, RlPanelEvaluationMode.ShortHistory, Ratio(), out var actualShort);
            Assert.Equal(Describe(expected, expectedShort), Describe(actual, actualShort));
        }
    }

    // ----- minimum calendar length -----

    [Theory]
    [InlineData(RlPortfolioPartitionMode.Ratio, 1, 1, 1, 1278)]
    [InlineData(RlPortfolioPartitionMode.BlockCount, 1, 1, 1, 704)]    // 192 * 3 + 2 * 64
    [InlineData(RlPortfolioPartitionMode.BlockCount, 3, 1, 1, 1088)]   // 192 * 5 + 2 * 64
    [InlineData(RlPortfolioPartitionMode.BlockCount, 2, 1, 1, 896)]    // 192 * 4 + 2 * 64
    [InlineData(RlPortfolioPartitionMode.BlockCount, 1, 2, 3, 1280)]  // 192 * (1 + 2 + 3) + 2 * 64
    public void MinimumCalendarLengthIsFoundFromThePlan(RlPortfolioPartitionMode mode, int train, int validation, int test, int expected)
    {
        int actual = RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, Plan(mode, train, validation, test));
        Assert.Equal(expected, actual);
        if (mode == RlPortfolioPartitionMode.BlockCount) Assert.Equal(S * (train + validation + test) + 2 * W, actual);
    }

    [Fact]
    public void TheBlockCountMinimumDoesNotNeedTheRatioMinimum()
    {
        Assert.True(RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, BlockCount())
            < RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory));
    }

    /// <summary>Independent derivation of the ratio rule: a window plus a block must fit each of the two fifths after the first, and one block the first three.</summary>
    private static int RatioBruteForce(int train, int validation, int test)
    {
        for (int length = 1; ; length++)
        {
            int cut1 = 3 * length / 5, cut2 = 4 * length / 5;
            int trainBlocks = cut1 / S;
            int validationBlocks = Math.Max(0, (cut2 - (cut1 + W)) / S);
            int testBlocks = Math.Max(0, (length - (cut2 + W)) / S);
            if (trainBlocks >= train && validationBlocks >= validation && testBlocks >= test) return length;
        }
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(3, 2, 1)]
    [InlineData(2, 1, 4)]
    public void TheRatioMinimumEqualsAnIndependentBruteForce(int train, int validation, int test)
        => Assert.Equal(RatioBruteForce(train, validation, test),
            RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, Ratio(train, validation, test)));

    [Theory]
    [InlineData(RlPortfolioPartitionMode.Ratio)]
    [InlineData(RlPortfolioPartitionMode.BlockCount)]
    public void TheMinimumGrowsWithEveryMember(RlPortfolioPartitionMode mode)
    {
        int Minimum(int t, int v, int o) => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, Plan(mode, t, v, o));
        foreach (var (t, v, o) in new[] { (1, 1, 1), (2, 1, 1), (1, 3, 2), (2, 2, 2) })
        {
            int baseline = Minimum(t, v, o);
            Assert.True(Minimum(t + 1, v, o) >= baseline);
            Assert.True(Minimum(t, v + 1, o) >= baseline);
            Assert.True(Minimum(t, v, o + 1) >= baseline);
        }
    }

    [Fact]
    public void ExactlyTheMinimumSatisfiesEveryTrainingCellAndOneLessDoesNot()
    {
        foreach (var plan in new[] { Ratio(), Ratio(1, 2, 2), BlockCount(), BlockCount(3, 1, 1), BlockCount(2, 2, 2) })
        {
            int minimum = RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, plan);
            Full(minimum, plan, out var atMinimum);
            Assert.Empty(atMinimum);
            Full(minimum - 1, plan, out var below);
            Assert.NotEmpty(below);
        }
    }

    // ----- the minimum is found by a binary search; a linear scan of the same engine call is the oracle -----

    /// <summary>Calendar lengths of the oracle never exceed this; the linear scan fails loudly instead of running on when the engine never becomes feasible.</summary>
    private const int OracleBound = 400_000;
    private static readonly int[] s_identity = Enumerable.Range(0, OracleBound + 1).ToArray();

    private static bool FeasibleAt(int length, RlPanelPartitionPlan plan)
    {
        var prefix = new ReadOnlyMemory<int>(s_identity, 0, length + 1);
        _ = RlPanelCatalogBuilder.EvaluatePlan(length, new[] { prefix, prefix }, Groups(false, false), RlPanelEvaluationMode.ShortHistory, plan, out var shortCells);
        return shortCells.IsEmpty;
    }

    /// <summary>The first length, from 1 upward, at which two fully present symbols satisfy every training cell (the definition the search must reproduce).</summary>
    private static int LinearMinimum(RlPanelPartitionPlan plan)
    {
        for (int length = 1; length <= OracleBound; length++)
            if (FeasibleAt(length, plan)) return length;
        throw new InvalidOperationException("The oracle bound was reached.");
    }

    private static int SearchedMinimum(RlPanelPartitionPlan plan) => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, plan);

    [Theory]
    [InlineData(RlPortfolioPartitionMode.Ratio)]
    [InlineData(RlPortfolioPartitionMode.BlockCount)]
    public void TheSearchedMinimumEqualsALinearScanForEveryCombinationOfSmallBlockCounts(RlPortfolioPartitionMode mode)
    {
        int[] counts = { 1, 2, 3, 5, 10 };
        foreach (int train in counts)
            foreach (int validation in counts)
                foreach (int test in counts)
                {
                    var plan = Plan(mode, train, validation, test);
                    Assert.Equal(LinearMinimum(plan), SearchedMinimum(plan));
                }
    }

    [Theory]
    [InlineData(RlPortfolioPartitionMode.Ratio, 80, 80, 80)]
    [InlineData(RlPortfolioPartitionMode.BlockCount, 80, 80, 80)]
    [InlineData(RlPortfolioPartitionMode.Ratio, 80, 1, 1)]
    [InlineData(RlPortfolioPartitionMode.BlockCount, 1, 80, 1)]
    [InlineData(RlPortfolioPartitionMode.Ratio, 1, 1, 80)]
    public void TheSearchedMinimumEqualsALinearScanAtTheLargestBlockCount(RlPortfolioPartitionMode mode, int train, int validation, int test)
    {
        var plan = Plan(mode, train, validation, test);
        int searched = SearchedMinimum(plan);                                   // terminates; no time threshold
        Assert.Equal(LinearMinimum(plan), searched);
    }

    [Fact]
    public void FeasibilityIsMonotoneInTheCalendarLengthSoTheBinarySearchIsSound()
    {
        foreach (var plan in new[] { Ratio(), Ratio(1, 2, 2), Ratio(3, 1, 2), BlockCount(), BlockCount(3, 1, 1), BlockCount(2, 2, 2), BlockCount(1, 2, 3) })
        {
            int minimum = SearchedMinimum(plan);
            for (int length = 1; length <= minimum + 2 * S; length++)
                Assert.Equal(length >= minimum, FeasibleAt(length, plan));      // infeasible below the minimum, feasible at and above it
        }
    }

    [Fact]
    public void TheStandardModeMinimumKeepsItsKnownValue()
        => Assert.Equal(5118, RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.Standard));

    // ----- the window gap after a cut is an embargo, not what keeps a window in its block -----

    [Theory]
    [InlineData(RlPortfolioPartitionMode.Ratio)]
    [InlineData(RlPortfolioPartitionMode.BlockCount)]
    public void EveryAnchorWindowAndFillOfATileLiesInsideItsOwnTileAndItsPartitionForEveryLengthUpTo3000(RlPortfolioPartitionMode mode)
    {
        var plan = Plan(mode, 1, 1, 1);
        for (int length = 1; length <= 3000; length++)
        {
            var prefix = new ReadOnlyMemory<int>(s_identity, 0, length + 1);
            var catalog = RlPanelCatalogBuilder.EvaluatePlan(length, new[] { prefix, prefix }, Groups(false, false), RlPanelEvaluationMode.ShortHistory, plan, out var shortCells);
            // The cuts are floored at zero for a calendar too short to hold the training cell: the partition bounds mean something only when the rule holds.
            bool ruleHolds = shortCells.IsEmpty;
            void Check(IEnumerable<RlPanelBlock> blocks, int partitionStart, int partitionEnd)
            {
                foreach (var b in blocks)
                {
                    int firstAnchor = b.StartIndex + W - 1, lastAnchor = firstAnchor + RlContract.EpisodeHorizon - 1;
                    bool inside = b.EndExclusive - b.StartIndex == S && (!ruleHolds || (b.StartIndex >= partitionStart && b.EndExclusive <= partitionEnd))
                        && firstAnchor - W + 1 == b.StartIndex                          // the first window starts at the tile start
                        && lastAnchor + 1 == b.EndExclusive - 1;                        // the last fill is the last bar of the tile
                    Assert.True(inside, $"{b.Id} at length {length}");
                }
            }
            Check(catalog.Train, 0, catalog.TrainCut);
            Check(catalog.Validation, catalog.TrainCut + W, catalog.ValidationCut);
            Check(catalog.Oos, catalog.ValidationCut + W, length);
        }
    }

    // ----- block-count geometry -----

    [Theory]
    // length, plan, cut1, cut2, train starts, validation starts, oos starts
    [InlineData(704, 1, 1, 1, 192, 448, new[] { 0 }, new[] { 256 }, new[] { 512 })]
    [InlineData(705, 1, 1, 1, 193, 449, new[] { 1 }, new[] { 257 }, new[] { 513 })]
    [InlineData(896, 2, 1, 1, 384, 640, new[] { 0, 192 }, new[] { 448 }, new[] { 704 })]
    [InlineData(1088, 3, 1, 1, 576, 832, new[] { 0, 192, 384 }, new[] { 640 }, new[] { 896 })]
    [InlineData(1218, 1, 1, 1, 706, 962, new[] { 514 }, new[] { 770 }, new[] { 1026 })]
    [InlineData(1500, 1, 2, 1, 796, 1244, new[] { 604 }, new[] { 860, 1052 }, new[] { 1308 })]
    public void TheBlockCountTilesAreEndAlignedAtTheLatestDate(int length, int train, int validation, int test, int cut1, int cut2,
        int[] trainStarts, int[] validationStarts, int[] oosStarts)
    {
        var catalog = Full(length, BlockCount(train, validation, test), out var shortCells);
        Assert.Empty(shortCells);
        Assert.Equal((cut1, cut2), (catalog.TrainCut, catalog.ValidationCut));
        Assert.Equal(trainStarts, Starts(catalog.Train));
        Assert.Equal(validationStarts, Starts(catalog.Validation));
        Assert.Equal(oosStarts, Starts(catalog.Oos));
        Assert.All(catalog.Train.Concat(catalog.Validation).Concat(catalog.Oos), b => Assert.Equal(S, b.EndExclusive - b.StartIndex));
    }

    [Fact]
    public void TheBlockIdsNameThePartitionAndTheRange()
    {
        var catalog = Full(704, BlockCount(), out _);
        Assert.Equal("train:0:192", catalog.Train[0].Id);
        Assert.Equal("validation:256:448", catalog.Validation[0].Id);
        Assert.Equal("oos:512:704", catalog.Oos[0].Id);
    }

    [Fact]
    public void OneBarBelowTheBlockCountMinimumLosesTheTrainBlockOnly()
    {
        // L = 703: cut2 = 447, cut1 = 191, so the train block would start at -1; validation 255..447 and oos 511..703 still exist.
        var catalog = Full(703, BlockCount(), out var shortCells);
        Assert.Equal((191, 447), (catalog.TrainCut, catalog.ValidationCut));
        Assert.Empty(catalog.Train);
        Assert.Equal(new[] { 255 }, Starts(catalog.Validation));
        Assert.Equal(new[] { 511 }, Starts(catalog.Oos));
        var cell = Assert.Single(shortCells);
        Assert.Equal(("train", RlPanelSymbolGroup.Train, 0, 1), (cell.Partition, cell.Group, cell.BlockCount, cell.RequiredBlocks));
    }

    [Fact]
    public void AVeryShortCalendarNeverCreatesNegativeIndicesAndStoresNonNegativeCuts()
    {
        foreach (int length in new[] { 1, 2, 191, 192, 193, 255, 256, 447, 448 })
        {
            var catalog = Full(length, BlockCount(3, 2, 2), out var shortCells);
            Assert.NotEmpty(shortCells);
            Assert.True(catalog.TrainCut >= 0 && catalog.ValidationCut >= 0);
            Assert.All(catalog.Train.Concat(catalog.Validation).Concat(catalog.Oos), b => Assert.InRange(b.StartIndex, 0, length - S));
        }
    }

    [Fact]
    public void ALongerCalendarUsesTheLatestBlocksAndLeavesOlderBarsUnused()
    {
        // L = 2000, 1/1/1: cut2 = 1744, cut1 = 1488; train 1296..1488, validation 1552..1744, oos 1808..2000. Nothing before 1296 is a block.
        var catalog = Full(2000, BlockCount(), out var shortCells);
        Assert.Empty(shortCells);
        Assert.Equal(new[] { 1296 }, Starts(catalog.Train));
        Assert.Equal(new[] { 1552 }, Starts(catalog.Validation));
        Assert.Equal(new[] { 1808 }, Starts(catalog.Oos));
    }

    [Fact]
    public void ABlockWithFewerThanTwoEligibleSymbolsIsDroppedAndNamedExcluded()
    {
        // L = 704, symbol 1 is listed at 100, so the train block 0..192 has one eligible symbol while the later blocks have both.
        var presence = new[] { Prefix(704, _ => true), Prefix(704, t => t >= 100) };
        var catalog = RlPanelCatalogBuilder.EvaluatePlan(704, presence, Groups(false, false), RlPanelEvaluationMode.ShortHistory, BlockCount(), out var shortCells);
        Assert.Empty(catalog.Train);
        Assert.Equal(new[] { "train:0:192:InsufficientAssets" }, catalog.Excluded.ToArray());
        Assert.Equal("train", Assert.Single(shortCells).Partition);
    }

    [Fact]
    public void AHoldoutCellBelowItsMinimumIsNotEvaluableAndNeverAShortfall()
    {
        // L = 1100, 1/1/1: cut2 = 844, cut1 = 588; validation block 652..844, oos block 908..1100. The holdout symbols are listed at 900, so
        // the validation block holds only the training symbols while the oos block holds all four.
        int length = 1100;
        var presence = new[] { Prefix(length, _ => true), Prefix(length, _ => true), Prefix(length, t => t >= 900), Prefix(length, t => t >= 900) };
        var catalog = RlPanelCatalogBuilder.EvaluatePlan(length, presence, Groups(false, false, true, true), RlPanelEvaluationMode.ShortHistory,
            BlockCount(), out var shortCells);
        var cells = catalog.Cells.ToDictionary(c => (c.Partition, c.Group));
        Assert.Equal(5, cells.Count);
        Assert.Empty(shortCells);
        Assert.False(cells[("validation", RlPanelSymbolGroup.Holdout)].Evaluable);
        Assert.True(cells[("oos", RlPanelSymbolGroup.Holdout)].Evaluable);
        Assert.All(new[] { "train", "validation", "oos" }, p => Assert.True(cells[(p, RlPanelSymbolGroup.Train)].Evaluable));
    }

    [Fact]
    public void TheCellsCarryThePlanBlockMinimums()
    {
        var catalog = Full(2000, BlockCount(2, 3, 4), out _);
        Assert.Equal(new[] { ("train", 2), ("validation", 3), ("oos", 4) }, catalog.Cells.Select(c => (c.Partition, c.RequiredBlocks)).ToArray());
        var ratio = Full(5000, Ratio(2, 3, 4), out _);
        Assert.Equal(new[] { ("train", 2), ("validation", 3), ("oos", 4) }, ratio.Cells.Select(c => (c.Partition, c.RequiredBlocks)).ToArray());
    }

    // ----- disjointness -----

    [Theory]
    [InlineData(RlPortfolioPartitionMode.Ratio, 1, 1, 1)]
    [InlineData(RlPortfolioPartitionMode.Ratio, 1, 2, 2)]
    [InlineData(RlPortfolioPartitionMode.BlockCount, 1, 1, 1)]
    [InlineData(RlPortfolioPartitionMode.BlockCount, 3, 2, 2)]
    public void PartitionsAreDisjointAndNoWindowCrossesACutForEveryLengthUpTo3000(RlPortfolioPartitionMode mode, int train, int validation, int test)
    {
        var plan = Plan(mode, train, validation, test);
        for (int length = 1; length <= 3000; length++)
        {
            var catalog = Full(length, plan, out _);
            int cut1 = mode == RlPortfolioPartitionMode.Ratio ? 3 * length / 5 : length - W - S * test - W - S * validation;
            int cut2 = mode == RlPortfolioPartitionMode.Ratio ? 4 * length / 5 : length - W - S * test;
            Assert.All(catalog.Train, b => Assert.True(b.EndExclusive <= cut1, $"train {b.Id} at length {length}"));
            Assert.All(catalog.Validation, b => Assert.True(b.StartIndex >= cut1 + W && b.EndExclusive <= cut2, $"validation {b.Id} at length {length}"));
            Assert.All(catalog.Oos, b => Assert.True(b.StartIndex >= cut2 + W && b.EndExclusive <= length, $"oos {b.Id} at length {length}"));
            var all = catalog.Train.Concat(catalog.Validation).Concat(catalog.Oos).OrderBy(b => b.StartIndex).ToArray();
            for (int i = 1; i < all.Length; i++) Assert.True(all[i - 1].EndExclusive <= all[i].StartIndex, $"overlap at length {length}");
        }
    }

    // ----- argument rules -----

    [Fact]
    public void APlanAppliesToTheShortHistoryEvaluationOnly()
    {
        var presence = new[] { Prefix(1278, _ => true), Prefix(1278, _ => true) };
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.EvaluatePlan(1278, presence, Groups(false, false), RlPanelEvaluationMode.Standard, Ratio(), out _));
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.BuildPlan(1278, presence, Groups(false, false), RlPanelEvaluationMode.Standard, BlockCount()));
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.Standard, Ratio()));
        Assert.Throws<ArgumentNullException>(() => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, null!));
        Assert.Throws<ArgumentNullException>(() => RlPanelCatalogBuilder.EvaluatePlan(1278, presence, Groups(false, false), RlPanelEvaluationMode.ShortHistory, null!, out _));
    }

    [Fact]
    public void InputShapesAreValidatedForAPlanToo()
    {
        var ok = Prefix(704, _ => true);
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPanelCatalogBuilder.BuildPlan(0, new[] { ok, ok }, Groups(false, false), RlPanelEvaluationMode.ShortHistory, BlockCount()));
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.BuildPlan(704, new[] { ok, ok }, Groups(false), RlPanelEvaluationMode.ShortHistory, BlockCount()));
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.BuildPlan(704, new[] { ok, Prefix(703, _ => true) }, Groups(false, false), RlPanelEvaluationMode.ShortHistory, BlockCount()));
    }

    [Fact]
    public void BuildPlanThrowsInsufficientDataLikeBuild()
    {
        var presence = new[] { Prefix(703, _ => true), Prefix(703, _ => true) };
        var error = Assert.Throws<InvalidDataException>(() => RlPanelCatalogBuilder.BuildPlan(703, presence, Groups(false, false), RlPanelEvaluationMode.ShortHistory, BlockCount()));
        Assert.True(RlSourceContract.IsInsufficientData(error));
        Assert.Contains("train/Train has 0 of 1 blocks", error.Message);
        Assert.Contains("2 symbols", error.Message);
        var ok = RlPanelCatalogBuilder.BuildPlan(704, new[] { Prefix(704, _ => true), Prefix(704, _ => true) }, Groups(false, false), RlPanelEvaluationMode.ShortHistory, BlockCount());
        Assert.Single(ok.Train);
    }

    // ----- the screenshot case: four symbols of 1218 bars -----

    [Fact]
    public void FourSymbolsOf1218BarsAreInfeasibleByRatioAndFeasibleByBlockCount()
    {
        var presence = Enumerable.Range(0, 4).Select(_ => Prefix(1218, _ => true)).ToArray();
        var holdout = Groups(false, false, false, false);
        _ = RlPanelCatalogBuilder.EvaluatePlan(1218, presence, holdout, RlPanelEvaluationMode.ShortHistory, Ratio(), out var ratioShort);
        Assert.NotEmpty(ratioShort);
        var catalog = RlPanelCatalogBuilder.EvaluatePlan(1218, presence, holdout, RlPanelEvaluationMode.ShortHistory, BlockCount(), out var blockShort);
        Assert.Empty(blockShort);
        Assert.Equal("train:514:706", catalog.Train[0].Id);
        Assert.Equal("validation:770:962", catalog.Validation[0].Id);
        Assert.Equal("oos:1026:1218", catalog.Oos[0].Id);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(RlPanelSymbolRole.Trains, RlPanelSymbolRoles.Of(catalog, i)));
    }

    // ----- symbol roles -----

    /// <summary>Two symbols present on the whole calendar and one that is present only on the last <paramref name="bars"/> indices.</summary>
    private static RlPanelSymbolRole RoleOfLateSymbol(int length, int bars, RlPanelPartitionPlan plan)
    {
        var presence = new[] { Prefix(length, _ => true), Prefix(length, _ => true), Prefix(length, t => t >= length - bars) };
        var catalog = RlPanelCatalogBuilder.EvaluatePlan(length, presence, Groups(false, false, false), RlPanelEvaluationMode.ShortHistory, plan, out var shortCells);
        Assert.Empty(shortCells);
        return RlPanelSymbolRoles.Of(catalog, 2);
    }

    [Theory]
    [InlineData(704, RlPanelSymbolRole.Trains)]        // S*(T+V+O) + 2*W
    [InlineData(703, RlPanelSymbolRole.EvaluatedOnly)]
    [InlineData(500, RlPanelSymbolRole.EvaluatedOnly)]
    [InlineData(448, RlPanelSymbolRole.EvaluatedOnly)] // S*(V+O) + W: validation and test
    [InlineData(447, RlPanelSymbolRole.EvaluatedOnly)] // test only
    [InlineData(300, RlPanelSymbolRole.EvaluatedOnly)]
    [InlineData(192, RlPanelSymbolRole.EvaluatedOnly)] // S*O
    [InlineData(191, RlPanelSymbolRole.None)]
    [InlineData(150, RlPanelSymbolRole.None)]
    public void ABlockCountRoleFollowsTheBarsFromTheLatestDate(int bars, RlPanelSymbolRole expected)
        => Assert.Equal(expected, RoleOfLateSymbol(1500, bars, BlockCount()));

    [Fact]
    public void TheTiersAreDerivedFromThePlanNotTyped()
    {
        // The role is "eligible in at least one train block", so the latest train block decides: S * (1 + V + O) + 2 * W bars, whatever T is.
        var plan = BlockCount(3, 2, 1);
        int trains = S * (1 + 2 + 1) + 2 * W;
        Assert.Equal(RlPanelSymbolRole.Trains, RoleOfLateSymbol(2000, trains, plan));
        Assert.Equal(RlPanelSymbolRole.EvaluatedOnly, RoleOfLateSymbol(2000, trains - 1, plan));
        Assert.Equal(RlPanelSymbolRole.None, RoleOfLateSymbol(2000, S - 1, plan));
    }

    [Fact]
    public void ARatioSymbolPresentOnlyInTheLastFortyPercentIsEvaluatedNeverTrained()
    {
        // L = 1400: cut1 = 840, so a symbol listed at index 840 has no complete train block, but the validation and oos blocks hold it.
        int length = 1400;
        var presence = new[] { Prefix(length, _ => true), Prefix(length, _ => true), Prefix(length, t => t >= 840) };
        var catalog = RlPanelCatalogBuilder.EvaluatePlan(length, presence, Groups(false, false, false), RlPanelEvaluationMode.ShortHistory, Ratio(), out var shortCells);
        Assert.Empty(shortCells);
        Assert.Equal(RlPanelSymbolRole.EvaluatedOnly, RlPanelSymbolRoles.Of(catalog, 2));
        Assert.Equal(RlPanelSymbolRole.Trains, RlPanelSymbolRoles.Of(catalog, 0));
    }

    [Fact]
    public void RoleArgumentsAreChecked()
    {
        var catalog = Full(704, BlockCount(), out _);
        Assert.Throws<ArgumentNullException>(() => RlPanelSymbolRoles.Of(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPanelSymbolRoles.Of(catalog, -1));
        Assert.Equal(RlPanelSymbolRole.None, RlPanelSymbolRoles.Of(catalog, 7));
    }

    [Fact]
    public void TheRequirementOfAModeIsThePinnedMinimum()
    {
        Assert.Equal(new RlPanelBlockRequirement(1, 1, 1), RlPanelBlockRequirement.Of(RlPanelEvaluationMode.ShortHistory));
        Assert.Equal(new RlPanelBlockRequirement(1, 5, 5), RlPanelBlockRequirement.Of(RlPanelEvaluationMode.Standard));
        Assert.Equal(RlContract.EvaluationEpisodeCount, RlPanelBlockRequirement.Standard.TestBlocks);
    }

    [Theory]
    [InlineData(1, 80)]     // 10240 transitions / (1 block * 128 decisions)
    [InlineData(3, 26)]     // 10240 / 384 = 26.67
    [InlineData(80, 1)]     // 10240 / 10240
    [InlineData(81, 0)]     // the budget cannot visit every block once
    public void ABlockCountPlanReplaysEachTrainBlockByTheBudgetOverTheBlocks(int trainBlocks, int replays)
        => Assert.Equal(replays, BlockCount(trainBlocks).TrainingBlockReplays);

    [Fact]
    public void PlanCutsEqualTheCutsOfTheCatalog()
    {
        foreach (var plan in new[] { Ratio(), Ratio(1, 2, 2), BlockCount(), BlockCount(3, 2, 2) })
            foreach (int length in new[] { 50, 703, 704, 1278, 2000 })
            {
                var catalog = Full(length, plan, out _);
                Assert.Equal((catalog.TrainCut, catalog.ValidationCut), RlPanelCatalogBuilder.PlanCuts(length, plan));
            }
    }
}
