using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Episode catalog of a panel (a new function; <see cref="RlSourceSnapshot"/> catalog construction is not branched). The partition
/// arithmetic is that of the single-symbol catalog on the shared calendar: train blocks start at 0, validation and OOS blocks start a
/// window after their cut. That window-length gap is an EMBARGO between two partitions; it is not what keeps a window inside its block: every anchor window and fill of a tile already lies inside the tile <c>[b, b + S)</c>
/// (first anchor <c>b + W - 1</c>, last fill <c>b + S - 1</c>, with <c>S = W + H</c>). Pure: it sees only the calendar length and per-symbol presence.
/// </summary>
public static class RlPanelCatalogBuilder
{
    /// <summary>
    /// Builds the catalog. <paramref name="presencePrefix"/> holds one array per symbol of length <c>calendarLength + 1</c> whose entry
    /// <c>t</c> is the number of calendar indices below <c>t</c> on which the symbol has a bar (so a block is complete for a symbol when the
    /// difference over the block equals its length). Selection is block-level and uses whole-block knowledge (curation, not an
    /// observation feature); the policy's own mask is built later from past bars only.
    /// </summary>
    public static RlPanelCatalog Build(int calendarLength, IReadOnlyList<ReadOnlyMemory<int>> presencePrefix,
        ImmutableArray<bool> isHoldout, RlPanelEvaluationMode mode)
    {
        var catalog = Evaluate(calendarLength, presencePrefix, isHoldout, mode, out var shortCells);
        if (shortCells.Length > 0) throw InsufficientData(calendarLength, presencePrefix.Count, shortCells);
        return catalog;
    }

    /// <summary>
    /// The catalog together with the training-group cells that miss their minimum (empty when the rule holds); never throws for a short
    /// panel, so a preflight can show the shortfall without an exception. Holdout cells below their minimum are reported in
    /// <see cref="RlPanelCatalog.Cells"/> as not evaluable and never counted as a failure here.
    /// </summary>
    public static RlPanelCatalog Evaluate(int calendarLength, IReadOnlyList<ReadOnlyMemory<int>> presencePrefix,
        ImmutableArray<bool> isHoldout, RlPanelEvaluationMode mode, out ImmutableArray<RlPanelCellCount> shortCells)
    {
        var need = RlPanelBlockRequirement.Of(mode);
        return EvaluateCore(calendarLength, presencePrefix, isHoldout, mode, need.TrainBlocks, need.ValidationBlocks, need.TestBlocks, out shortCells);
    }

    /// <summary>
    /// <see cref="Build"/> for a partition plan (short-history only). Throws <c>InsufficientData</c> exactly like <see cref="Build"/> when a
    /// training-group cell misses its minimum.
    /// </summary>
    public static RlPanelCatalog BuildPlan(int calendarLength, IReadOnlyList<ReadOnlyMemory<int>> presencePrefix,
        ImmutableArray<bool> isHoldout, RlPanelEvaluationMode mode, RlPanelPartitionPlan plan)
    {
        var catalog = EvaluatePlan(calendarLength, presencePrefix, isHoldout, mode, plan, out var shortCells);
        if (shortCells.Length > 0) throw InsufficientData(calendarLength, presencePrefix.Count, shortCells);
        return catalog;
    }

