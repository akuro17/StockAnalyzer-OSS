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

    public void Post(Action action)
    {
        action();
    }

    public void Post<T>(Action<T> action, T state)
    {
        action(state);
    }


    public Task PostAsync(Func<Task> action)
    {
        PostAsyncCallCount++;
        return action();
    }

    public Task PostAsync<TState>(Func<TState, Task> action, TState state)
    {
        PostAsyncCallCount++;
        return action(state);
    }

    public bool CheckAccess() => true;
    public void VerifyAccess() { }
}
