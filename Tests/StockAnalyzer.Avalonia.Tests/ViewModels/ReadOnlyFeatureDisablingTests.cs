using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using Xunit;
using static StockAnalyzer.TestSupport.TestDataStorageState;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// D-2' Run 3 / S4: when the data folder cannot be written, the features whose main purpose is to write below it are disabled before they
/// start (a command that cannot run, with the reason as its tooltip) instead of failing halfway. With no state, or a writable one, every
/// command behaves exactly as before. The state is fixed for the life of the process, so there is no "state changed" case.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class ReadOnlyFeatureDisablingTests
{
    private static string Reason => LocalizationManager.Instance[StorageFeatureGate.DisabledToolTipKey];

    // ---- the gate ---------------------------------------------------------------------------------------------

    [Fact]
    public void Gate_NoStateOrWritable_IsAvailableWithoutToolTip()
    {
        foreach (var gate in new[] { new StorageFeatureGate(null), new StorageFeatureGate(WritableState()) })
        {
            Assert.True(gate.IsAvailable);
            Assert.Null(gate.DisabledToolTip);
        }
    }

    [Theory]
    [InlineData(DataStorageReadOnlyReason.DataRootCannotBeCreated)]
    [InlineData(DataStorageReadOnlyReason.DataRootNotWritable)]
    public void Gate_ReadOnly_IsUnavailable_WithTheOneSharedReason(DataStorageReadOnlyReason reason)
    {
        var gate = new StorageFeatureGate(ReadOnlyState(reason));

        Assert.False(gate.IsAvailable);
        Assert.Equal(Reason, gate.DisabledToolTip);
        Assert.False(string.IsNullOrWhiteSpace(Reason));
        Assert.NotEqual(StorageFeatureGate.DisabledToolTipKey, Reason); // the key exists in the shipped locale, it is not echoed back as "missing"
    }

    // ---- ticker sync and the master list ----------------------------------------------------------------------

    private static TickerListViewModel NewTickerList(
        IDataStorageState? state, Mock<IDialogService>? dialog = null, Mock<IMarketDataProvider>? market = null, Mock<IWatchlistManager>? watchlistMock = null)
    {
        var watchlist = watchlistMock ?? new Mock<IWatchlistManager>();
        watchlist.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());
        return new TickerListViewModel(
            (market ?? new Mock<IMarketDataProvider>()).Object,
            new Mock<IPythonService>().Object,
            new WeakReferenceMessenger(),
            new SynchronousDispatcherService(),
            watchlist.Object,
            new PortfolioManager(),
            (dialog ?? new Mock<IDialogService>()).Object,
            new TickerImportService(NullLogger<TickerImportService>.Instance),
            new Mock<IChartSettingsManager>().Object,
            NullLogger<TickerListViewModel>.Instance,
            dataStorageState: state);
    }

    [Fact]
    public void TickerList_ReadOnly_DisablesSyncAndAddTicker_WithTheReason()
    {
        using var vm = NewTickerList(ReadOnlyState());

        Assert.False(vm.IsStorageWritable);
        Assert.Equal(Reason, vm.StorageDisabledToolTip);
        Assert.False(vm.SyncSelectedTickersCommand.CanExecute(null));
        Assert.False(vm.SyncGroupCommand.CanExecute(null));
        Assert.False(vm.AddTickerToMasterCommand.CanExecute(null));
        Assert.False(vm.DeleteFromMasterListCommand.CanExecute(null)); // the other two writers of the master ticker list
        Assert.False(vm.BulkImportCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TickerList_NoStateOrWritable_KeepsSyncAndAddTickerEnabled(bool withWritableState)
    {
        using var vm = NewTickerList(withWritableState ? WritableState() : null);

        Assert.True(vm.IsStorageWritable);
        Assert.Null(vm.StorageDisabledToolTip);
        Assert.True(vm.SyncSelectedTickersCommand.CanExecute(null));
        Assert.True(vm.SyncGroupCommand.CanExecute(null));
        Assert.True(vm.AddTickerToMasterCommand.CanExecute(null));
        Assert.True(vm.DeleteFromMasterListCommand.CanExecute(null));
        Assert.True(vm.BulkImportCommand.CanExecute(null));
    }

    private static WatchlistItemViewModel TickerItem(string symbol) => new(symbol, symbol, "", "", 0, 0, 0, 0, 0, 0);

    [Fact]
    public async Task TickerList_ReadOnly_DeleteFromMasterList_ExplainsBeforeAnyConfirmation_AndWritesNothing()
    {
        // BulkRemove on the All Tickers node calls the method directly, so the entry itself must refuse, not only the command's CanExecute.
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var market = new Mock<IMarketDataProvider>();
        using var vm = NewTickerList(ReadOnlyState(), dialog, market);
        var item = TickerItem("AAA");
        item.IsChecked = true;
        vm.DisplayItems.Add(item); // a checked row is what the command acts on

        await vm.DeleteFromMasterListCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowAlertAsync(It.IsAny<string>(), Reason), Times.Once);
        dialog.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        market.Verify(m => m.RemoveTickersAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task TickerList_Writable_DeleteFromMasterList_ReportsAFailedWrite_AndStopsBeforeTheCascade()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        dialog.Setup(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var market = new Mock<IMarketDataProvider>();
        market.Setup(m => m.RemoveTickersAsync(It.IsAny<IEnumerable<string>>())).ThrowsAsync(new IOException("master list is locked"));
        var watchlist = new Mock<IWatchlistManager>();
        using var vm = NewTickerList(WritableState(), dialog, market, watchlist);
        var item = TickerItem("AAA");
        item.IsChecked = true;
        vm.DisplayItems.Add(item); // a checked row is what the command acts on

        await vm.DeleteFromMasterListCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowAlertAsync(It.IsAny<string>(), "master list is locked"), Times.Once);
        watchlist.Verify(w => w.RemoveTickersFromAllProfiles(It.IsAny<IEnumerable<string>>()), Times.Never); // no watchlist lost a ticker the master list still has
    }

    [Fact]
    public async Task TickerSyncService_ReadOnly_ExplainsInsteadOfOpeningTheProgressWindow()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var service = new TickerSyncService(new Mock<IPythonService>().Object, dialog.Object, new SynchronousDispatcherService(), dataStorageState: ReadOnlyState());

        await service.SyncSingleTickerAsync("AAPL");

        dialog.Verify(d => d.ShowAlertAsync(It.IsAny<string>(), Reason), Times.Once);
        dialog.Verify(d => d.CreateMultiSyncProgressSession(), Times.Never);
    }

    [Fact]
    public async Task TickerSyncService_Writable_StillOpensTheProgressSession()
    {
        var dialog = new Mock<IDialogService>();
        var service = new TickerSyncService(new Mock<IPythonService>().Object, dialog.Object, new SynchronousDispatcherService(), dataStorageState: WritableState());

        // The loose mock returns no session, so the flow stops right after it asked for one; that request is what is pinned here.
        try { await service.SyncSingleTickerAsync("AAPL"); } catch (NullReferenceException) { }

        dialog.Verify(d => d.CreateMultiSyncProgressSession(), Times.Once);
        dialog.Verify(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ---- notes ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Notes_ReadOnly_BlocksEveryEntryToTheComposePanel_AndSave()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_ro_notes_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, dataStorageState: ReadOnlyState());
            var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();
            Assert.Empty(timeline.DisplayedNotes); // S5: in read-only mode the notes database is not read at all, so the timeline stays empty

            // A card is built with the gate its timeline would hand it.
            Action<NoteTimelineItemViewModel> noOp = _ => { };
            var card = new NoteTimelineItemViewModel(note, false, _ => Task.CompletedTask, _ => Task.CompletedTask, noOp, _ => { }, noOp, noOp, _ => { }, _ => { },
                onQuoteRequested: noOp, onReplyRequested: noOp, storageGate: new StorageFeatureGate(ReadOnlyState()));

            Assert.False(timeline.IsStorageWritable);
            Assert.Equal(Reason, timeline.StorageDisabledToolTip);
            Assert.False(timeline.BeginCreateCommand.CanExecute(null));
            Assert.False(timeline.OpenTrashCommand.CanExecute(null));
            Assert.False(timeline.OpenOrphanedFilesCommand.CanExecute(null));
            Assert.Equal(Reason, timeline.TrashButtonToolTip); // the reason replaces the usual tooltip while the button is disabled
            Assert.Equal(Reason, timeline.OrphanedButtonToolTip);
            Assert.False(card.EditCommand.CanExecute(null));
            Assert.False(card.QuoteCommand.CanExecute(null));
            Assert.False(card.ReplyCommand.CanExecute(null));
            Assert.Equal(Reason, card.StorageDisabledToolTip);

            // Even when an editor is opened some other way, Save does not run.
            var editor = new StockAnalyzer.Avalonia.ViewModels.Notes.NoteEditorViewModel(
                noteRepository, null!, null!, new FakeMarketDataProvider(), new SynchronousDispatcherService(), new FakeNotesSettingsManager(),
                NullLogger<StockAnalyzer.Avalonia.ViewModels.Notes.NoteEditorViewModel>.Instance, ReadOnlyState()) { Body = "typed text" };
            Assert.False(editor.SaveCommand.CanExecute(null));
            Assert.Equal(Reason, editor.StorageDisabledToolTip);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notes_NoStateOrWritable_KeepsTheComposeEntriesEnabled(bool withWritableState)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_ro_notes_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, dataStorageState: withWritableState ? WritableState() : null);
            await noteRepository.CreateAsync(new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now));
            await timeline.RefreshAsync();
            var card = Assert.Single(timeline.DisplayedNotes);

            Assert.True(timeline.BeginCreateCommand.CanExecute(null));
            Assert.True(timeline.OpenTrashCommand.CanExecute(null));
            Assert.True(timeline.OpenOrphanedFilesCommand.CanExecute(null));
            Assert.Equal(LocalizationManager.Instance["Note_TrashButton_Tooltip"], timeline.TrashButtonToolTip);
            Assert.Equal(LocalizationManager.Instance["Note_OrphanedButton_Tooltip"], timeline.OrphanedButtonToolTip);
            Assert.True(card.EditCommand.CanExecute(null));
            Assert.True(card.QuoteCommand.CanExecute(null));
            Assert.True(card.ReplyCommand.CanExecute(null));
            Assert.Null(timeline.StorageDisabledToolTip);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    // ---- portfolio transactions -------------------------------------------------------------------------------

    private static PortfolioSummaryViewModel NewPortfolio(IDataStorageState? state)
    {
        var market = new Mock<IMarketDataProvider>();
        var dispatcher = new SynchronousDispatcherService();
        var localization = new Mock<ILocalizationService>();
        var watchlist = new Mock<IWatchlistManager>();
        watchlist.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());
        return new PortfolioSummaryViewModel(
            new Mock<IPortfolioManager>().Object,
            market.Object,
            dispatcher,
            new Mock<IMessenger>().Object,
            new Mock<ILogger<PortfolioSummaryViewModel>>().Object,
            watchlist.Object,
            new Mock<IDialogService>().Object,
            () => new EditTransactionDialogViewModel(market.Object, dispatcher, localization.Object),
            new Mock<IDesignTimeDetector>().Object,
            localization.Object,
            state);
    }

    [Fact]
    public void Portfolio_ReadOnly_DisablesEveryCommandThatChangesTransactions()
    {
        using var vm = NewPortfolio(ReadOnlyState());

        Assert.False(vm.IsStorageWritable);
        Assert.Equal(Reason, vm.StorageDisabledToolTip);
        Assert.False(vm.AddTransactionCommand.CanExecute("Long"));
        Assert.False(vm.EditTransactionCommand.CanExecute(null));
        Assert.False(vm.DeleteTransactionCommand.CanExecute(null));
        Assert.False(vm.DeleteTransactionsCommand.CanExecute(null));
        Assert.False(vm.ExitPositionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Portfolio_NoStateOrWritable_KeepsTheCommandsEnabled(bool withWritableState)
    {
        using var vm = NewPortfolio(withWritableState ? WritableState() : null);

        Assert.True(vm.IsStorageWritable);
        Assert.Null(vm.StorageDisabledToolTip);
        Assert.True(vm.AddTransactionCommand.CanExecute("Long"));
        Assert.True(vm.EditTransactionCommand.CanExecute(null));
        Assert.True(vm.DeleteTransactionCommand.CanExecute(null));
        Assert.True(vm.DeleteTransactionsCommand.CanExecute(null));
    }

    // ---- training wizard --------------------------------------------------------------------------------------

    private static TrainingWizardViewModel NewWizard(IDataStorageState? state)
    {
        var orchestrator = new Mock<ITrainingOrchestrator>();
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAA" });
        var store = FakeTickerStateStore.FromProfiles(Array.Empty<WatchlistProfile>());
        return new TrainingWizardViewModel(orchestrator: orchestrator.Object, marketDataProvider: market.Object, tickerStore: store, dataStorageState: state);
    }

    [Fact]
    public void TrainingWizard_ReadOnly_CannotStart_AndTheFooterNamesTheReason()
    {
        using var wizard = NewWizard(ReadOnlyState());

        Assert.False(wizard.StartTrainingCommand.CanExecute(null));
        Assert.True(wizard.HasStartBlocker);
        Assert.Equal(Reason, wizard.StartBlockerDisplay);
        Assert.False(wizard.DeployToModelsCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrainingWizard_NoStateOrWritable_CanStartAsBefore(bool withWritableState)
    {
        using var wizard = NewWizard(withWritableState ? WritableState() : null);

        Assert.True(wizard.StartTrainingCommand.CanExecute(null));
        Assert.False(wizard.HasStartBlocker);
    }
}
