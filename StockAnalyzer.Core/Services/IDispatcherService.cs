using System;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Abstraction for UI thread dispatching to decouple ViewModels from UI frameworks.
/// </summary>
public interface IDispatcherService
{
    /// <summary>
    /// Posts an action to the UI thread for execution (fire-and-forget).
    /// </summary>
    void Post(Action action);
    void Post<T>(Action<T> action, T state);


    /// <summary>
    /// Invokes an action on the UI thread asynchronously and returns a task that completes when the action is done.
    /// Supports asynchronous actions (Func&lt;Task&gt;).
    /// </summary>
    Task PostAsync(Func<Task> action);

    /// <summary>
    /// Invokes an action on the UI thread asynchronously with state and returns a task that completes when the action is done.
    /// Supports asynchronous actions (Func&lt;TState, Task&gt;) to avoid closure allocations.
    /// </summary>
    Task PostAsync<TState>(Func<TState, Task> action, TState state);

    /// <summary>
    /// Checks whether the current thread is the UI thread.
    /// </summary>
    bool CheckAccess();

    /// <summary>
    /// Verifies that the current thread is the UI thread, throwing an exception if it is not.
    /// </summary>
    void VerifyAccess();
}
