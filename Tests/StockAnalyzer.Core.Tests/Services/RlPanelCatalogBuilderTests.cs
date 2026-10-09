using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Panel catalog rule on hand-computed calendars (cuts, block starts and masks are written out in each test).</summary>
public sealed class RlPanelCatalogBuilderTests
{
    private static ReadOnlyMemory<int> Prefix(int length, Func<int, bool> present)
    {
        var prefix = new int[length + 1];
        for (int t = 0; t < length; t++) prefix[t + 1] = prefix[t] + (present(t) ? 1 : 0);
        return prefix;
    }

    private static ImmutableArray<bool> Groups(params bool[] holdout) => holdout.ToImmutableArray();

    private static RlPanelCatalog Full(int length, RlPanelEvaluationMode mode, int symbols = 2)
        => RlPanelCatalogBuilder.Build(length, Enumerable.Range(0, symbols).Select(_ => Prefix(length, _ => true)).ToArray(),
            Groups(new bool[symbols]), mode);

    [Theory]
    [InlineData(RlPanelEvaluationMode.ShortHistory, 1278)]
    [InlineData(RlPanelEvaluationMode.Standard, 5118)]
    public void MinimumCalendarLengthIsFoundByCallingTheCatalogRule(RlPanelEvaluationMode mode, int expected)
        => Assert.Equal(expected, RlPanelCatalogBuilder.MinimumCalendarLength(mode));

    [Fact]
    public void ShortHistoryBoundaryHandTable()
    {
        // T = 1278: cut1 = floor(3*1278/5) = 766, cut2 = floor(4*1278/5) = 1022.
        var catalog = Full(1278, RlPanelEvaluationMode.ShortHistory);
        Assert.Equal((766, 1022), (catalog.TrainCut, catalog.ValidationCut));
        Assert.Equal(new[] { 0, 192, 384 }, catalog.Train.Select(b => b.StartIndex).ToArray());
        var validation = Assert.Single(catalog.Validation);
        Assert.Equal(("validation:830:1022", 830, 1022), (validation.Id, validation.StartIndex, validation.EndExclusive));
        var oos = Assert.Single(catalog.Oos);
        Assert.Equal(("oos:1086:1278", 1086, 1278), (oos.Id, oos.StartIndex, oos.EndExclusive));
        Assert.Equal(new[] { 0, 1 }, validation.EligibleSymbols.ToArray());
        Assert.Empty(catalog.Excluded);

        // T = 1277: cut2 = floor(4*1277/5) = 1021, so the validation block 830..1022 no longer fits.
        var error = Assert.Throws<InvalidDataException>(() => Full(1277, RlPanelEvaluationMode.ShortHistory));
        Assert.True(RlSourceContract.IsInsufficientData(error));
        Assert.Contains("validation/Train has 0 of 1 blocks", error.Message);
    }

    [Fact]
    public void StandardBoundaryHandTable()
    {
        // T = 5118: cut1 = 3070, cut2 = 4094; validation 3134 + 192k (k < 5), OOS 4158 + 192k ending exactly at 5118.
        var catalog = Full(5118, RlPanelEvaluationMode.Standard);
        Assert.Equal((3070, 4094), (catalog.TrainCut, catalog.ValidationCut));
        Assert.Equal(new[] { 3134, 3326, 3518, 3710, 3902 }, catalog.Validation.Select(b => b.StartIndex).ToArray());
        Assert.Equal(new[] { 4158, 4350, 4542, 4734, 4926 }, catalog.Oos.Select(b => b.StartIndex).ToArray());
        // T = 5117: cut2 = 4093 drops the fifth validation block (3902 + 192 = 4094).
        var error = Assert.Throws<InvalidDataException>(() => Full(5117, RlPanelEvaluationMode.Standard));
        Assert.Contains("validation/Train has 4 of 5 blocks", error.Message);
    }

    [Fact]
    public void ListedSymbolsMidHistoryGetBlockLevelMasksAndExcludedBlocks()
    {
        // T = 1400: cut1 = 840, cut2 = 1120. Symbol 1 is listed at index 300, so train blocks 0..192 and 192..384 have one eligible symbol.
        int length = 1400;
        var catalog = RlPanelCatalogBuilder.Build(length, new[] { Prefix(length, _ => true), Prefix(length, t => t >= 300) },
            Groups(false, false), RlPanelEvaluationMode.ShortHistory);
        Assert.Equal(new[] { "train:0:192:InsufficientAssets", "train:192:384:InsufficientAssets" }, catalog.Excluded.ToArray());
        Assert.Equal(new[] { 384, 576 }, catalog.Train.Select(b => b.StartIndex).ToArray());
        Assert.All(catalog.Train.Concat(catalog.Validation).Concat(catalog.Oos), b => Assert.Equal(new[] { 0, 1 }, b.EligibleSymbols.ToArray()));
        Assert.Equal(new[] { 904 }, catalog.Validation.Select(b => b.StartIndex).ToArray());
        Assert.Equal(new[] { 1184 }, catalog.Oos.Select(b => b.StartIndex).ToArray());
    }

