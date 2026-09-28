using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>The role/side partitions evaluated by <see cref="ConditionBasedBacktestStrategy"/>.</summary>
public sealed class BacktestConditionExecutionPaths
{
    private BacktestConditionExecutionPaths(
        ImmutableArray<BacktestConditionEntry> longEntry,
        ImmutableArray<BacktestConditionEntry> shortEntry,
        ImmutableArray<BacktestConditionEntry> exit,
        ImmutableArray<BacktestConditionEntry> reverseLong,
        ImmutableArray<BacktestConditionEntry> reverseShort)
    {
        LongEntry = longEntry;
        ShortEntry = shortEntry;
        Exit = exit;
        ReverseLong = reverseLong;
        ReverseShort = reverseShort;
    }

    public ImmutableArray<BacktestConditionEntry> LongEntry { get; }
    public ImmutableArray<BacktestConditionEntry> ShortEntry { get; }
    public ImmutableArray<BacktestConditionEntry> Exit { get; }
    public ImmutableArray<BacktestConditionEntry> ReverseLong { get; }
    public ImmutableArray<BacktestConditionEntry> ReverseShort { get; }

    public static BacktestConditionExecutionPaths Create(IReadOnlyList<BacktestConditionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new BacktestConditionExecutionPaths(
            entries.Where(e => e.Role != BacktestConditionRole.ExitOnly && e.Position == TradeSide.Long).ToImmutableArray(),
            entries.Where(e => e.Role != BacktestConditionRole.ExitOnly && e.Position == TradeSide.Short).ToImmutableArray(),
            entries.Where(e => e.Role == BacktestConditionRole.ExitOnly || e.Role == BacktestConditionRole.Both).ToImmutableArray(),
            entries.Where(e => e.Role == BacktestConditionRole.Reversal && e.Position == TradeSide.Long).ToImmutableArray(),
            entries.Where(e => e.Role == BacktestConditionRole.Reversal && e.Position == TradeSide.Short).ToImmutableArray());
    }
}
