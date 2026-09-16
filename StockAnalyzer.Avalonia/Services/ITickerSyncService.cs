using System;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Runs the shared "Sync Progress" flow (IDialogService.CreateMultiSyncProgressSession +
/// IPythonService.RunUpdatePipelineAsync) for a single ticker symbol. Extracted so both the
/// chart's "Sync Symbol" action and the Ticker Dashboard's History tab "Sync" button share one
/// implementation instead of duplicating the sync-orchestration logic.
/// </summary>
public interface ITickerSyncService
{
    /// <summary>
    /// Opens the Sync Progress window for a single symbol and runs the update pipeline.
    /// </summary>
    /// <param name="symbol">The ticker symbol to sync.</param>
    /// <param name="onSyncedAsync">Optional callback invoked after a successful sync (e.g. to reload data for display).</param>
    Task SyncSingleTickerAsync(string symbol, Func<Task>? onSyncedAsync = null);
}
