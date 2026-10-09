using System;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// How a short-history panel is cut into train / validation / test blocks. <see cref="Ratio"/> is the 3:1:1 split of the calendar (the
/// default; unchanged); <see cref="BlockCount"/> counts a fixed number of blocks back from the latest date, so it needs far fewer bars.
/// </summary>
public enum RlPortfolioPartitionMode { Ratio = 0, BlockCount = 1 }

/// <summary>Minimum bars the user asked for per partition (a setting value, before rounding up to whole blocks).</summary>
public sealed record RlPanelMinimumBars(int Train, int Validation, int Test)
{
    /// <summary>Each partition holds complete blocks of <see cref="RlContract.SupportLength"/> bars, so a minimum is rounded up to whole blocks.</summary>
    public RlPanelBlockRequirement ToRequirement() => RlPanelBlockRequirement.FromBars(Train, Validation, Test);
}

/// <summary>Complete blocks required per partition (each at least one). The single owner of the bars-to-blocks conversion.</summary>
public sealed record RlPanelBlockRequirement
{
    public RlPanelBlockRequirement(int trainBlocks, int validationBlocks, int testBlocks)
    {
        if (trainBlocks < 1) throw new ArgumentOutOfRangeException(nameof(trainBlocks), trainBlocks, "At least one block is required.");
        if (validationBlocks < 1) throw new ArgumentOutOfRangeException(nameof(validationBlocks), validationBlocks, "At least one block is required.");
        if (testBlocks < 1) throw new ArgumentOutOfRangeException(nameof(testBlocks), testBlocks, "At least one block is required.");
        TrainBlocks = trainBlocks; ValidationBlocks = validationBlocks; TestBlocks = testBlocks;
    }

    public int TrainBlocks { get; }
    public int ValidationBlocks { get; }
    public int TestBlocks { get; }

    /// <summary>The requirement of the default short-history rule: one complete block per partition.</summary>
    public static RlPanelBlockRequirement OneBlockEach { get; } = new(1, 1, 1);

    /// <summary>The pinned requirement of the Standard evaluation: one train block, <see cref="RlContract.EvaluationEpisodeCount"/> validation and test blocks.</summary>
    public static RlPanelBlockRequirement Standard { get; } = new(1, RlContract.EvaluationEpisodeCount, RlContract.EvaluationEpisodeCount);

    /// <summary>The requirement of an evaluation mode when no partition plan is given.</summary>
    public static RlPanelBlockRequirement Of(RlPanelEvaluationMode mode) => mode == RlPanelEvaluationMode.ShortHistory ? OneBlockEach : Standard;

    /// <summary>Blocks needed to hold at least <paramref name="bars"/> bars: <c>ceil(bars / SupportLength)</c>.</summary>
    public static int BlocksFor(int bars)
    {
        if (bars < 1) throw new ArgumentOutOfRangeException(nameof(bars), bars, "At least one bar is required.");
        return checked((bars + RlContract.SupportLength - 1) / RlContract.SupportLength);
    }

    public static RlPanelBlockRequirement FromBars(int trainBars, int validationBars, int testBars)
        => new(BlocksFor(trainBars), BlocksFor(validationBars), BlocksFor(testBars));

    public static RlPanelBlockRequirement FromSettings(RlPortfolioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return FromBars(settings.MinimumTrainBars, settings.MinimumValidationBars, settings.MinimumTestBars);
    }

    /// <summary>Bars of whole blocks (<c>blocks * SupportLength</c>).</summary>
    public static int BarsOf(int blocks) => checked(blocks * RlContract.SupportLength);
}

/// <summary>
/// Partition method and block counts of one short-history run. <see cref="Bars"/> keeps the minimum the user stated (evidence and display);
/// the geometry only reads <see cref="Requirement"/>. A <c>null</c> plan means "today's behaviour of the evaluation mode" everywhere.
/// </summary>
public sealed record RlPanelPartitionPlan(RlPortfolioPartitionMode Mode, RlPanelBlockRequirement Requirement, RlPanelMinimumBars? Bars = null)
{
    public static RlPanelPartitionPlan FromSettings(RlPortfolioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bars = new RlPanelMinimumBars(settings.MinimumTrainBars, settings.MinimumValidationBars, settings.MinimumTestBars);
        return new RlPanelPartitionPlan(settings.PartitionMode, bars.ToRequirement(), bars);
    }

    /// <summary>True for the 3:1:1 ratio split with one block per partition: exactly the short-history rule without a plan.</summary>
    public bool IsDefault => Mode == RlPortfolioPartitionMode.Ratio && Requirement == RlPanelBlockRequirement.OneBlockEach;

    /// <summary>
    /// How often the training budget of one seed replays each train block of a <see cref="RlPortfolioPartitionMode.BlockCount"/> plan
    /// (integer division; zero when the budget cannot visit every block once).
    /// </summary>
    public int TrainingBlockReplays => RlContinuousContract.TrainingTransitionsPerSeed / (Requirement.TrainBlocks * RlContract.EpisodeHorizon);
}

/// <summary>What a symbol does in a panel run, derived from the episode catalog (never typed by hand).</summary>
public enum RlPanelSymbolRole { None = 0, EvaluatedOnly = 1, Trains = 2 }

public static class RlPanelSymbolRoles
{
    /// <summary>
    /// <see cref="RlPanelSymbolRole.Trains"/> when the symbol is eligible in at least one train block, else
    /// <see cref="RlPanelSymbolRole.EvaluatedOnly"/> when it is eligible in a validation or test block (the policy trades it but never trained on it),
    /// else <see cref="RlPanelSymbolRole.None"/>.
    /// </summary>
    public static RlPanelSymbolRole Of(RlPanelCatalog catalog, int symbolIndex)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (symbolIndex < 0) throw new ArgumentOutOfRangeException(nameof(symbolIndex));
        bool In(System.Collections.Immutable.ImmutableArray<RlPanelBlock> blocks) => System.Linq.Enumerable.Any(blocks, b => b.EligibleSymbols.Contains(symbolIndex));
        return In(catalog.Train) ? RlPanelSymbolRole.Trains
            : In(catalog.Validation) || In(catalog.Oos) ? RlPanelSymbolRole.EvaluatedOnly
            : RlPanelSymbolRole.None;
    }
}
