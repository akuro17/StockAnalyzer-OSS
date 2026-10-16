using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// Closing the Screener window stops its running scan and releases the view-model's subscriptions (the view-model is created per window).
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class ScreenerWindowCloseTests
{
    /// <summary>A screener whose scan never ends by itself: it only reports when it was cancelled.</summary>
    private sealed class HangingScreenerService : IScreenerService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<List<string>> ScreenAsync(ScreeningCriteria criteria, IProgress<int> progress, CancellationToken ct)
            => throw new NotSupportedException();

        public async Task<List<string>> ScreenAsync(List<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, IProgress<int> progress, CancellationToken ct)
        {
            Started.TrySetResult();
            // Registered, not awaited: the cancellation is observed at the moment it is requested, on the caller's thread.
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, ct);
            return new List<string>();
        }
    }

    /// <summary>A watchlist manager that counts how many handlers are subscribed to <see cref="WatchlistsChanged"/>.</summary>
    private sealed class SubscriberCountingWatchlistManager : IWatchlistManager
    {
        private EventHandler? _handlers;
        public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        public event EventHandler? WatchlistsChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public IReadOnlyList<WatchlistProfile> GetAllProfiles() => Array.Empty<WatchlistProfile>();
        public WatchlistProfile? GetProfileById(Guid profileId) => null;
        public WatchlistProfile CreateProfile(string name, IndicatorColor color, bool isPortfolio = false) => throw new NotSupportedException();
        public void UpdateProfileName(Guid profileId, string name) { }
        public void DeleteProfile(Guid profileId) { }
        public void AddTickerToProfile(Guid profileId, string ticker) { }
        public void AddTickersToProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickerFromProfile(Guid profileId, string ticker) { }
        public void RemoveTickersFromProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickersFromAllProfiles(IEnumerable<string> tickers) { }
        public void Initialize(IEnumerable<WatchlistProfile> profiles) { }
    }

    private static void RegisterOneEnabledEntry(ScreenerViewModel vm)
        => vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.RSI },
            Operator = ComparisonOperator.LessThan,
            RightNumericValue = 30m,
            IsEnabled = true
        });

    private static async Task<bool> CompletesWithin(Task task, TimeSpan limit)
        => await Task.WhenAny(task, Task.Delay(limit)) == task;

    [Fact]
    public async Task Dispose_WhileScanning_CancelsTheScan_AndTheScanEndsCleanly()
    {
        var service = new HangingScreenerService();
        var vm = new ScreenerViewModel(service);
        RegisterOneEnabledEntry(vm);

        Task scan = vm.ScanCommand.ExecuteAsync(null);
        Assert.True(await CompletesWithin(service.Started.Task, TimeSpan.FromSeconds(2)), "The scan did not start.");

        vm.Dispose();

        Assert.True(await CompletesWithin(service.Cancelled.Task, TimeSpan.FromSeconds(2)), "Dispose did not cancel the running scan.");
        // The scan's own cleanup runs after Dispose already cleared its cancellation source: it must neither throw nor hang.
        Assert.True(await CompletesWithin(scan, TimeSpan.FromSeconds(2)), "The cancelled scan did not finish.");
        await scan;
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var vm = new ScreenerViewModel(new HangingScreenerService());

        vm.Dispose();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void ClosingTheWindow_DisposesTheViewModel_AndReleasesTheWatchlistSubscription()
    {
        var watchlists = new SubscriberCountingWatchlistManager();
        var vm = new ScreenerViewModel(watchlistManager: watchlists);
        var window = new ScreenerWindow { DataContext = vm };
        try
        {
            window.Show();
            Assert.Equal(1, watchlists.SubscriberCount);

            window.Close();

            Assert.Equal(0, watchlists.SubscriberCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClosingTheWindow_WhileScanning_CancelsTheScan()
    {
        var service = new HangingScreenerService();
        var vm = new ScreenerViewModel(service);
        RegisterOneEnabledEntry(vm);
        var window = new ScreenerWindow { DataContext = vm };
        try
        {
            window.Show();
            _ = vm.ScanCommand.ExecuteAsync(null);
            Assert.True(service.Started.Task.IsCompleted);

            window.Close();

            Assert.True(service.Cancelled.Task.Wait(TimeSpan.FromSeconds(2)), "Closing the window did not cancel the running scan.");
        }
        finally { window.Close(); }
    }

    // The footer Cancel button is bound to $parent[Window].Close. A command that stays null would make it a dead button (the same defect as the Escape KeyBinding).
    [AvaloniaFact]
    public void CancelButton_Command_IsResolved_AndClosesWindow()
    {
        var window = new ScreenerWindow { DataContext = new ScreenerViewModel() };
        try
        {
            window.Show();
            string cancelText = LocalizationManager.Instance["Btn_Cancel"];
            var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, cancelText));

            Assert.NotNull(cancel.Command);
            cancel.Command!.Execute(null);

            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }
}
