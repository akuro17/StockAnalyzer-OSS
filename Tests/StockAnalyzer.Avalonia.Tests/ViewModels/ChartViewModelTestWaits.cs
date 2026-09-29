using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Deterministic waits for tests that drive a real <see cref="ChartViewModel"/>. A symbol/timeframe change starts a load (debounce, load lock, an awaited
/// save of the outgoing drawings on the thread pool, then restore), so the former fixed delay (debounce + 350 ms) raced with thread-pool latency and failed
/// intermittently under load (reproduced by saturating the thread pool). Wait for what actually happens instead of for a duration.
/// </summary>
internal static class ChartViewModelTestWaits
{
    /// <summary>Upper bound for a condition that is expected to become true; a passing run never waits for it, only a genuine failure reaches it.</summary>
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Awaits the load(s) actually in flight on each view model. A newer load can replace the one being awaited (and a superseded one ends early), so this loops
    /// until no execution is running and the view model reports it is no longer loading. Call it right after the action that starts the load
    /// (setting <see cref="ChartViewModel.Symbol"/> starts it synchronously).
    /// </summary>
    public static async Task AwaitLoadsAsync(params ChartViewModel[] viewModels)
    {
        foreach (ChartViewModel viewModel in viewModels)
        {
            while (viewModel.LoadDataCommand.ExecutionTask is { IsCompleted: false } || viewModel.IsLoading)
            {
                try
                {
                    await (viewModel.LoadDataCommand.ExecutionTask ?? Task.CompletedTask);
                }
                catch (OperationCanceledException)
                {
                    // A newer load superseded this one; the loop re-reads the command's current execution.
                }

                await Task.Delay(PollInterval);
            }
        }
    }

    /// <summary>
    /// Waits for a fire-and-forget disk write (its completion has no awaitable handle) by re-running <paramref name="assertion"/> until it passes.
    /// Only for POSITIVE conditions ("the file now holds X"): a "stays empty" check must instead await the load that would have restored the data.
    /// </summary>
    public static async Task EventuallyAsync(Action assertion)
    {
        DateTime deadline = DateTime.UtcNow + ConvergenceTimeout;
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
            }
        }
    }
}
