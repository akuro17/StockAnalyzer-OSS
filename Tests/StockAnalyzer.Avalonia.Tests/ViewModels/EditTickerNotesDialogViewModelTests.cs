using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

// Sends TickerDataRefreshedMessage on the shared static WeakReferenceMessenger.Default using
// symbol names that overlap with TickerListViewModelTests (see MessengerSharedStateCollection.cs).
[Collection("MessengerSharedState")]
public class EditTickerNotesDialogViewModelTests
{
    /// <summary>Minimal ITickerSyncService test double: records which symbol it was asked to sync
    /// and invokes the post-sync callback synchronously (as the real dispatcher-backed
    /// implementation would once the underlying sync work completes).</summary>
    private class FakeTickerSyncService : ITickerSyncService
    {
        public List<string> CalledSymbols { get; } = new();

        public Task SyncSingleTickerAsync(string symbol, Func<Task>? onSyncedAsync = null)
        {
            CalledSymbols.Add(symbol);
            return onSyncedAsync?.Invoke() ?? Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SyncHistoryCommand_WhenSymbolEmpty_DoesNotThrow()
    {
        // Arrange: an empty Ticker yields an empty Symbol.
        var vm = new EditTickerNotesDialogViewModel(string.Empty);

        // Act & Assert: the guard clause must return before touching the sync service.
        await vm.SyncHistoryCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SyncHistoryCommand_WhenSyncServiceUnavailable_DoesNotThrow()
    {
        // Arrange: no ITickerSyncService was supplied to the constructor (defaults to null) —
        // the command must no-op safely rather than throw.
        var vm = new EditTickerNotesDialogViewModel("AAPL");

        // Act & Assert
        await vm.SyncHistoryCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SyncHistoryCommand_WhenServiceAvailable_InvokesSyncForCurrentSymbol()
    {
        // Arrange: constructor-injected fake, exactly as DialogService/ActivatorUtilities would
        // supply the real ITickerSyncService in production.
        var syncService = new FakeTickerSyncService();
        var vm = new EditTickerNotesDialogViewModel("AAPL", tickerSyncService: syncService);

        try
        {
            // Act
            await vm.SyncHistoryCommand.ExecuteAsync(null);

            // Assert
            Assert.Equal(new[] { "AAPL" }, syncService.CalledSymbols);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SyncHistoryCommand_AfterDispose_DoesNotRun()
    {
        // Arrange: guards against "Ghost" operations if the Sync Progress window outlives the dialog.
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        vm.Dispose();

        // Act & Assert: must no-op safely, not throw.
        await vm.SyncHistoryCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task DeleteHistoryFromSelectedCommand_AfterDispose_DoesNotRun()
    {
        // Arrange: a selection exists (so CanExecute would otherwise be true) but the VM is disposed.
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        vm.SelectedHistoryRow = new CandleData(new DateTime(2024, 1, 1), 10m, 11m, 9m, 10.5m, 1000);
        vm.Dispose();

        // Act & Assert: must no-op safely, not throw or reach the confirmation/deletion path.
        await vm.DeleteHistoryFromSelectedCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Regression test for the "Ticker Dashboard" crash: opening the dialog for a ticker with no
    /// saved strategy metadata leaves longVal/exitLong/stopLossLong/shortVal/exitShort/stopLossShort/
    /// notes all null. DialogService used to resolve this VM via
    /// ActivatorUtilities.CreateInstance(serviceProvider, ticker, longVal, ...) — boxing a null
    /// Nullable&lt;decimal&gt; loses its runtime type, so ActivatorUtilities could not map it to a
    /// specific constructor parameter and threw InvalidOperationException, crashing the app.
    /// DialogService now resolves the three service dependencies itself and calls this constructor
    /// directly, exactly as exercised here.
    /// </summary>
    [Fact]
    public void Constructor_DIResolvedServices_WithAllNullPriceAndNotesArgs_DoesNotThrow()
    {
        // Arrange: build the same DI container DialogService resolves EditTickerNotesDialogViewModel's
        // dependencies from.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        var provider = services.BuildServiceProvider();

        // Act & Assert: must not throw, matching DialogService.ShowEditTickerNotesDialogAsync's call site.
        var vm = new EditTickerNotesDialogViewModel(
            "AAPL", null, null, null, null, null, null, null,
            onApplyCallback: null,
            marketDataProvider: provider.GetService<IMarketDataProvider>(),
            dialogService: provider.GetService<IDialogService>(),
            tickerSyncService: provider.GetService<ITickerSyncService>());

        Assert.Equal("AAPL", vm.Ticker);
    }

    [Fact]
    public void Constructor_InitializesPropertiesCorrectly()
    {
        // Arrange & Act
        var vm = new EditTickerNotesDialogViewModel("AAPL", 150.50m, 200.00m, 140.00m, reminder: "Test strategy notes");

        // Assert
        Assert.Equal("AAPL", vm.Ticker);
        Assert.Equal("150.50", vm.EntryPriceText);
        Assert.Equal("200.00", vm.TargetPriceText);
        Assert.Equal("140.00", vm.StopLossText);
        Assert.Equal("Test strategy notes", vm.Reminder);
        Assert.Equal(150.50m, vm.EntryPrice);
        Assert.Equal(200.00m, vm.TargetPrice);
        Assert.Equal(140.00m, vm.StopLoss);
    }

    [Fact]
    public void Constructor_WhenPassedNullPrices_RestoresPricesFromUserStrategyMetadataRepository()
    {
        // Arrange: seed strategy into repository
        var ticker = "TEST_RESTORE_" + Guid.NewGuid().ToString("N")[..6];
        StockAnalyzer.Core.Services.UserStrategyMetadataRepository.Instance.RegisterLoadedStrategy(
            ticker, 123.45m, 234.56m, 100.00m, 345.67m, 300.00m, 360.00m, "Initial notes",
            isLong: true, isTPLong: false, isSLLong: false, isShort: false, isTPShort: false, isSLShort: false);

        // Act: construct dialog VM with null prices
        var vm = new EditTickerNotesDialogViewModel(ticker);

        // Assert: price text properties should be restored from UserStrategyMetadataRepository
        Assert.Equal("123.45", vm.LongText);
        Assert.Equal("234.56", vm.ExitLongText);
        Assert.Equal("100.00", vm.StopLossLongText);
        Assert.Equal("345.67", vm.ShortText);
        Assert.Equal("300.00", vm.ExitShortText);
        Assert.Equal("360.00", vm.StopLossShortText);
        Assert.Equal(123.45m, vm.Long);
        Assert.Equal(234.56m, vm.ExitLong);
        Assert.Equal(100.00m, vm.StopLossLong);
        Assert.Equal(345.67m, vm.Short);
        Assert.Equal(300.00m, vm.ExitShort);
        Assert.Equal(360.00m, vm.StopLossShort);
    }

    [Fact]
    public async Task SaveCommand_TriggersCloseActionWithTrue()
    {
        // Arrange
        var vm = new EditTickerNotesDialogViewModel("MSFT", null, 400.00m, 350.00m, reminder: "Bullish momentum");
        bool? result = null;
        vm.CloseAction = res => result = res;

        // Act: SaveCommand remains an async command (see PersistStrategyAsync).
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CancelCommand_TriggersCloseActionWithFalse()
    {
        // Arrange
        var vm = new EditTickerNotesDialogViewModel("NVDA");
        bool? result = null;
        vm.CloseAction = res => result = res;

        // Act
        vm.CancelCommand.Execute(null);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ApplyCommand_TriggersOnApplyCallbackWithoutClosing()
    {
        // Arrange
        bool applied = false;
        var vm = new EditTickerNotesDialogViewModel("AMZN", 120.00m, 160.00m, 100.00m, "Growth plan", (e, t, s, n) => applied = true);
        bool? closed = null;
        vm.CloseAction = res => closed = res;

        // Act: ApplyCommand remains an async command (see PersistStrategyAsync).
        await vm.ApplyCommand.ExecuteAsync(null);

        // Assert
        Assert.True(applied);
        Assert.Null(closed);
    }

    [Fact]
    public void SymbolChanged_WhenSyncEnabled_BroadcastsTickerSelectedMessage()
    {
        // Arrange
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        vm.IsSyncEnabled = true;
        string? received = null;
        var probe = new object();
        WeakReferenceMessenger.Default.Register<TickerSelectedMessage>(probe, (r, m) => received = m.Value);

        try
        {
            // Act
            vm.Symbol = "MSFT";

            // Assert
            Assert.Equal("MSFT", received);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<TickerSelectedMessage>(probe);
            vm.Dispose();
        }
    }

    [Fact]
    public void SymbolChanged_WhenSyncDisabled_DoesNotBroadcastTickerSelectedMessage()
    {
        // Arrange
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        string? received = null;
        var probe = new object();
        WeakReferenceMessenger.Default.Register<TickerSelectedMessage>(probe, (r, m) => received = m.Value);

        try
        {
            // Act
            vm.Symbol = "MSFT";

            // Assert
            Assert.Null(received);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<TickerSelectedMessage>(probe);
            vm.Dispose();
        }
    }

    [Fact]
    public void ReceivingTickerSelectedMessage_WhenSyncEnabled_DoesNotReBroadcast()
    {
        // Arrange
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        vm.IsSyncEnabled = true;
        int sendCount = 0;
        var probe = new object();
        WeakReferenceMessenger.Default.Register<TickerSelectedMessage>(probe, (r, m) => sendCount++);

        try
        {
            // Act: simulate an external broadcast (e.g. main window ticker selection)
            WeakReferenceMessenger.Default.Send(new TickerSelectedMessage("GOOG"));

            // Assert: the dashboard follows the incoming symbol...
            Assert.Equal("GOOG", vm.Symbol);
            // ...but must not re-broadcast it (loop guard), so the probe only observes the original Send.
            Assert.Equal(1, sendCount);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<TickerSelectedMessage>(probe);
            vm.Dispose();
        }
    }

    [Fact]
    public void Dispose_UnregistersFromMessenger_NoLongerFollowsSync()
    {
        // Arrange
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        vm.IsSyncEnabled = true;

        // Act
        vm.Dispose();
        WeakReferenceMessenger.Default.Send(new TickerSelectedMessage("TSLA"));

        // Assert: a disposed dashboard must not process further Sync updates ("Ghost" prevention)
        Assert.Equal("AAPL", vm.Symbol);
    }

    /// <summary>Minimal IMarketDataProvider test double for History-tab tests: returns a fixed
    /// candle set and records DeleteTickerDataFromDateAsync calls for assertion.</summary>
    private class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly IReadOnlyList<CandleData> _candlesToReturn;
        public List<(string Symbol, DateTime Cutoff)> DeleteCalls { get; } = new();
        public int DeleteReturnValue { get; set; } = 1;

        public FakeMarketDataProvider(IReadOnlyList<CandleData> candlesToReturn)
        {
            _candlesToReturn = candlesToReturn;
        }

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => Task.FromResult(_candlesToReturn);
        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>());
        public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => ValueTask.FromResult(TickerMetadata.Unknown);
        public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => Task.FromResult(TickerMetadata.Unknown);
        public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Task.CompletedTask;
        public Task AddTickerAsync(string symbol) => Task.CompletedTask;
        public Task AddTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public Task RemoveTickerAsync(string symbol) => Task.CompletedTask;
        public Task RemoveTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public void InvalidateMetadataCache(string ticker) { }
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => Task.FromResult<DateTimeOffset?>(null);

        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate)
        {
            DeleteCalls.Add((symbol, cutoffDate));
            return Task.FromResult(DeleteReturnValue);
        }
    }

    /// <summary>Minimal IDispatcherService test double (same pattern as
    /// NoteEditorViewModelTests.FakeDispatcherService): no real Avalonia UI thread runs under
    /// [Fact], so Post just invokes synchronously.</summary>
    private sealed class FakeDispatcherService : IDispatcherService
    {
        public void Post(Action action) => action();
        public void Post<T>(Action<T> action, T state) => action(state);
        public Task PostAsync(Func<Task> action) => action();
        public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }

    [Fact]
    public void DeleteHistoryFromSelectedCommand_CanExecute_FalseWithNoSelection()
    {
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        try
        {
            Assert.False(vm.DeleteHistoryFromSelectedCommand.CanExecute(null));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void DeleteHistoryFromSelectedCommand_CanExecute_TrueWhenRowSelected()
    {
        var vm = new EditTickerNotesDialogViewModel("AAPL");
        try
        {
            vm.SelectedHistoryRow = new CandleData(new DateTime(2024, 1, 1), 10m, 11m, 9m, 10.5m, 1000);
            Assert.True(vm.DeleteHistoryFromSelectedCommand.CanExecute(null));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task DeleteHistoryFromSelectedCommand_WhenCancelled_DoesNotCallProviderOrBroadcast()
    {
        var candles = new[] { new CandleData(new DateTime(2024, 1, 1), 10m, 11m, 9m, 10.5m, 1000) };
        var provider = new FakeMarketDataProvider(candles);
        var dialogService = new FakeDialogService { ConfirmationResult = false };

        var vm = new EditTickerNotesDialogViewModel("AAPL", marketDataProvider: provider, dialogService: dialogService);
        vm.SelectedHistoryRow = candles[0];
        string? received = null;
        var probe = new object();
        WeakReferenceMessenger.Default.Register<TickerDataRefreshedMessage>(probe, (r, m) => received = m.Value);

        try
        {
            await vm.DeleteHistoryFromSelectedCommand.ExecuteAsync(null);

            Assert.Equal(1, dialogService.ConfirmationCallCount);
            Assert.Empty(provider.DeleteCalls);
            Assert.Null(received);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<TickerDataRefreshedMessage>(probe);
            vm.Dispose();
        }
    }

    [Fact]
    public async Task DeleteHistoryFromSelectedCommand_WhenConfirmed_DeletesFromCutoffAndBroadcastsRefresh_ReloadedRowsAreDescending()
    {
        var initialCandles = new[]
        {
            new CandleData(new DateTime(2024, 1, 1), 10m, 11m, 9m, 10.5m, 1000),
            new CandleData(new DateTime(2024, 1, 2), 10.5m, 11.2m, 10m, 11m, 1100),
            new CandleData(new DateTime(2024, 1, 3), 11m, 11.8m, 10.8m, 11.5m, 1200),
        };
        var provider = new FakeMarketDataProvider(initialCandles);
        var dialogService = new FakeDialogService { ConfirmationResult = true };

        var vm = new EditTickerNotesDialogViewModel("AAPL", marketDataProvider: provider, dialogService: dialogService);
        // Selecting the 2024-01-02 row must delete it AND everything newer (01-02, 01-03),
        // preserving the older 01-01 row (continuity: only the tail is ever removed).
        vm.SelectedHistoryRow = initialCandles[1];

        string? received = null;
        var probe = new object();
        WeakReferenceMessenger.Default.Register<TickerDataRefreshedMessage>(probe, (r, m) => received = m.Value);

        try
        {
            await vm.DeleteHistoryFromSelectedCommand.ExecuteAsync(null);

            Assert.Equal(1, dialogService.ConfirmationCallCount);
            var call = Assert.Single(provider.DeleteCalls);
            Assert.Equal("AAPL", call.Symbol);
            Assert.Equal(new DateTime(2024, 1, 2), call.Cutoff);
            Assert.Equal("AAPL", received);

            // The reload after delete (FakeMarketDataProvider still returns the same 3 rows
            // since it doesn't actually mutate storage) must re-populate HistoryRows newest-first.
            Assert.Equal(3, vm.HistoryRows.Count);
            Assert.Equal(new DateTime(2024, 1, 3), vm.HistoryRows[0].Timestamp);
            Assert.Equal(new DateTime(2024, 1, 2), vm.HistoryRows[1].Timestamp);
            Assert.Equal(new DateTime(2024, 1, 1), vm.HistoryRows[2].Timestamp);
            // Selection must be cleared after a successful delete+reload.
            Assert.Null(vm.SelectedHistoryRow);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<TickerDataRefreshedMessage>(probe);
            vm.Dispose();
        }
    }

    // --- Reminder persistence (replaces the removed Ticker Note integration, spec 4.7) ---

    private static string UniqueTicker() => $"EDIT_DIALOG_TEST_{Guid.NewGuid():N}";

    [Fact]
    public async Task SaveCommand_PersistsReminderWithoutClobberingAutoSyncedNotes()
    {
        var ticker = UniqueTicker();
        // Simulate a Notes-tab-derived preview already cached for this ticker (owned by
        // TickerMetadataNotesCacheSynchronizer, never edited from the Dashboard anymore).
        UserStrategyMetadataRepository.Instance.RegisterLoadedStrategy(ticker, null, null, null, null, null, null, "Synced notes preview");

        var vm = new EditTickerNotesDialogViewModel(ticker, reminder: "Check earnings date");

        await vm.SaveCommand.ExecuteAsync(null);

        var strategy = UserStrategyMetadataRepository.Instance.GetStrategy(ticker);
        Assert.NotNull(strategy);
        Assert.Equal("Check earnings date", strategy!.Reminder);
        // Notes must survive the Dashboard save untouched, since the Dashboard no longer edits it.
        Assert.Equal("Synced notes preview", strategy.Notes);
    }

    [Fact]
    public async Task ApplyCommand_UpdatesReminderAcrossRepeatedCalls()
    {
        var ticker = UniqueTicker();
        var vm = new EditTickerNotesDialogViewModel(ticker, reminder: "First reminder");

        await vm.ApplyCommand.ExecuteAsync(null);
        vm.Reminder = "Updated reminder";
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Updated reminder", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Reminder);
    }

    [Fact]
    public void ReloadForSymbol_RestoresReminderFromRepository()
    {
        var ticker = UniqueTicker();
        UserStrategyMetadataRepository.Instance.RegisterLoadedStrategy(ticker, null, null, null, null, null, null, null, reminder: "Existing reminder");

        var vm = new EditTickerNotesDialogViewModel("OTHER_TICKER");
        vm.Symbol = ticker;

        Assert.Equal("Existing reminder", vm.Reminder);
    }
}
