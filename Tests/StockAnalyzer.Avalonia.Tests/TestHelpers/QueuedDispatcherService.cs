using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>
/// A dispatcher double that behaves like the UI dispatcher's queue: <see cref="Post(Action)"/> only enqueues, and work
/// runs (first in, first out, including work enqueued while draining) when a test calls <see cref="RunAll"/>.
/// Use it to prove behavior that depends on callback ordering, which <see cref="Services.SynchronousDispatcherService"/>
/// cannot show because it runs every callback immediately.
/// </summary>
internal sealed class QueuedDispatcherService : IDispatcherService
{
    private readonly Queue<Action> _queue = new();

    public int PendingCount => _queue.Count;

    public void Post(Action action) => _queue.Enqueue(action);

    public void Post<T>(Action<T> action, T state) => _queue.Enqueue(() => action(state));

    /// <summary>Runs queued callbacks in order until the queue is empty.</summary>
    public void RunAll()
    {
        while (_queue.Count > 0) _queue.Dequeue()();
    }

    /// <summary>Drops queued callbacks without running them.</summary>
    public void Discard() => _queue.Clear();

    public Task PostAsync(Func<Task> action) => action();

    public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);

    public bool CheckAccess() => true;

    public void VerifyAccess() { }
}
