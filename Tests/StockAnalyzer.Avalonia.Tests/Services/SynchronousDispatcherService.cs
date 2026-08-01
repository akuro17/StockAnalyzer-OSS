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
        return action();
    }

    public Task PostAsync<TState>(Func<TState, Task> action, TState state)
    {
        return action(state);
    }

    public bool CheckAccess() => true;
    public void VerifyAccess() { }
}
