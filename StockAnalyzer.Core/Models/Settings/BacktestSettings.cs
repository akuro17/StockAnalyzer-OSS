using System;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Backtest tunables bound from the "Backtest" section of appsettings.json (via <c>IStockAnalyzerSettings</c>).
/// The default lives here only; the interface default and the configuration file both follow it.
/// </summary>
public class BacktestSettings
{
    /// <summary>
    /// Largest Offset (bars before the evaluated bar) a backtest condition may use. One value serves the Offset input boxes and the saved-configuration
    /// validation, so a value the UI cannot express is never loaded (and silently clamped) from a file. The lower bound is not a tunable:
    /// it is the causality invariant <see cref="BacktestConditionOffsetRule.MinOffset"/>.
    /// </summary>
    public int MaxConditionOffset { get; set; } = 500;

    /// <summary>
    /// Deepest AND/OR nesting a backtest condition tree may have: the root group is depth 1, every nested group adds 1, leaves add nothing.
    /// A saved file exceeding it loads as an error (never clamped). The lower bound is <see cref="BacktestConditionTreeRule.MinBound"/>.
    /// Rationale of the default: a readable and editable tree in the editor and a bounded cost of the validation and test matrix; both limits are tunable.
    /// A file written before the tree existed (schema 1) is migrated and is not bound by these limits.
    /// </summary>
    public int MaxConditionTreeDepth { get; set; } = 8;

    /// <summary>Most nodes (leaves and groups, root included) one condition-tree root may contain. Same load-error rule and rationale as <see cref="MaxConditionTreeDepth"/>.</summary>
    public int MaxConditionTreeNodes { get; set; } = 64;

    /// <summary>
    /// Longest user-entered name (in characters, after trimming) a nested condition group may have. A saved file exceeding it loads as an error (never truncated);
    /// the editor's name input takes its MaxLength from this value. Rationale of the default: a name is a short label shown in tree rows and tooltips.
    /// </summary>
    public int MaxConditionGroupNameLength { get; set; } = 32;

    /// <summary>
    /// Distance (device-independent pixels, along either axis) a pressed pointer must travel over a condition row before the press becomes a drag that moves the condition.
    /// Below it the press stays a plain click (select / open / close). Rationale of the default: a few DIPs tolerate a hand tremor without delaying the drag.
    /// </summary>
    public int ConditionDragStartDistance { get; set; } = 4;

    public void Validate()
    {
        if (MaxConditionTreeDepth < BacktestConditionTreeRule.MinBound)
        {
            throw new InvalidOperationException($"BacktestSettings: MaxConditionTreeDepth must be >= {BacktestConditionTreeRule.MinBound}.");
        }
        if (MaxConditionTreeNodes < BacktestConditionTreeRule.MinBound)
        {
            throw new InvalidOperationException($"BacktestSettings: MaxConditionTreeNodes must be >= {BacktestConditionTreeRule.MinBound}.");
        }

        if (MaxConditionGroupNameLength < BacktestConditionTreeRule.MinBound)
        {
            throw new InvalidOperationException($"BacktestSettings: MaxConditionGroupNameLength must be >= {BacktestConditionTreeRule.MinBound}.");
        }

        if (ConditionDragStartDistance < BacktestConditionTreeRule.MinBound)
        {
            throw new InvalidOperationException($"BacktestSettings: ConditionDragStartDistance must be >= {BacktestConditionTreeRule.MinBound}.");
        }

        if (MaxConditionOffset < BacktestConditionOffsetRule.MinOffset)
        {
            throw new InvalidOperationException($"BacktestSettings: MaxConditionOffset must be >= {BacktestConditionOffsetRule.MinOffset}.");
        }
    }
}
