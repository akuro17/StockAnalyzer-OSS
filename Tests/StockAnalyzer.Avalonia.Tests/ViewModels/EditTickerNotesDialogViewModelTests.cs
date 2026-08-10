using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

// Sends TickerDataRefreshedMessage on the shared static WeakReferenceMessenger.Default using
// symbol names that overlap with TickerListViewModelTests (see MessengerSharedStateCollection.cs).
[Collection("MessengerSharedState")]
public class EditTickerNotesDialogViewModelTests
{
    /// <summary>Minimal IDialogService test double: only ShowConfirmationAsync is exercised by
    /// the History-tab delete flow; every other member is unused by these tests.</summary>
    private class FakeDialogService : IDialogService
    {
        public bool ConfirmationResult { get; set; }
        public int ConfirmationCallCount { get; private set; }
        public string? LastConfirmationTitle { get; private set; }
        public string? LastConfirmationMessage { get; private set; }

        public Task<bool> ShowConfirmationAsync(string title, string message)
        {
            ConfirmationCallCount++;
            LastConfirmationTitle = title;
            LastConfirmationMessage = message;
            return Task.FromResult(ConfirmationResult);
        }

        public Task ShowAlertAsync(string title, string message) => throw new NotImplementedException();
        public Task<string?> ShowInputAsync(string title, string message, string defaultValue = "") => throw new NotImplementedException();
        public Task<AddTickerResult> ShowAddTickerDialogAsync(Guid targetProfileId) => throw new NotImplementedException();
        public Task<Transaction?> ShowEditTransactionDialogAsync(EditTransactionDialogViewModel viewModel) => throw new NotImplementedException();
        public Task<(string Text, double FontSize)?> ShowTextDialogAsync(string title, string defaultText = "", double defaultFontSize = 12) => throw new NotImplementedException();
        public Task<DrawingSettingsResult> ShowDrawingSettingsDialogAsync(StockAnalyzer.Avalonia.Drawing.IChartObject drawing) => throw new NotImplementedException();
        public Task<global::Avalonia.Media.Color?> ShowColorPickerAsync(global::Avalonia.Media.Color initialColor) => throw new NotImplementedException();
        public Task ShowIndicatorSettingsDialogAsync(IEnumerable<CoreIndicatorSettings> currentIndicators, Action<IEnumerable<CoreIndicatorSettings>>? onApply = null) => throw new NotImplementedException();
        public Task ShowIndicatorPropertiesDialogAsync(CoreIndicatorSettings indicator, Action<CoreIndicatorSettings>? onApply = null) => throw new NotImplementedException();
        public Task ShowThemeSettingsDialogAsync() => throw new NotImplementedException();
        public Task ShowSettingsDialogAsync(string? initialCategoryKey = null) => throw new NotImplementedException();
        public Task<List<string>?> ShowColumnChooserDialogAsync(IEnumerable<WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumns, Action<List<string>>? onApply = null) => throw new NotImplementedException();
        public Task<StockAnalyzer.Core.Models.Settings.FilterSettings?> ShowFilterSettingsDialogAsync(StockAnalyzer.Core.Models.Settings.FilterSettings initialSettings, Action<StockAnalyzer.Core.Models.Settings.FilterSettings>? onApply = null) => throw new NotImplementedException();
        public Task ShowScreenerDialogAsync() => throw new NotImplementedException();
        public Task<BulkTagEditResult?> ShowBulkTagEditDialogAsync(IEnumerable<string> existingTags) => throw new NotImplementedException();
        public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? longVal = null, decimal? exitLong = null, decimal? stopLossLong = null, decimal? shortVal = null, decimal? exitShort = null, decimal? stopLossShort = null, string? notes = null, Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? onSave = null) => throw new NotImplementedException();
        [Obsolete]
        public Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? notes, Action<decimal?, decimal?, decimal?, string?>? onSave) => throw new NotImplementedException();
        public IMultiSyncProgressSession CreateMultiSyncProgressSession() => throw new NotImplementedException();
        public Task<PythonSetupDecision> ShowPythonSetupConfirmationAsync() => throw new NotImplementedException();
        public Task ShowManualSetupInstructionsAsync() => throw new NotImplementedException();
        public Task<PythonSetupDecision> ShowPythonUpdateConfirmationAsync() => throw new NotImplementedException();
        public Task ShowPythonManualUpdateInstructionsAsync() => throw new NotImplementedException();
        public Task RunWithProgressAsync(string title, Func<IProgress<string>, Task> action) => throw new NotImplementedException();
        public object? GetMainWindowOwner() => null;
        public Task ShowLogViewerAsync() => throw new NotImplementedException();
        public Task<string?> ShowOpenFileDialogAsync(string title, string[]? filters = null) => throw new NotImplementedException();
        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension = "", string defaultFilename = "", string[]? filters = null) => throw new NotImplementedException();
        public void Shutdown() { }
        public void ActivateMainWindow() { }
    }

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
        var vm = new EditTickerNotesDialogViewModel("AAPL", 150.50m, 200.00m, 140.00m, notes: "Test strategy notes");

        // Assert
        Assert.Equal("AAPL", vm.Ticker);
        Assert.Equal("150.50", vm.EntryPriceText);
        Assert.Equal("200.00", vm.TargetPriceText);
        Assert.Equal("140.00", vm.StopLossText);
        Assert.Equal("Test strategy notes", vm.Notes);
        Assert.Equal(150.50m, vm.EntryPrice);
        Assert.Equal(200.00m, vm.TargetPrice);
        Assert.Equal(140.00m, vm.StopLoss);
    }

    [Fact]
    public void SaveCommand_TriggersCloseActionWithTrue()
    {
        // Arrange
        var vm = new EditTickerNotesDialogViewModel("MSFT", null, 400.00m, 350.00m, notes: "Bullish momentum");
        bool? result = null;
        vm.CloseAction = res => result = res;

        // Act
        vm.SaveCommand.Execute(null);

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
    public void ApplyCommand_TriggersOnApplyCallbackWithoutClosing()
    {
        // Arrange
        bool applied = false;
        var vm = new EditTickerNotesDialogViewModel("AMZN", 120.00m, 160.00m, 100.00m, "Growth plan", (e, t, s, n) => applied = true);
        bool? closed = null;
        vm.CloseAction = res => closed = res;

        // Act
        vm.ApplyCommand.Execute(null);

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
}