    /// <summary>
    /// <see cref="Evaluate"/> for a partition plan (short-history only). <see cref="RlPortfolioPartitionMode.Ratio"/> runs the unchanged 3:1:1
    /// geometry with the plan's required block counts; <see cref="RlPortfolioPartitionMode.BlockCount"/> counts the blocks back from the latest
    /// date (see <see cref="EvaluateBlockCount"/>).
    /// </summary>
    public static RlPanelCatalog EvaluatePlan(int calendarLength, IReadOnlyList<ReadOnlyMemory<int>> presencePrefix,
        ImmutableArray<bool> isHoldout, RlPanelEvaluationMode mode, RlPanelPartitionPlan plan, out ImmutableArray<RlPanelCellCount> shortCells)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (mode != RlPanelEvaluationMode.ShortHistory) throw new ArgumentException("Partition plans apply to the short-history evaluation only.", nameof(mode));
        var need = plan.Requirement;
        return plan.Mode == RlPortfolioPartitionMode.Ratio
            ? EvaluateCore(calendarLength, presencePrefix, isHoldout, mode, need.TrainBlocks, need.ValidationBlocks, need.TestBlocks, out shortCells)
            : EvaluateBlockCount(calendarLength, presencePrefix, isHoldout, mode, need, out shortCells);
    }

    private static System.IO.InvalidDataException InsufficientData(int calendarLength, int symbolCount, ImmutableArray<RlPanelCellCount> shortCells)
        => new(RlSourceContract.InsufficientData(
            $"complete independent panel episode support is unavailable ({symbolCount} symbols, calendar length {calendarLength}; "
            + string.Join(", ", shortCells.Select(c => $"{c.Partition}/{c.Group} has {c.BlockCount} of {c.RequiredBlocks} blocks")) + ")."));

    private static void ValidateInput(int calendarLength, IReadOnlyList<ReadOnlyMemory<int>> presencePrefix, ImmutableArray<bool> isHoldout)
    {
        ArgumentNullException.ThrowIfNull(presencePrefix);
        if (calendarLength < 1) throw new ArgumentOutOfRangeException(nameof(calendarLength));
        if (isHoldout.IsDefault || isHoldout.Length != presencePrefix.Count)
            throw new ArgumentException("One holdout flag per symbol is required.", nameof(isHoldout));
        foreach (var prefix in presencePrefix)
            if (prefix.Length != calendarLength + 1) throw new ArgumentException("Every presence array must have calendarLength + 1 entries.", nameof(presencePrefix));
    }

    /// <summary>
    /// The block starting at <paramref name="start"/>, listed when at least <see cref="RlPortfolioContract.MinimumBlockSymbols"/> symbols have a bar on
    /// every index of it; otherwise it is recorded in <paramref name="excluded"/> and null is returned.
    /// </summary>
    private static RlPanelBlock? TryBlock(IReadOnlyList<ReadOnlyMemory<int>> presencePrefix, string partition, int start, ImmutableArray<string>.Builder excluded)
    {
        int stop = start + RlContract.SupportLength;
        string id = $"{partition}:{start}:{stop}";
        var eligible = Enumerable.Range(0, presencePrefix.Count)
            .Where(i => presencePrefix[i].Span[stop] - presencePrefix[i].Span[start] == RlContract.SupportLength).ToImmutableArray();
        if (eligible.Length < RlPortfolioContract.MinimumBlockSymbols) { excluded.Add(id + ":InsufficientAssets"); return null; }
        return new RlPanelBlock(id, partition, start, stop, eligible);
    }

    /// <summary>The cells in their fixed order: train, validation and oos for the training group, then the holdout cells when a symbol is held out.</summary>
    private static RlPanelCatalog WithCells(RlPanelCatalog provisional, ImmutableArray<bool> isHoldout, int trainRequired, int validationRequired,
        int oosRequired, out ImmutableArray<RlPanelCellCount> shortCells)
    {
        var cells = ImmutableArray.CreateBuilder<RlPanelCellCount>();
        void Add(string partition, RlPanelSymbolGroup group, int required)
            => cells.Add(new RlPanelCellCount(partition, group, provisional.CellBlocks(partition, group).Length, required));
        Add(RlPortfolioContract.TrainPartition, RlPanelSymbolGroup.Train, trainRequired);
        Add(RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Train, validationRequired);
        Add(RlPortfolioContract.OosPartition, RlPanelSymbolGroup.Train, oosRequired);
        if (isHoldout.Contains(true))
        {
            Add(RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Holdout, validationRequired);
            Add(RlPortfolioContract.OosPartition, RlPanelSymbolGroup.Holdout, oosRequired);
        }
        var result = provisional with { Cells = cells.ToImmutable() };
        shortCells = result.Cells.Where(c => c.Group == RlPanelSymbolGroup.Train && !c.Evaluable).ToImmutableArray();
        return result;
    }

    /// <summary>The cuts between train and validation and between validation and test of the 3:1:1 ratio split (integer division).</summary>
    private static (int Cut1, int Cut2) RatioCuts(int calendarLength)
        => (checked((int)((long)RlContract.TrainPartitionNumerator * calendarLength / RlContract.PartitionDenominator)),
            checked((int)((long)RlContract.ValidationPartitionNumerator * calendarLength / RlContract.PartitionDenominator)));

    /// <summary>The cuts of the end-aligned block-count split: <c>cut2 = L - W - S*O</c>, <c>cut1 = cut2 - W - S*V</c> (negative when the calendar is too short).</summary>
    private static (int Cut1, int Cut2) BlockCountCuts(int calendarLength, RlPanelBlockRequirement need)
    {
        int cut2 = checked(calendarLength - RlContract.WindowLength - RlContract.SupportLength * need.TestBlocks);
        return (checked(cut2 - RlContract.WindowLength - RlContract.SupportLength * need.ValidationBlocks), cut2);
    }

    /// <summary>The cuts a catalog built with <paramref name="plan"/> stores (<see cref="RlPanelCatalog.TrainCut"/>, <see cref="RlPanelCatalog.ValidationCut"/>), floored at zero.</summary>
    public static (int TrainCut, int ValidationCut) PlanCuts(int calendarLength, RlPanelPartitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var (cut1, cut2) = plan.Mode == RlPortfolioPartitionMode.Ratio ? RatioCuts(calendarLength) : BlockCountCuts(calendarLength, plan.Requirement);
        return (Math.Max(cut1, 0), Math.Max(cut2, 0));
    }

    /// <summary>The 3:1:1 ratio geometry; the required block counts per partition are arguments (<see cref="Evaluate"/> passes 1 / needed / needed).</summary>
    private static RlPanelCatalog EvaluateCore(int calendarLength, IReadOnlyList<ReadOnlyMemory<int>> presencePrefix,
        ImmutableArray<bool> isHoldout, RlPanelEvaluationMode mode, int trainRequired, int validationRequired, int oosRequired,
        out ImmutableArray<RlPanelCellCount> shortCells)
    {
        ValidateInput(calendarLength, presencePrefix, isHoldout);
        var (cut1, cut2) = RatioCuts(calendarLength);
        var excluded = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<RlPanelBlock> Blocks(string name, int start, int end)
        {
            var blocks = ImmutableArray.CreateBuilder<RlPanelBlock>();
            for (int b = start; b + RlContract.SupportLength <= end; b += RlContract.SupportLength)
                if (TryBlock(presencePrefix, name, b, excluded) is { } block) blocks.Add(block);
            return blocks.ToImmutable();
        }
        var train = Blocks(RlPortfolioContract.TrainPartition, 0, cut1);
        var validation = Blocks(RlPortfolioContract.ValidationPartition, cut1 + RlContract.WindowLength, cut2);
        var oos = Blocks(RlPortfolioContract.OosPartition, cut2 + RlContract.WindowLength, calendarLength);
        var provisional = new RlPanelCatalog(train, validation, oos, excluded.ToImmutable(), cut1, cut2, isHoldout, mode,
            ImmutableArray<RlPanelCellCount>.Empty);
        return WithCells(provisional, isHoldout, trainRequired, validationRequired, oosRequired, out shortCells);
    }

    /// <summary>
    /// End-aligned block-count geometry: <c>cut2 = L - W - S*O</c>, <c>cut1 = cut2 - W - S*V</c>. The latest <c>n</c> blocks of a partition end at
    /// its end (<c>L</c>, <c>cut2</c>, <c>cut1</c>); a block whose start would be negative is not created, and bars older than the first train block stay unused.
    /// </summary>
    private static RlPanelCatalog EvaluateBlockCount(int calendarLength, IReadOnlyList<ReadOnlyMemory<int>> presencePrefix,
        ImmutableArray<bool> isHoldout, RlPanelEvaluationMode mode, RlPanelBlockRequirement need, out ImmutableArray<RlPanelCellCount> shortCells)
    {
        ValidateInput(calendarLength, presencePrefix, isHoldout);
        int support = RlContract.SupportLength;
        var (cut1, cut2) = BlockCountCuts(calendarLength, need);
        var excluded = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<RlPanelBlock> Blocks(string name, int end, int count)
        {
            var blocks = ImmutableArray.CreateBuilder<RlPanelBlock>();
            for (int k = count; k >= 1; k--)
            {
                int start = checked(end - support * k);
                if (start >= 0 && TryBlock(presencePrefix, name, start, excluded) is { } block) blocks.Add(block);
            }
            return blocks.ToImmutable();
        }
        var train = Blocks(RlPortfolioContract.TrainPartition, cut1, need.TrainBlocks);
        var validation = Blocks(RlPortfolioContract.ValidationPartition, cut2, need.ValidationBlocks);
        var oos = Blocks(RlPortfolioContract.OosPartition, calendarLength, need.TestBlocks);
        var provisional = new RlPanelCatalog(train, validation, oos, excluded.ToImmutable(), Math.Max(cut1, 0), Math.Max(cut2, 0), isHoldout, mode,
            ImmutableArray<RlPanelCellCount>.Empty);
        return WithCells(provisional, isHoldout, need.TrainBlocks, need.ValidationBlocks, need.TestBlocks, out shortCells);
    }

    /// <summary>
    /// Smallest calendar length for which <see cref="Build"/> succeeds with two fully present symbols and no holdout, found by calling it
    /// (never a stored constant or closed form), so the preflight and the engine cannot drift. 1,278 short-history, 5,118 standard with
    /// the current constants.
    /// </summary>
    public static int MinimumCalendarLength(RlPanelEvaluationMode mode)
    {
        int needed = mode == RlPanelEvaluationMode.ShortHistory ? 1 : RlContract.EvaluationEpisodeCount;
        // Upper bound only for termination: five partitions each holding a window and the needed blocks.
        int cap = checked(RlContract.PartitionDenominator * (RlContract.WindowLength + (needed + 1) * RlContract.SupportLength));
        return FirstFeasibleLength(cap, (length, prefixes, holdout) =>
        {
            _ = Evaluate(length, prefixes, holdout, mode, out var shortCells);
            return shortCells;
        });
    }

    /// <summary>
    /// <see cref="MinimumCalendarLength(RlPanelEvaluationMode)"/> for a partition plan (short-history only), found by calling
    /// <see cref="EvaluatePlan"/>. Computed per call: the answer depends on the plan, so nothing is cached under it.
    /// </summary>
    public static int MinimumCalendarLength(RlPanelEvaluationMode mode, RlPanelPartitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (mode != RlPanelEvaluationMode.ShortHistory) throw new ArgumentException("Partition plans apply to the short-history evaluation only.", nameof(mode));
        var need = plan.Requirement;
        int widest = Math.Max(need.TrainBlocks, Math.Max(need.ValidationBlocks, need.TestBlocks));
        // Upper bound only for termination: the ratio split needs a fifth of the calendar to hold the widest partition and a window.
        int cap = checked(RlContract.PartitionDenominator * (RlContract.WindowLength + (widest + 1) * RlContract.SupportLength)
            + RlContract.SupportLength * (need.TrainBlocks + need.ValidationBlocks + need.TestBlocks));
        return FirstFeasibleLength(cap, (length, prefixes, holdout) =>
        {
            _ = EvaluatePlan(length, prefixes, holdout, mode, plan, out var shortCells);
            return shortCells;
        });
    }

    /// <summary>
    /// First calendar length up to <paramref name="cap"/> whose evaluation has no short cell, with two fully present symbols and no holdout, found by a binary
    /// search that calls the same engine function as a linear scan would. Precondition: feasibility is monotone in the length (every block count of either
    /// geometry is non-decreasing in it: <c>floor(3L/5)</c>, <c>floor(4L/5) - floor(3L/5)</c> and <c>L - floor(4L/5)</c> for the ratio split, and the end-aligned
    /// cuts grow with L for the block-count split); the tests compare the result with a linear scan.
    /// </summary>
    private static int FirstFeasibleLength(int cap,
        Func<int, IReadOnlyList<ReadOnlyMemory<int>>, ImmutableArray<bool>, ImmutableArray<RlPanelCellCount>> shortCellsOf)
    {
        var identity = Enumerable.Range(0, cap + 1).ToArray();
        var holdout = ImmutableArray.Create(false, false);
        bool Feasible(int length)
        {
            var prefix = new ReadOnlyMemory<int>(identity, 0, length + 1);
            return shortCellsOf(length, new[] { prefix, prefix }, holdout).IsEmpty;
        }
        if (!Feasible(cap)) throw new InvalidOperationException("No calendar length up to the search bound satisfies the panel catalog rule.");
        int low = 1, high = cap;                                  // the answer lies in [low, high] and high is feasible
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (Feasible(middle)) high = middle; else low = middle + 1;
        }
        return low;
    }
}
