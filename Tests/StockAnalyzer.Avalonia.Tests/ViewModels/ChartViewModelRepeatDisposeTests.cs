using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// FP01: ChartViewModel.Dispose is a one-shot teardown. The first caller runs the existing cleanup sequence;
/// every later call (sequential or queued) returns without touching a resource, so the already-disposed
/// display-mode subject is never completed again.
/// </summary>
public sealed class ChartViewModelRepeatDisposeTests
{
    private static ChartViewModel Chart(IMessenger messenger, IDispatcherService dispatcher)
    {
        var data = new MockDataService();
        return new ChartViewModel(data, new DialogService(), null!, new MockStockAnalyzerSettings(), new TimeFrameManager(data),
            null!, new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            dispatcher, null, null!, null, messenger: messenger);
    }

    [Fact]
    public void Dispose_CalledTwice_CleansUpOnceAndDoesNotThrow()
    {
        var messenger = new CountingMessenger();
        var chart = Chart(messenger, new SynchronousDispatcherService());

        chart.Dispose();
        var exception = Record.Exception(chart.Dispose);

        Assert.Null(exception);
        Assert.Equal(1, messenger.UnregisterAllCalls);
    }

    [Fact]
    public void Dispose_DuplicateQueuedDispatcherCalls_CleansUpOnce()
    {
        var messenger = new CountingMessenger();
        var dispatcher = new QueueingDispatcherService();
        var chart = Chart(messenger, dispatcher);

        dispatcher.Post(chart.Dispose);
        dispatcher.Post(chart.Dispose);
        Assert.Equal(0, messenger.UnregisterAllCalls);

        var exception = Record.Exception(dispatcher.Drain);

        Assert.Null(exception);
        Assert.Equal(1, messenger.UnregisterAllCalls);
    }

    private sealed class CountingMessenger : IMessenger
    {
        private readonly StrongReferenceMessenger _inner = new();
        public int UnregisterAllCalls { get; private set; }

        public bool IsRegistered<TMessage, TToken>(object recipient, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> => _inner.IsRegistered<TMessage, TToken>(recipient, token);

        public void Register<TRecipient, TMessage, TToken>(TRecipient recipient, TToken token, MessageHandler<TRecipient, TMessage> handler)
            where TRecipient : class where TMessage : class where TToken : IEquatable<TToken> => _inner.Register(recipient, token, handler);

        public void UnregisterAll(object recipient) { UnregisterAllCalls++; _inner.UnregisterAll(recipient); }

        public void UnregisterAll<TToken>(object recipient, TToken token)
            where TToken : IEquatable<TToken> => _inner.UnregisterAll(recipient, token);

        public void Unregister<TMessage, TToken>(object recipient, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> => _inner.Unregister<TMessage, TToken>(recipient, token);

        public TMessage Send<TMessage, TToken>(TMessage message, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> => _inner.Send(message, token);

        public void Cleanup() => ((IMessenger)_inner).Cleanup();
        public void Reset() => _inner.Reset();
    }

    private sealed class QueueingDispatcherService : IDispatcherService
    {
        private readonly Queue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);
        public void Post<T>(Action<T> action, T state) => _queue.Enqueue(() => action(state));
        public Task PostAsync(Func<Task> action) => throw new NotSupportedException();
        public Task PostAsync<TState>(Func<TState, Task> action, TState state) => throw new NotSupportedException();
        public bool CheckAccess() => true;
        public void VerifyAccess() { }

        public void Drain()
        {
            while (_queue.Count > 0) _queue.Dequeue()();
        }
    }
}
