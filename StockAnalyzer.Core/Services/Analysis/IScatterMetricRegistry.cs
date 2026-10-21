using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>Outcome of <see cref="IScatterMetricRegistry.ReplaceAllAsync"/>.</summary>
public enum ScatterMetricRegistryResult
{
    /// <summary>The list was stored (or was already stored: an identical list is accepted without a write).</summary>
    Ok,
    /// <summary>An entry fails <see cref="ScatterMetricRules.Validate"/>, or two entries share an Id. Nothing changed.</summary>
    Invalid,
    /// <summary>More entries than <c>ScatterMetrics:MaxRegisteredMetrics</c>. Nothing changed.</summary>
    LimitReached,
    /// <summary>The file could not be written. Nothing changed (neither the file's content in memory nor the event).</summary>
    PersistFailed,
}

/// <summary>
/// The confirmed ("Active") scatter metrics, in order. It holds committed state only: the registration screen edits a draft and hands the whole
/// list over with <see cref="ReplaceAllAsync"/> when the user presses OK, so there is no per-entry mutator.
/// </summary>
public interface IScatterMetricRegistry
{
    /// <summary>The confirmed metrics in Active order, as independent copies. Empty until <see cref="Loaded"/> completes.</summary>
    IReadOnlyList<ScatterMetricDefinition> Snapshot();

    /// <summary>Completes when the first read of the file has finished (it never faults: a read problem leaves the list empty and is logged).</summary>
    Task Loaded { get; }

    /// <summary>Entries the first read dropped as invalid; returns the count once and 0 afterwards.</summary>
    int TakeLoadDroppedCount();

    /// <summary>
    /// Replaces the whole confirmed list. Waits for <see cref="Loaded"/> first. Checked in this order: more entries than the configured limit
    /// (<see cref="ScatterMetricRegistryResult.LimitReached"/>); an entry that fails <see cref="ScatterMetricRules.Validate"/> or a repeated Id
    /// (<see cref="ScatterMetricRegistryResult.Invalid"/>); then the write. Entries with the same content are accepted. The memory changes only after the write succeeded.
    /// </summary>
    Task<ScatterMetricRegistryResult> ReplaceAllAsync(IReadOnlyList<ScatterMetricDefinition> metrics);

    /// <summary>
    /// Raised (on the thread that finished the work, outside every lock) after the confirmed list changed in its Id order or in the content of
    /// an entry, and once when the first read found at least one metric. Handlers that touch the UI must marshal themselves.
    /// A subscriber that attaches after <see cref="Loaded"/> has completed has missed the first-read event and must read <see cref="Snapshot"/> itself.
    /// A handler that throws is logged and does not affect the others or the change.
    /// </summary>
    event EventHandler? Changed;
}
