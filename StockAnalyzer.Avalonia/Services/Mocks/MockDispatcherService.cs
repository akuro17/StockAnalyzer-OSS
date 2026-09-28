using System;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services.Mocks;

/// <summary>
/// Immediate execution dispatcher for design-time previews and unit tests.
/// </summary>
public class DesignTimeDispatcher : IDispatcherService
{
    public void Post(Action action) => action();
    public void Post<T>(Action<T> action, T state) => action(state);
    public Task PostAsync(Func<Task> action) => action();
    public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);

    public bool CheckAccess() => true;
    public void VerifyAccess() { }
}
