using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// The pre-registered numbers and wording of the observation ablation (FX0 report, section 4; decisions D-FX3). They are fixed BEFORE any run and are not
/// parameters of an experiment: a changed number is a changed protocol. Kept apart from <see cref="RlPortfolioContract"/>, whose hash is pinned.
/// </summary>
public static class RlPortfolioAblationContract
{
    /// <summary>Independent training runs per arm (D-FX3).</summary>
    public const int RunsPerArm = 5;
    /// <summary>Runs of an arm that must pass for the arm to be called supported (D-FX3).</summary>
    public const int RequiredPassingRuns = 4;
    /// <summary>Smallest share of the blocks of a cell that the arm must win (ties are not wins), theta of D-FX3.</summary>
    public const decimal MinimumWinShare = 0.6m;
    /// <summary>The run-level seed offset of run r is r times this stride, so run 0 is the standard run and no two runs share a candidate seed (the stride is the number of candidate seeds).</summary>
    public static int SeedStride => RlContract.CandidateSeeds.Length;

    public static int SeedOffset(int runIndex)
    {
        if (runIndex < 0 || runIndex >= RunsPerArm) throw new ArgumentOutOfRangeException(nameof(runIndex));
        return checked(runIndex * SeedStride);
    }

    /// <summary>Printed with every result; the protocol never states efficacy, quality, point-in-time correctness or independence of the evidence.</summary>
    public const string Statement =
        "Descriptive comparison of two observation layouts on one stored panel under a pre-registered rule. It is not a claim of investment efficacy, model quality, "
        + "point-in-time correctness or independent evidence; the blocks of one run share a market path and one model.";
}

public enum RlPortfolioAblationRunVerdict { Passed, Failed, NotEvaluable }

/// <summary>
/// Overall result. <c>Incomplete</c>: fewer runs per arm than the protocol requires, so no arm-level statement is made (the per-run lines are still shown).
/// <c>NotEvaluable</c>: no run had a judgement cell with enough effective pairs. <c>NotShown</c>: evaluable, but fewer passing runs than required.
/// </summary>
public enum RlPortfolioAblationVerdict { Incomplete, NotEvaluable, NotShown, SupportedDescriptively }

/// <summary>One judgement cell of one run pair: the arm's paired delta against the control and the counts the rule reads.</summary>
public sealed record RlPortfolioAblationCell(string Partition, RlPanelSymbolGroup Group, bool Qualifying, int BlockCount, int Wins, int Ties, int Losses,
    decimal MeanDelta, decimal WinShare);

public sealed record RlPortfolioAblationRun(int RunIndex, RlPortfolioAblationRunVerdict Verdict, ImmutableArray<RlPortfolioAblationCell> Cells);

public sealed record RlPortfolioAblationResult(RlPortfolioAblationVerdict Verdict, int RunsPerArm, int PassedRuns, int FailedRuns, int NotEvaluableRuns,
    ImmutableArray<RlPortfolioAblationRun> Runs, string Statement);

/// <summary>
/// The pre-registered decision rule, as a pure function of the evaluation reports. Run i of the arm is paired with run i of the control (same panel, plan, costs,
/// weight cap and blocks, checked by <see cref="RlPortfolioTransferComparison.Compare"/> with the across-contracts option). A judgement cell is a comparable cell
/// other than the validation cell of the training symbols (it chose the checkpoint, so it is selection-contaminated). A judgement cell qualifies when it has at least
/// <see cref="RlPortfolioSignTest.MinimumEffectivePairs"/> effective pairs (wins plus losses). A run passes when it has a qualifying cell, every qualifying cell has a
/// positive mean delta and a win share (wins over all blocks of the cell) of at least theta, and no judgement cell has a negative mean delta. An arm is supported
/// descriptively when the required number of runs pass. Everything is decimal arithmetic.
/// </summary>
public static class RlPortfolioAblationProtocol
{
    public static RlPortfolioAblationResult Evaluate(IReadOnlyList<RlPortfolioEvaluationReport> control, IReadOnlyList<RlPortfolioEvaluationReport> arm)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(arm);
        if (control.Count == 0 || control.Count != arm.Count || control.Count > RlPortfolioAblationContract.RunsPerArm)
            throw new ArgumentException($"The arms need the same number of runs, from one to {RlPortfolioAblationContract.RunsPerArm}.", nameof(arm));
        var runs = ImmutableArray.CreateBuilder<RlPortfolioAblationRun>(arm.Count);
        for (int i = 0; i < arm.Count; i++)
        {
            if (arm[i].ModelSha256 == control[i].ModelSha256) throw new InvalidDataException("An arm run and its control run are the same model.");
            var comparison = RlPortfolioTransferComparison.Compare(arm[i], control[i], acrossModelContracts: true);
            var cells = comparison.Cells.Where(c => c.Comparable && !c.SelectionContaminated && c.PairedDelta is not null)
                .Select(c => new RlPortfolioAblationCell(c.Partition, c.Group, c.NEff >= RlPortfolioSignTest.MinimumEffectivePairs, c.BlockCount, c.Wins, c.Ties, c.Losses,
                    c.PairedDelta!.Mean, c.BlockCount == 0 ? 0m : (decimal)c.Wins / c.BlockCount)).ToImmutableArray();
            runs.Add(new RlPortfolioAblationRun(i, Judge(cells), cells));
        }
        int passed = runs.Count(r => r.Verdict == RlPortfolioAblationRunVerdict.Passed);
        int failed = runs.Count(r => r.Verdict == RlPortfolioAblationRunVerdict.Failed);
        int none = runs.Count(r => r.Verdict == RlPortfolioAblationRunVerdict.NotEvaluable);
        var verdict = arm.Count < RlPortfolioAblationContract.RunsPerArm ? RlPortfolioAblationVerdict.Incomplete
            : passed >= RlPortfolioAblationContract.RequiredPassingRuns ? RlPortfolioAblationVerdict.SupportedDescriptively
            : none == runs.Count ? RlPortfolioAblationVerdict.NotEvaluable
            : RlPortfolioAblationVerdict.NotShown;
        return new RlPortfolioAblationResult(verdict, arm.Count, passed, failed, none, runs.ToImmutable(), RlPortfolioAblationContract.Statement);
    }

    private static RlPortfolioAblationRunVerdict Judge(ImmutableArray<RlPortfolioAblationCell> cells)
    {
        var qualifying = cells.Where(c => c.Qualifying).ToArray();
        if (qualifying.Length == 0) return RlPortfolioAblationRunVerdict.NotEvaluable;
        bool qualifyingOk = qualifying.All(c => c.MeanDelta > 0m && c.WinShare >= RlPortfolioAblationContract.MinimumWinShare);
        bool noneWorse = cells.All(c => c.MeanDelta >= 0m);
        return qualifyingOk && noneWorse ? RlPortfolioAblationRunVerdict.Passed : RlPortfolioAblationRunVerdict.Failed;
    }
}
