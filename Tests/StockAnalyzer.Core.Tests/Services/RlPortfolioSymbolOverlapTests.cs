#nullable enable
using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The symbol-overlap report of a transfer, on hand-written panel manifests: the base training group is the base symbols minus its held-out symbols,
/// the comparison is ordinal (case-sensitive), and the counts are an upper bound (symbols the base only evaluated are included).
/// </summary>
public sealed class RlPortfolioSymbolOverlapTests
{
    private static RlPanelManifest Manifest(string[] symbols, params string[] heldOut)
        => new(1, "spec", symbols.Select(s => new RlPanelSymbolEntry(s, new string('a', 64), 10, new DateOnly(2000, 1, 1), new DateOnly(2000, 1, 10))).ToImmutableArray(),
            "USD", "UTC", new DateOnly(2001, 1, 1), new DateOnly(2000, 1, 1), new DateOnly(2000, 2, 1), new string('b', 64), 10, new string('c', 64), 1u,
            heldOut.Length > 0, heldOut.Length > 0 ? 20 : 0, heldOut.ToImmutableArray(), new string('d', 64));

    private static string[] Names(ImmutableArray<string> symbols) => symbols.ToArray();

    [Fact]
    public void ASymbolHeldOutInTheTargetThatTheBaseTrainedOnIsCounted()
    {
        var overlap = RlPortfolioSymbolOverlap.Of(Manifest(new[] { "A", "B", "C" }), Manifest(new[] { "A", "C", "D" }, "C", "D"));
        Assert.Equal(new[] { "A", "B", "C" }, Names(overlap.BaseTrainingGroupSymbols));
        Assert.Equal(new[] { "C", "D" }, Names(overlap.TargetHeldOutSymbols));
        Assert.Equal(new[] { "C" }, Names(overlap.HeldOutSeenByBase));                                  // 1 of 2
        Assert.Equal(new[] { "A", "C" }, Names(overlap.TargetSymbolsSeenByBase));
    }

    [Fact]
    public void ASymbolTheBaseHeldOutItselfIsNotInItsTrainingGroup()
    {
        var overlap = RlPortfolioSymbolOverlap.Of(Manifest(new[] { "A", "B", "C" }, "C"), Manifest(new[] { "A", "B", "C" }, "C"));
        Assert.Equal(new[] { "A", "B" }, Names(overlap.BaseTrainingGroupSymbols));
        Assert.Empty(overlap.HeldOutSeenByBase);                                                         // 0 of 1
        Assert.Equal(new[] { "A", "B" }, Names(overlap.TargetSymbolsSeenByBase));
    }

    [Fact]
    public void NoOverlapIsZeroOfTheHeldOutSymbols()
    {
        var overlap = RlPortfolioSymbolOverlap.Of(Manifest(new[] { "A", "B" }), Manifest(new[] { "X", "Y" }, "Y"));
        Assert.Equal((0, 1, 0), (overlap.HeldOutSeenByBase.Length, overlap.TargetHeldOutSymbols.Length, overlap.TargetSymbolsSeenByBase.Length));
    }

    [Fact]
    public void ATargetWithoutAHoldoutIsZeroOfZeroButStillListsTheSymbolsTheBaseHasSeen()
    {
        var overlap = RlPortfolioSymbolOverlap.Of(Manifest(new[] { "A", "B" }), Manifest(new[] { "A", "B" }));
        Assert.Equal((0, 0), (overlap.HeldOutSeenByBase.Length, overlap.TargetHeldOutSymbols.Length));
        Assert.Equal(new[] { "A", "B" }, Names(overlap.TargetSymbolsSeenByBase));
    }

    [Fact]
    public void TheComparisonIsOrdinalSoCaseMatters()
    {
        var overlap = RlPortfolioSymbolOverlap.Of(Manifest(new[] { "a", "b" }), Manifest(new[] { "A", "b" }, "A", "b"));
        Assert.Equal(new[] { "b" }, Names(overlap.HeldOutSeenByBase));
        Assert.Equal(new[] { "A", "b" }, Names(overlap.TargetHeldOutSymbols));                          // ordinal order: upper case sorts first
    }

    [Fact]
    public void TheListsAreSortedAndFreeOfDuplicatesWhateverTheManifestOrder()
    {
        var overlap = RlPortfolioSymbolOverlap.Of(Manifest(new[] { "C", "A", "B", "A" }), Manifest(new[] { "B", "A" }, "B", "A", "B"));
        Assert.Equal(new[] { "A", "B", "C" }, Names(overlap.BaseTrainingGroupSymbols));
        Assert.Equal(new[] { "A", "B" }, Names(overlap.TargetHeldOutSymbols));
        Assert.Equal(new[] { "A", "B" }, Names(overlap.HeldOutSeenByBase));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => RlPortfolioSymbolOverlap.Of(null!, Manifest(new[] { "A" })));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioSymbolOverlap.Of(Manifest(new[] { "A" }), null!));
    }
}
