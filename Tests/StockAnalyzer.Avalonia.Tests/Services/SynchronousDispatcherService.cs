using System;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// A synchronous dispatcher service for unit testing.
/// Executes all actions immediately on the calling thread.
/// </summary>
public class SynchronousDispatcherService : IDispatcherService
{
    /// <summary>Number of PostAsync calls observed (either overload) - lets tests assert a method
    /// actually routed work through the dispatcher, not just that the end state happens to be
    /// correct (which this synchronous implementation would satisfy even without being called).</summary>
    public int PostAsyncCallCount { get; private set; }

    // The real Avalonia UI dispatcher marshals every posted action onto a single UI thread, so two
    // overlapping callers (e.g. two concurrent ViewModel methods each posting a callback after their
    // own await Task.Run(...)) never have their callbacks run at the same time - one is always
    // serialized after the other. This test double instead invokes the callback immediately on
    // whichever background thread called Post, so without this lock two such callbacks could execute
    // concurrently and race on shared state (e.g. an ObservableCollection mutated by both), which the
    // real dispatcher would never allow. The lock restores that single-threaded guarantee.
    private readonly object _gate = new();

    public void Post(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }

    public void Post<T>(Action<T> action, T state)
    {
        lock (_gate)
        {
            action(state);
        }
    }


    /// <summary>Runs <paramref name="action"/> as if on the UI thread: serialized with every Post callback.
    /// Tests use it for the calls production makes on the UI thread (e.g. setting a filter text) so they
    /// cannot interleave with a still-running fire-and-forget refresh whose callback this double runs on
    /// a background thread.</summary>
    public void Run(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }

    /// <summary>Value-returning form of <see cref="Run(Action)"/>, for reading state that a Post callback
    /// may be rewriting on a background thread.</summary>
    public T Run<T>(Func<T> func)
    {
        lock (_gate)
        {
            return func();
        }
    }

    public Task PostAsync(Func<Task> action)
    {
        PostAsyncCallCount++;
        lock (_gate)
        {
            return action();
        }
    }

    public Task PostAsync<TState>(Func<TState, Task> action, TState state)
    {
        PostAsyncCallCount++;
        lock (_gate)
        {
            return action(state);
        }
    }

    public bool CheckAccess() => true;
    public void VerifyAccess() { }
}
