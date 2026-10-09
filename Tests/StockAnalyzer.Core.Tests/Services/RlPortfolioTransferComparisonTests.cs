using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The pure paired comparison of two evaluation reports. Reports are assembled by hand (the comparison reads only their identity fields and the per-block
/// policy returns), so every expected delta is a decimal difference written out in the test.
/// </summary>
public sealed class RlPortfolioTransferComparisonTests
{
    private static BacktestConfiguration Costs(decimal flat = 0m) => RlContract.CreateConfiguration(new BacktestConfiguration
        { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m, CommissionFlat = flat });

    private static RlPortfolioBlockResult Block(string id, decimal policy)
        => new(id, ImmutableArray.Create("AAA", "BBB"), policy, 0m, 0m, 0m);

    private static RlPortfolioCellReport Cell(string partition, RlPanelSymbolGroup group, params (string Id, decimal Return)[] blocks)
        => new(partition, group, ImmutableArray.Create("AAA", "BBB"), blocks.Length, 1, blocks.Length >= 1,
            blocks.Select(b => Block(b.Id, b.Return)).ToImmutableArray(), null, ImmutableArray<RlPortfolioBaselineSummary>.Empty);

    private static RlPortfolioEvaluationReport Report(string model, params RlPortfolioCellReport[] cells)
        => new(RlPortfolioContract.SpecificationId, RlPortfolioEvaluationContract.SchemaVersion, RlPortfolioEvaluationContract.ContractHash,
            RlPortfolioContract.ContractHash, "spec", new string('u', 64), new string('c', 64), model,
            ImmutableArray.Create("AAA", "BBB"), ImmutableArray.Create("AAA", "BBB"), ImmutableArray<string>.Empty, false, Costs(), 100, true,
            cells.ToImmutableArray(), "pit", "efficacy", RlPortfolioEvaluationContract.Statement, ImmutableArray<string>.Empty);

    private static RlPortfolioCellReport Validation(params (string, decimal)[] blocks) => Cell("validation", RlPanelSymbolGroup.Train, blocks);
    private static RlPortfolioCellReport Oos(params (string, decimal)[] blocks) => Cell("oos", RlPanelSymbolGroup.Train, blocks);

    private static RlPortfolioEvaluationReport A() => Report(new string('a', 64),
        Validation(("validation:830:1022", 0.10m), ("validation:1022:1214", -0.05m), ("validation:1214:1406", 0.02m)),
        Oos(("oos:1500:1692", 0.03m)));

    private static RlPortfolioEvaluationReport B() => Report(new string('b', 64),
        Validation(("validation:830:1022", 0.04m), ("validation:1022:1214", -0.05m), ("validation:1214:1406", 0.05m)),
        Oos(("oos:1500:1692", 0.01m)));

    [Fact]
    public void TheDeltaIsTheFirstReportMinusTheSecondPerBlockInDecimal()
    {
        var result = RlPortfolioTransferComparison.Compare(A(), B());
        Assert.Equal(2, result.Cells.Length);
        var validation = result.Cells[0];
        Assert.Equal(("validation", RlPanelSymbolGroup.Train, true, 3), (validation.Partition, validation.Group, validation.Comparable, validation.BlockCount));
        // deltas 0.06, 0.00, -0.03: mean 0.03 / 3 = 0.01, median 0.00, minimum -0.03, maximum 0.06, positive rate 1/3.
        var delta = validation.PairedDelta!;
        Assert.Equal(3, delta.Count);
        Assert.Equal(0.01m, delta.Mean);
        Assert.Equal(0.00m, delta.Median);
        Assert.Equal(-0.03m, delta.Minimum);
        Assert.Equal(0.06m, delta.Maximum);
        Assert.Equal(1m / 3m, delta.PositiveRate);
        var oos = result.Cells[1].PairedDelta!;
        Assert.Equal((1, 0.02m, 0.02m, 0.02m, 0.02m, 1m), (oos.Count, oos.Mean, oos.Median, oos.Minimum, oos.Maximum, oos.PositiveRate));
        Assert.Equal(RlPortfolioTransferContract.Statement, result.Statement);
    }

    [Fact]
    public void ReversingTheArgumentsNegatesTheDeltas()
    {
        var forward = RlPortfolioTransferComparison.Compare(A(), B()).Cells[0].PairedDelta!;
        var backward = RlPortfolioTransferComparison.Compare(B(), A()).Cells[0].PairedDelta!;
        Assert.Equal(-forward.Mean, backward.Mean);
        Assert.Equal(-forward.Maximum, backward.Minimum);
        Assert.Equal(-forward.Minimum, backward.Maximum);
    }

    [Fact]
    public void AReportAgainstItselfHasZeroDeltasEvenWhenTheModelDiffers()
    {
        var a = A();
        var sameBlocksOtherModel = a with { ModelSha256 = new string('z', 64) };
        foreach (var other in new[] { a, sameBlocksOtherModel })
        {
            var result = RlPortfolioTransferComparison.Compare(a, other);
            Assert.All(result.Cells, c => Assert.Equal((0m, 0m, 0m, 0m, 0m), (c.PairedDelta!.Mean, c.PairedDelta.Median, c.PairedDelta.Minimum, c.PairedDelta.Maximum, c.PairedDelta.PositiveRate)));
        }
    }

    [Fact]
    public void ACellEvaluatedInOneReportOnlyIsStatedNotComparableAndKept()
    {
        var a = A();
        var b = B() with { Cells = ImmutableArray.Create(B().Cells[0], Cell("oos", RlPanelSymbolGroup.Train)) };
        var result = RlPortfolioTransferComparison.Compare(a, b);
        Assert.Equal(2, result.Cells.Length);
        Assert.True(result.Cells[0].Comparable);
        var oos = result.Cells[1];
        Assert.Equal(("oos", false, 0), (oos.Partition, oos.Comparable, oos.BlockCount));
        Assert.Null(oos.PairedDelta);
        var neither = RlPortfolioTransferComparison.Compare(b, b);
        Assert.False(neither.Cells[1].Comparable);
    }

    public static TheoryData<string, Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>, string> Mismatches() => new()
    {
        { "specification id", r => r with { SpecificationId = "other" }, "the specification id" },
        { "numerical contract", r => r with { PortfolioContractHash = new string('x', 64) }, "the numerical contract hash" },
        { "evaluation mode", r => r with { EvaluationSpecificationId = "standard" }, "the evaluation mode" },
        { "universe", r => r with { UniverseSha256 = new string('x', 64) }, "the universe hash" },
        { "calendar", r => r with { CalendarSha256 = new string('x', 64) }, "the calendar hash" },
        { "costs", r => r with { Costs = Costs(flat: 1m) }, "the costs" },
        { "weight cap", r => r with { MaxWeightPercent = 50 }, "the weight cap" },
        { "cell list shorter", r => r with { Cells = r.Cells.RemoveAt(1) }, "the cell list" },
        { "cell group", r => r with { Cells = r.Cells.SetItem(1, r.Cells[1] with { Group = RlPanelSymbolGroup.Holdout }) }, "the cell list" },
        { "cell order", r => r with { Cells = ImmutableArray.Create(r.Cells[1], r.Cells[0]) }, "the cell list" },
        { "block ids", r => r with { Cells = r.Cells.SetItem(0, r.Cells[0] with { Blocks = r.Cells[0].Blocks.SetItem(2, r.Cells[0].Blocks[2] with { BlockId = "validation:9:201" }) }) },
            "the blocks of cell validation/Train" },
        { "block order", r => r with { Cells = r.Cells.SetItem(0, r.Cells[0] with { Blocks = r.Cells[0].Blocks.Reverse().ToImmutableArray() }) },
            "the blocks of cell validation/Train" },
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void AMismatchIsRejectedNamingTheField(string name, Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport> change, string field)
    {
        Assert.False(string.IsNullOrEmpty(name));
        var error = Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(A(), change(B())));
        Assert.StartsWith(RlPortfolioTransferContract.ErrorPrefix, error.Message);
        Assert.Contains(field, error.Message);
    }

    // ----- costs as numbers -----

    [Fact]
    public void CostsAreComparedAsDecimalsSoTheScaleOfANumberDoesNotMatter()
    {
        var withScale = B() with { Costs = Costs(flat: 0.10m) };
        var without = A() with { Costs = Costs(flat: 0.1m) };
        Assert.NotEqual(System.Text.Json.JsonSerializer.Serialize(without.Costs, RlFiles.Json), System.Text.Json.JsonSerializer.Serialize(withScale.Costs, RlFiles.Json));      // the texts differ ...
        Assert.Equal(2, RlPortfolioTransferComparison.Compare(without, withScale).Cells.Length);                                                                              // ... the numbers do not
        var error = Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(without, B() with { Costs = Costs(flat: 0.2m) }));
        Assert.Contains("the costs", error.Message);
    }

    // ----- the sign test over the paired blocks (a reference value only) -----

    /// <summary>A pair of reports with one validation and one oos cell whose blocks differ by exactly the given deltas (A minus B), in decimal.</summary>
    private static (RlPortfolioEvaluationReport A, RlPortfolioEvaluationReport B) Pair(params decimal[] deltas)
    {
        var ids = deltas.Select((_, i) => $"validation:{i * 200}:{i * 200 + 192}").ToArray();
        var b = Report(new string('b', 64), Validation(ids.Select(id => (id, 0.10m)).ToArray()), Oos(("oos:9000:9192", 0.00m)));
        var a = Report(new string('a', 64), Validation(ids.Select((id, i) => (id, 0.10m + deltas[i])).ToArray()), Oos(("oos:9000:9192", 0.00m)));
        return (a, b);
    }

    [Fact]
    public void WinsTiesLossesAndTheExactSignTestFollowFromTheHandCountedDeltas()
    {
        var (a, b) = Pair(0.01m, 0.02m, -0.005m, 0.03m, 0.00m);
        var cell = RlPortfolioTransferComparison.Compare(a, b).Cells[0];
        Assert.Equal(0.011m, cell.PairedDelta!.Mean);                                       // 0.055 / 5
        Assert.Equal((3, 1, 1, 4), (cell.Wins, cell.Ties, cell.Losses, cell.NEff));
        Assert.Equal(0.3125, cell.OneSidedP);                                               // P(X >= 3 | n = 4) = (4 + 1) / 16
        Assert.Equal(0.125, cell.MinAttainableP);                                           // 2 * 2^-4
        Assert.Equal(RlPortfolioComparisonInference.Unavailable, cell.Inference);           // 4 effective pairs: no p-value can reach the reference level
        Assert.True(cell.SelectionContaminated);                                            // the validation cell of the training symbols chose the checkpoint
        var oos = RlPortfolioTransferComparison.Compare(a, b).Cells[1];
        Assert.False(oos.SelectionContaminated);
        Assert.Equal((0, 1, 0, 0, null, null), (oos.Wins, oos.Ties, oos.Losses, oos.NEff, oos.OneSidedP, oos.MinAttainableP));          // a tie is no pair
        Assert.Equal(RlPortfolioComparisonInference.Unavailable, oos.Inference);
    }

    [Theory]
    [InlineData(5, 0.03125, 0.0625, RlPortfolioComparisonInference.Unavailable)]           // 2^-5 and 2 * 2^-5: the smallest two-sided p is above 0.05
    [InlineData(6, 0.015625, 0.03125, RlPortfolioComparisonInference.ReferenceOnly)]       // from six effective pairs on the level can be reached
    public void AllWinsGiveTheSmallestPAndTheInferenceFlagFollowsTheEffectivePairs(int pairs, double p, double minimum, RlPortfolioComparisonInference inference)
    {
        var (a, b) = Pair(Enumerable.Repeat(0.01m, pairs).ToArray());
        var cell = RlPortfolioTransferComparison.Compare(a, b).Cells[0];
        Assert.Equal((pairs, 0, 0), (cell.Wins, cell.Ties, cell.Losses));
        Assert.Equal((p, minimum, inference), (cell.OneSidedP, cell.MinAttainableP, cell.Inference));
    }

    [Fact]
    public void SwappingTheReportsSwapsWinsAndLossesAndTheOneSidedDirection()
    {
        var (a, b) = Pair(0.01m, 0.02m, -0.005m, 0.03m, 0.00m);
        var forward = RlPortfolioTransferComparison.Compare(a, b).Cells[0];
        var backward = RlPortfolioTransferComparison.Compare(b, a).Cells[0];
        Assert.Equal((forward.Wins, forward.Ties, forward.Losses, forward.NEff), (backward.Losses, backward.Ties, backward.Wins, backward.NEff));
        Assert.Equal(0.9375, backward.OneSidedP);                                           // P(X >= 1 | n = 4) = 15 / 16
        Assert.Equal(forward.MinAttainableP, backward.MinAttainableP);
        Assert.Equal(-forward.PairedDelta!.Mean, backward.PairedDelta!.Mean);
    }

    [Fact]
    public void TheMinimumEffectivePairsIsDerivedFromTheReferenceLevel()
    {
        Assert.Equal(6, RlPortfolioSignTest.MinimumEffectivePairs);
        Assert.True(RlPortfolioSignTest.MinimumAttainableTwoSidedP(RlPortfolioSignTest.MinimumEffectivePairs) < RlPortfolioSignTest.ReferenceLevel);
        Assert.True(RlPortfolioSignTest.MinimumAttainableTwoSidedP(RlPortfolioSignTest.MinimumEffectivePairs - 1) >= RlPortfolioSignTest.ReferenceLevel);
    }

    [Theory]
    [InlineData(0, 7)] [InlineData(1, 7)] [InlineData(4, 7)] [InlineData(7, 7)] [InlineData(3, 10)] [InlineData(60, 120)]
    public void TheOneSidedPTailsAddUpToOneAndTheEdgesAreExact(int wins, int effective)
    {
        double tail = RlPortfolioSignTest.OneSidedP(wins, effective);
        Assert.InRange(tail, 0d, 1d);
        if (wins >= 1) Assert.Equal(1d, tail + RlPortfolioSignTest.OneSidedP(effective - wins + 1, effective), 12);     // P(X >= k) + P(X >= n - k + 1) = 1
        Assert.Equal(1d, RlPortfolioSignTest.OneSidedP(0, effective));
        Assert.Equal(Math.Pow(2, -effective), RlPortfolioSignTest.OneSidedP(effective, effective));
    }

    [Fact]
    public void ALargeNumberOfPairsNeitherOverflowsNorUnderflowsTheExactCounts()
    {
        double middle = RlPortfolioSignTest.OneSidedP(750, 1_500);                          // 2^1500 does not fit a double: the counts are scaled down first
        Assert.InRange(middle, 0.5, 0.55);
        Assert.Equal(0d, RlPortfolioSignTest.OneSidedP(1_500, 1_500));                      // 2^-1500 underflows to zero, it does not throw
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSignTest.OneSidedP(5, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSignTest.OneSidedP(-1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSignTest.MinimumAttainableTwoSidedP(0));
        Assert.Equal(1d, RlPortfolioSignTest.MinimumAttainableTwoSidedP(1));
    }

    private static RlPortfolioEvaluationReport V2(RlPortfolioEvaluationReport report) => report with { PortfolioContractHash = RlPortfolioV2Contract.ContractHash };

    [Fact]
    public void ReportsOfDifferentObservationContractsAreRefusedUnlessTheCallerAsksForTheAblation()
    {
        var error = Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(A(), V2(B())));
        Assert.Contains("the numerical contract hash", error.Message);
        var result = RlPortfolioTransferComparison.Compare(A(), V2(B()), acrossModelContracts: true);
        Assert.True(result.AcrossModelContracts);
        // The pairing itself is the same as for two reports of one contract.
        var same = RlPortfolioTransferComparison.Compare(A(), B());
        Assert.False(same.AcrossModelContracts);
        Assert.Equal(same.Cells, result.Cells);
        Assert.Equal(RlPortfolioTransferContract.Statement, result.Statement);
        Assert.False(RlPortfolioTransferComparison.Compare(A(), B(), acrossModelContracts: true).AcrossModelContracts);   // the option never marks a same-contract pair
    }

    [Fact]
    public void TheAblationOptionStillRefusesAnUnknownContractAndEveryOtherMismatch()
    {
        var unknown = A() with { PortfolioContractHash = new string('x', 64) };
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(unknown, V2(B()), acrossModelContracts: true));
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(V2(A()), unknown, acrossModelContracts: true));
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(A(), V2(B()) with { UniverseSha256 = new string('q', 64) }, acrossModelContracts: true));
        Assert.Throws<InvalidDataException>(() => RlPortfolioTransferComparison.Compare(A(), V2(B()) with { Costs = Costs(1m) }, acrossModelContracts: true));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => RlPortfolioTransferComparison.Compare(null!, A()));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioTransferComparison.Compare(A(), null!));
    }

    [Fact]
    public void TheStatementKeepsTheLimitationsAndTheContractClassStaysApartFromThePinnedHash()
    {
        Assert.Contains("not shown to beat training from scratch", RlPortfolioTransferContract.Statement);
        Assert.Contains("no investment efficacy", RlPortfolioTransferContract.Statement);
        // The transfer constants live in their own class: the pinned training contract (RlPortfolioContractTests) never sees them.
        Assert.DoesNotContain(nameof(RlPortfolioTransferContract.Statement), RlPortfolioContract.ToWireDefaults().Keys);
    }
}
