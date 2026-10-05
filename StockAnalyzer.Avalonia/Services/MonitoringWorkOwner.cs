using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Owns every background monitoring task (forecast record, observation refresh, scheduler runner, health evaluation,
/// notification delivery) so that application shutdown can cancel and then await all of them instead of
/// remembering only the latest task of each kind.
/// </summary>
public interface IMonitoringWorkOwner
{
    /// <summary>
    /// Schedules <paramref name="work"/> on the thread pool and tracks it until it completes. Once the owner is stopping, or when
    /// <paramref name="cancellationToken"/> is already canceled, a canceled task is returned and nothing is scheduled.
    /// </summary>
    Task Run(Func<CancellationToken, Task> work, CancellationToken cancellationToken);

    /// <summary>
    /// Rejects new work, cancels the registered work and completes after every registered task has finished. There is no timeout:
    /// a worker that never ends keeps the shutdown pending, because releasing its resources early would be unsafe.
    /// The same task is returned by every call.
    /// </summary>
    Task StopAsync();
}

/// <inheritdoc />
public sealed class MonitoringWorkOwner : IMonitoringWorkOwner
{
    private enum Phase { Running, Stopping, Stopped }

    // One gate guards the phase, the registry and the stop completion. Nothing that can run user code is invoked while it is held.
    private readonly object _gate = new();
    private readonly HashSet<Task> _active = [];
    private readonly CancellationTokenSource _stop = new();
    private Phase _phase = Phase.Running;
    private Task? _stopCompletion;

    /// <inheritdoc />
    public Task Run(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_phase != Phase.Running) return Task.FromCanceled(new CancellationToken(true));
            linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            _active.Add(finished.Task);
        }
        // Registered before scheduling, so a Stop that snapshots now already waits for this item. The delegate always runs; it
        // starts no work once cancellation has been requested.
        var task = Task.Run(async () =>
        {
            linked.Token.ThrowIfCancellationRequested();
            await work(linked.Token).ConfigureAwait(false);
        });
        // The registration ends when the task handed to the caller has completed, not when the delegate body ends: otherwise a
        // Stop could complete while that task is still finishing. The continuation has no cancellation and no completion filter,
        // so it runs for every outcome of the task.
        _ = task.ContinueWith(_ =>
        {
            lock (_gate) _active.Remove(finished.Task);
            linked.Dispose(); // its only user has finished
            finished.SetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task[] pending;
        lock (_gate)
        {
            if (_stopCompletion is { } existing) return existing;
            _phase = Phase.Stopping;
            pending = [.. _active];
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopCompletion = completion.Task;
        }
        _ = DrainAsync(pending, completion);
        return completion.Task;
    }

    private async Task DrainAsync(Task[] pending, TaskCompletionSource completion)
    {
        try
        {
            // Cancellation callbacks run here, outside the gate. A callback that faults must not stop the drain.
            try { _stop.Cancel(); }
            catch (AggregateException) { }
            // The registered tasks only ever complete successfully (see Run), so this never throws.
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _phase = Phase.Stopped;
            _stop.Dispose(); // every linked source (the only users of its token) has finished by now
            completion.SetResult();
        }
    }
}
