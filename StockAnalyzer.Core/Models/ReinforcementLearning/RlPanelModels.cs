using System;
using System.Collections.Immutable;
using System.Linq;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Evaluation minimum of a panel run: the standard episode count per partition, or one complete block (short history).</summary>
public enum RlPanelEvaluationMode { Standard, ShortHistory }

/// <summary>Symbols a policy is trained on (Train) or never trains on (Holdout). Without a symbol holdout every symbol is Train.</summary>
public enum RlPanelSymbolGroup { Train, Holdout }

/// <summary>One source symbol of a panel: the hash of its immutable single-symbol source manifest and its stored extent.</summary>
public sealed record RlPanelSymbolEntry(string Symbol, string SourceManifestSha256, int BarCount, DateOnly FirstDate, DateOnly LastDate);

/// <summary>
/// Identity and evidence of one immutable multi-symbol source snapshot. Every derived value (calendar, split, hashes) is recomputed on
/// load and must equal what is stored here.
/// </summary>
public sealed record RlPanelManifest(int SchemaVersion, string SpecificationId, ImmutableArray<RlPanelSymbolEntry> Symbols,
    string Currency, string ExchangeTimezone, DateOnly CapturedExchangeDate, DateOnly PeriodStartInclusive, DateOnly PeriodEndExclusive,
    string CalendarSha256, int CalendarLength, string MembershipSha256, uint SplitSeed, bool HoldoutEnabled, int HoldoutPercent,
    ImmutableArray<string> HoldoutSymbols, string UniverseSha256);

/// <summary>An episode block of the shared calendar and the symbols (indices into the canonical order) that have a bar on every index of it.</summary>
public sealed record RlPanelBlock(string Id, string Partition, int StartIndex, int EndExclusive, ImmutableArray<int> EligibleSymbols);

/// <summary>Complete blocks of one evaluation cell against the minimum the run needs; a Holdout cell below its minimum is "not evaluable", never dropped silently.</summary>
public sealed record RlPanelCellCount(string Partition, RlPanelSymbolGroup Group, int BlockCount, int RequiredBlocks)
{
    public bool Evaluable => BlockCount >= RequiredBlocks;
}

/// <summary>
/// Episode blocks of a panel by partition. A block is listed when at least two symbols have a bar on every index of it; whether it is
/// usable for a cell (partition x symbol group) is decided per cell by <see cref="CellBlocks"/>.
/// </summary>
public sealed record RlPanelCatalog(ImmutableArray<RlPanelBlock> Train, ImmutableArray<RlPanelBlock> Validation,
    ImmutableArray<RlPanelBlock> Oos, ImmutableArray<string> Excluded, int TrainCut, int ValidationCut,
    ImmutableArray<bool> IsHoldout, RlPanelEvaluationMode Mode, ImmutableArray<RlPanelCellCount> Cells)
{
    public ImmutableArray<RlPanelBlock> Partition(string partition) => partition switch
    {
        RlPortfolioContract.TrainPartition => Train,
        RlPortfolioContract.ValidationPartition => Validation,
        RlPortfolioContract.OosPartition => Oos,
        _ => throw new ArgumentException("Unknown partition.", nameof(partition)),
    };

    /// <summary>Symbols of <paramref name="block"/> that belong to <paramref name="group"/>.</summary>
    public ImmutableArray<int> EligibleIn(RlPanelBlock block, RlPanelSymbolGroup group)
    {
        ArgumentNullException.ThrowIfNull(block);
        return block.EligibleSymbols.Where(i => IsHoldout[i] == (group == RlPanelSymbolGroup.Holdout)).ToImmutableArray();
    }

    /// <summary>Blocks of the partition with at least two eligible symbols of the group.</summary>
    public ImmutableArray<RlPanelBlock> CellBlocks(string partition, RlPanelSymbolGroup group)
        => Partition(partition).Where(b => EligibleIn(b, group).Length >= RlPortfolioContract.MinimumBlockSymbols).ToImmutableArray();
}
