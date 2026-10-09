using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The pre-registered ablation rule on hand-built reports: the control returns 0 in every block, so the arm's return IS the paired delta and every expected
/// count is written out by hand. Cells: validation/train (selection-contaminated, excluded), oos/train, validation/holdout, oos/holdout.
/// </summary>
public sealed class RlPortfolioAblationProtocolTests
{
    private static BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
        { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m });

    private static RlPortfolioCellReport Cell(string partition, RlPanelSymbolGroup group, params decimal[] returns)
        => new(partition, group, ImmutableArray.Create("AAA", "BBB"), returns.Length, 1, returns.Length >= 1,
            returns.Select((r, i) => new RlPortfolioBlockResult($"{partition}:{i}", ImmutableArray.Create("AAA", "BBB"), r, 0m, 0m, 0m)).ToImmutableArray(),
            null, ImmutableArray<RlPortfolioBaselineSummary>.Empty);

    private static RlPortfolioEvaluationReport Report(string model, bool v2, params RlPortfolioCellReport[] cells)
        => new(RlPortfolioContract.SpecificationId, RlPortfolioEvaluationContract.SchemaVersion, RlPortfolioEvaluationContract.ContractHash,
            v2 ? RlPortfolioV2Contract.ContractHash : RlPortfolioContract.ContractHash, "spec", new string('u', 64), new string('c', 64), model,
            ImmutableArray.Create("AAA", "BBB"), ImmutableArray.Create("AAA", "BBB"), ImmutableArray<string>.Empty, false, Costs(), 100, true,
            cells.ToImmutableArray(), "pit", "efficacy", RlPortfolioEvaluationContract.Statement, ImmutableArray<string>.Empty);

    private static decimal[] Same(int count, decimal value) => Enumerable.Repeat(value, count).ToArray();

    /// <summary>The control run i (zero everywhere) and the arm run i whose cells carry the given returns; the selection cell always holds -0.5.</summary>
    private static (RlPortfolioEvaluationReport Control, RlPortfolioEvaluationReport Arm) Pair(int i, decimal[] oosTrain, decimal[] valHoldout, decimal[] oosHoldout)
    {
        RlPortfolioCellReport[] Cells(Func<decimal[], decimal[]> f) => new[]
        {
            Cell("validation", RlPanelSymbolGroup.Train, f(Same(10, -0.5m))),
            Cell("oos", RlPanelSymbolGroup.Train, f(oosTrain)),
            Cell("validation", RlPanelSymbolGroup.Holdout, f(valHoldout)),
            Cell("oos", RlPanelSymbolGroup.Holdout, f(oosHoldout)),
        };
        // The control's returns are zero; the arm's are the deltas (the selection cell of the control is zero as well, so its delta is -0.5).
        return (Report(new string((char)('a' + i), 64), false, Cells(r => r.Select(_ => 0m).ToArray())),
                Report(new string((char)('A' + i), 64), true, Cells(r => r)));
    }

    private static (IReadOnlyList<RlPortfolioEvaluationReport> Control, IReadOnlyList<RlPortfolioEvaluationReport> Arm) Runs(int count, Func<int, (decimal[], decimal[], decimal[])> perRun)
    {
        var pairs = Enumerable.Range(0, count).Select(i => { var (a, b, c) = perRun(i); return Pair(i, a, b, c); }).ToArray();
        return (pairs.Select(p => p.Control).ToArray(), pairs.Select(p => p.Arm).ToArray());
    }

    private static (decimal[], decimal[], decimal[]) Good() => (Same(10, 0.02m), Same(10, 0.01m), Same(10, 0.03m));

    [Fact]
    public void FiveRunsThatAllPassAreSupportedDescriptivelyAndTheSelectionCellIsLeftOut()
    {
        var (control, arm) = Runs(5, _ => Good());
        var result = RlPortfolioAblationProtocol.Evaluate(control, arm);
        Assert.Equal(RlPortfolioAblationVerdict.SupportedDescriptively, result.Verdict);
        Assert.Equal((5, 5, 0, 0), (result.RunsPerArm, result.PassedRuns, result.FailedRuns, result.NotEvaluableRuns));
        var run = result.Runs[0];
        Assert.Equal(RlPortfolioAblationRunVerdict.Passed, run.Verdict);
        Assert.Equal(3, run.Cells.Length);                                                     // validation/train is not a judgement cell
        Assert.DoesNotContain(run.Cells, c => c.Partition == "validation" && c.Group == RlPanelSymbolGroup.Train);
        var oosTrain = run.Cells.Single(c => c.Partition == "oos" && c.Group == RlPanelSymbolGroup.Train);
        Assert.Equal((true, 10, 10, 0, 0, 0.02m, 1m), (oosTrain.Qualifying, oosTrain.BlockCount, oosTrain.Wins, oosTrain.Ties, oosTrain.Losses, oosTrain.MeanDelta, oosTrain.WinShare));
        Assert.Contains("not a claim of investment efficacy", result.Statement);
    }

    [Theory]
    [InlineData(4, RlPortfolioAblationVerdict.SupportedDescriptively)]
    [InlineData(3, RlPortfolioAblationVerdict.NotShown)]
    [InlineData(0, RlPortfolioAblationVerdict.NotShown)]
    public void TheArmIsSupportedExactlyWhenTheRequiredNumberOfRunsPass(int passing, RlPortfolioAblationVerdict expected)
    {
        // A failing run: the oos/train cell wins 5 of 10 blocks (share 0.5 below theta 0.6) with a positive mean.
        var failing = (Enumerable.Repeat(0.02m, 5).Concat(Enumerable.Repeat(-0.001m, 5)).ToArray(), Same(10, 0.01m), Same(10, 0.03m));
        var (control, arm) = Runs(5, i => i < passing ? Good() : failing);
        var result = RlPortfolioAblationProtocol.Evaluate(control, arm);
        Assert.Equal(expected, result.Verdict);
        Assert.Equal((passing, 5 - passing), (result.PassedRuns, result.FailedRuns));
    }

    [Fact]
    public void ARunFailsOnALowWinShareOnANonPositiveMeanAndOnAnyWorseCellEvenWithoutEnoughPairs()
    {
        // Win share: 5 wins of 10 (0.5) although the mean is positive.
        var lowShare = RlPortfolioAblationProtocol.Evaluate(Runs(1, _ => (Enumerable.Repeat(0.05m, 5).Concat(Enumerable.Repeat(-0.01m, 5)).ToArray(), Same(10, 0.01m), Same(10, 0.03m))).Control,
            Runs(1, _ => (Enumerable.Repeat(0.05m, 5).Concat(Enumerable.Repeat(-0.01m, 5)).ToArray(), Same(10, 0.01m), Same(10, 0.03m))).Arm);
        Assert.Equal(RlPortfolioAblationRunVerdict.Failed, lowShare.Runs[0].Verdict);
        // Mean not positive: 6 wins of 0.01 and 4 losses of -0.02 -> mean -0.002.
        var (c1, a1) = Runs(1, _ => (Enumerable.Repeat(0.01m, 6).Concat(Enumerable.Repeat(-0.02m, 4)).ToArray(), Same(10, 0.01m), Same(10, 0.03m)));
        Assert.Equal(RlPortfolioAblationRunVerdict.Failed, RlPortfolioAblationProtocol.Evaluate(c1, a1).Runs[0].Verdict);
        // A holdout cell with only two blocks (not qualifying) and a negative mean still fails the run: no judgement cell may be worse than the control.
        var (c2, a2) = Runs(1, _ => (Same(10, 0.02m), Same(2, -0.04m), Same(10, 0.03m)));
        var worse = RlPortfolioAblationProtocol.Evaluate(c2, a2);
        Assert.Equal(RlPortfolioAblationRunVerdict.Failed, worse.Runs[0].Verdict);
        Assert.False(worse.Runs[0].Cells.Single(c => c.Group == RlPanelSymbolGroup.Holdout && c.Partition == "validation").Qualifying);
    }

    [Fact]
    public void TiesAreNotWinsAndOnlyEffectivePairsQualifyACell()
    {
        // 6 wins and 4 ties: NEff 6 qualifies; share 6/10 = 0.6 is exactly theta -> passes. 5 wins and 5 ties: NEff 5 does not qualify.
        var six = Same(6, 0.01m).Concat(Same(4, 0m)).ToArray();
        var five = Same(5, 0.01m).Concat(Same(5, 0m)).ToArray();
        var (cOk, aOk) = Runs(1, _ => (six, Same(10, 0.01m), Same(10, 0.03m)));
        var ok = RlPortfolioAblationProtocol.Evaluate(cOk, aOk).Runs[0];
        var cell = ok.Cells.Single(c => c.Partition == "oos" && c.Group == RlPanelSymbolGroup.Train);
        Assert.Equal((true, 6, 4, 0, 0.006m, 0.6m), (cell.Qualifying, cell.Wins, cell.Ties, cell.Losses, cell.MeanDelta, cell.WinShare));
        Assert.Equal(RlPortfolioAblationRunVerdict.Passed, ok.Verdict);
        var (cFew, aFew) = Runs(1, _ => (five, Same(10, 0.01m), Same(10, 0.03m)));
        Assert.False(RlPortfolioAblationProtocol.Evaluate(cFew, aFew).Runs[0].Cells.Single(c => c.Partition == "oos" && c.Group == RlPanelSymbolGroup.Train).Qualifying);
    }

    [Fact]
    public void WithoutAQualifyingCellEveryRunIsNotEvaluableAndNothingIsClaimed()
    {
        var (control, arm) = Runs(5, _ => (Same(3, 0.05m), Same(3, 0.05m), Same(3, 0.05m)));
        var result = RlPortfolioAblationProtocol.Evaluate(control, arm);
        Assert.Equal(RlPortfolioAblationVerdict.NotEvaluable, result.Verdict);
        Assert.Equal(5, result.NotEvaluableRuns);
        Assert.All(result.Runs, r => Assert.Equal(RlPortfolioAblationRunVerdict.NotEvaluable, r.Verdict));
    }

    [Fact]
    public void FewerThanTheRequiredRunsIsIncompleteWhateverTheRunsSay()
    {
        var (control, arm) = Runs(2, _ => Good());
        var result = RlPortfolioAblationProtocol.Evaluate(control, arm);
        Assert.Equal(RlPortfolioAblationVerdict.Incomplete, result.Verdict);
        Assert.Equal(2, result.PassedRuns);                                                    // the per-run lines stay visible, the arm-level statement does not
    }

    [Fact]
    public void TheInputsAreChecked()
    {
        var (control, arm) = Runs(2, _ => Good());
        Assert.Throws<ArgumentNullException>(() => RlPortfolioAblationProtocol.Evaluate(null!, arm));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioAblationProtocol.Evaluate(control, null!));
        Assert.Throws<ArgumentException>(() => RlPortfolioAblationProtocol.Evaluate(control, arm.Take(1).ToArray()));
        Assert.Throws<ArgumentException>(() => RlPortfolioAblationProtocol.Evaluate(Array.Empty<RlPortfolioEvaluationReport>(), Array.Empty<RlPortfolioEvaluationReport>()));
        var (six, sixArm) = Runs(6, _ => Good());
        Assert.Throws<ArgumentException>(() => RlPortfolioAblationProtocol.Evaluate(six, sixArm));
        Assert.Throws<InvalidDataException>(() => RlPortfolioAblationProtocol.Evaluate(control, new[] { control[0], arm[1] }));      // an arm run that is the control's model
        var otherPanel = arm[0] with { UniverseSha256 = new string('q', 64) };
        Assert.Throws<InvalidDataException>(() => RlPortfolioAblationProtocol.Evaluate(control, new[] { otherPanel, arm[1] }));
    }

    [Fact]
    public void TheProtocolNumbersAreThePreRegisteredOnesAndTheSeedsOfTheRunsNeverOverlap()
    {
        Assert.Equal((5, 4, 0.6m), (RlPortfolioAblationContract.RunsPerArm, RlPortfolioAblationContract.RequiredPassingRuns, RlPortfolioAblationContract.MinimumWinShare));
        Assert.Equal(new[] { 0, 3, 6, 9, 12 }, Enumerable.Range(0, 5).Select(RlPortfolioAblationContract.SeedOffset).ToArray());
        var seeds = Enumerable.Range(0, 5).SelectMany(r => RlContract.CandidateSeeds.Select(s => s + RlPortfolioAblationContract.SeedOffset(r))).ToArray();
        Assert.Equal(seeds.Length, seeds.Distinct().Count());
        Assert.Equal(RlContract.CandidateSeeds, Enumerable.Range(0, 3).Select(i => RlContract.CandidateSeeds[i] + RlPortfolioAblationContract.SeedOffset(0)));   // run 0 is the standard run
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioAblationContract.SeedOffset(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioAblationContract.SeedOffset(5));
    }

    [Fact]
    public async Task ARequestWithAnOutOfRangeSeedOffsetIsRejectedBeforeAnythingIsTouched()
    {
        var runner = new RlPortfolioTrainingRunner();
        var costs = Costs();
        foreach (int offset in new[] { -1, int.MaxValue })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(
                new RlPortfolioTrainingRequest("no such panel", costs, new RlPortfolioSettings(), SeedOffset: offset)));
    }
}
