using System;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Avalonia implementation of IDispatcherService.
/// </summary>
public class DispatcherService : IDispatcherService
{
    public void Post(Action action)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public void Post<T>(Action<T> action, T state)
    {
        // Use the stateful overload to avoid closure allocations.
        // We use a static lambda to ensure no capture of 'this' or local variables.
        // The only remaining allocation is the boxing of the state tuple.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(static s => 
        {
            var tuple = ((Action<T>, T))s!;
            tuple.Item1(tuple.Item2);
        }, (action, state));
    }



    public Task PostAsync(Func<Task> action)
    {
        return global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
    }

    public Task PostAsync<TState>(Func<TState, Task> action, TState state)
    {
        // Use a standard non-capturing adapter since InvokeAsync doesn't accept a state object directly,
        // but passing it inside a closure is extremely clean.
        return global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => action(state));
    }

    public bool CheckAccess()
    {
        return global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
    }

    public void VerifyAccess()
    {
        global::Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
    }
}