    [Fact]
    public void AMissingBarInsideABlockMakesTheSymbolIneligibleForThatBlockOnly()
    {
        // Symbol 1 lacks index 100 only. Train block 0..192 then has one eligible symbol (excluded); the next block 192..384 is complete again.
        int length = 1400;
        var catalog = RlPanelCatalogBuilder.Build(length, new[] { Prefix(length, _ => true), Prefix(length, t => t != 100), Prefix(length, _ => true) },
            Groups(false, false, false), RlPanelEvaluationMode.ShortHistory);
        Assert.Empty(catalog.Excluded);
        Assert.Equal(new[] { 0, 2 }, catalog.Train[0].EligibleSymbols.ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, catalog.Train[1].EligibleSymbols.ToArray());
    }

    [Fact]
    public void HoldoutCellsBelowTheirMinimumAreNotEvaluableAndNeverDropped()
    {
        // T = 1278, four symbols; the two holdout symbols (2, 3) are listed at index 1000, so the validation block 830..1022 has only the
        // training symbols eligible while the OOS block 1086..1278 has all four.
        int length = 1278;
        var presence = new[] { Prefix(length, _ => true), Prefix(length, _ => true), Prefix(length, t => t >= 1000), Prefix(length, t => t >= 1000) };
        var catalog = RlPanelCatalogBuilder.Build(length, presence, Groups(false, false, true, true), RlPanelEvaluationMode.ShortHistory);
        var cells = catalog.Cells.ToDictionary(c => (c.Partition, c.Group));
        Assert.Equal(5, cells.Count);
        Assert.True(cells[("train", RlPanelSymbolGroup.Train)].Evaluable);
        Assert.True(cells[("validation", RlPanelSymbolGroup.Train)].Evaluable);
        Assert.True(cells[("oos", RlPanelSymbolGroup.Train)].Evaluable);
        Assert.Equal(0, cells[("validation", RlPanelSymbolGroup.Holdout)].BlockCount);
        Assert.False(cells[("validation", RlPanelSymbolGroup.Holdout)].Evaluable);
        Assert.Equal(1, cells[("oos", RlPanelSymbolGroup.Holdout)].BlockCount);
        Assert.True(cells[("oos", RlPanelSymbolGroup.Holdout)].Evaluable);
        // The validation block itself is still listed (two training symbols are eligible).
        Assert.Single(catalog.Validation);
        Assert.Equal(new[] { 0, 1 }, catalog.EligibleIn(catalog.Validation[0], RlPanelSymbolGroup.Train).ToArray());
        Assert.Empty(catalog.EligibleIn(catalog.Validation[0], RlPanelSymbolGroup.Holdout));
        Assert.Equal(new[] { 2, 3 }, catalog.EligibleIn(catalog.Oos[0], RlPanelSymbolGroup.Holdout).ToArray());
    }

    [Fact]
    public void ATrainingCellBelowItsMinimumIsInsufficientDataWithTheShortCellNamed()
    {
        // Training symbols 0 and 1 exist, but symbol 1 is missing from the whole validation block: only symbol 0 of the group is eligible there.
        int length = 1278;
        var presence = new[] { Prefix(length, _ => true), Prefix(length, t => t < 830 || t >= 1022), Prefix(length, _ => true), Prefix(length, _ => true) };
        var error = Assert.Throws<InvalidDataException>(() => RlPanelCatalogBuilder.Build(length, presence,
            Groups(false, false, true, true), RlPanelEvaluationMode.ShortHistory));
        Assert.True(RlSourceContract.IsInsufficientData(error));
        Assert.Contains("validation/Train has 0 of 1 blocks", error.Message);
        Assert.Contains("4 symbols", error.Message);
    }

    [Fact]
    public void EvaluateReportsTheShortfallWithoutThrowing()
    {
        int length = 1277;
        var catalog = RlPanelCatalogBuilder.Evaluate(length, new[] { Prefix(length, _ => true), Prefix(length, _ => true) }, Groups(false, false),
            RlPanelEvaluationMode.ShortHistory, out var shortCells);
        // cut2 = 1021: no validation block fits (830 + 192 = 1022), while the OOS block 1085..1277 still does.
        var cell = Assert.Single(shortCells);
        Assert.Equal(("validation", RlPanelSymbolGroup.Train, 0, 1), (cell.Partition, cell.Group, cell.BlockCount, cell.RequiredBlocks));
        Assert.Empty(catalog.Validation);
        Assert.Equal(new[] { 1085 }, catalog.Oos.Select(b => b.StartIndex).ToArray());
    }

    [Fact]
    public void NoHoldoutMeansNoHoldoutCells()
        => Assert.Equal(3, Full(1278, RlPanelEvaluationMode.ShortHistory).Cells.Length);

    [Fact]
    public void InputShapesAreValidated()
    {
        var ok = Prefix(1278, _ => true);
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPanelCatalogBuilder.Build(0, new[] { ok, ok }, Groups(false, false), RlPanelEvaluationMode.Standard));
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.Build(1278, new[] { ok, ok }, Groups(false), RlPanelEvaluationMode.Standard));
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.Build(1278, new[] { ok, Prefix(1277, _ => true) }, Groups(false, false), RlPanelEvaluationMode.Standard));
        Assert.Throws<ArgumentException>(() => RlPanelCatalogBuilder.Build(1278, new[] { ok, ok }, default, RlPanelEvaluationMode.Standard));
        Assert.Throws<ArgumentException>(() => Full(1278, RlPanelEvaluationMode.ShortHistory).Partition("other"));
    }
}
