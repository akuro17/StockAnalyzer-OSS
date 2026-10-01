using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The Tickers grid re-derives its Notes cell/popup text when Settings &gt; Notes "Read More Threshold" changes
/// (<see cref="NotesReadMoreThresholdChangedMessage"/>). Uses a private messenger so no other live
/// TickerListViewModel is involved, and shares the static-context collection because it initializes the context.
/// </summary>
[Collection("TickerNotesDisplayContext State")]
public class TickerListViewModelNotesThresholdTests : IDisposable
{
    private readonly FakeNotesSettingsManager _settings = new();
    private readonly WeakReferenceMessenger _messenger = new();

    public TickerListViewModelNotesThresholdTests()
    {
        TickerNotesDisplayContext.ResetForTesting();
        TickerNotesDisplayContext.Initialize(_settings, _messenger, () => "MORE-LABEL");
    }

    public void Dispose() => TickerNotesDisplayContext.ResetForTesting();

    // The view model starts a background hydration when a node is selected, and the synchronous test dispatcher runs its posted
    // callbacks inline on that pool thread, so the row raises PropertyChanged (Status, ErrorCode, ...) off the test thread while
    // the test is asserting. The collectors below are therefore thread-safe queues; a plain List threw "Collection was modified".

    /// <summary>A view model whose Watchlists category holds "AAPL"; selecting that node fills DisplayItems from the
    /// view model's own row cache, which is what the threshold handler iterates.</summary>
    private TickerListViewModel CreateViewModelShowingAapl()
    {
        var watchlist = new WatchlistProfile(
            Guid.NewGuid(), "Tech", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            items: new List<WatchlistItem> { new("AAPL", DateTimeOffset.UtcNow) });
        var watchlistManager = new Mock<IWatchlistManager>();
        watchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { watchlist });

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: _messenger,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: watchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);
        vm.SelectedNode = vm.Groups.First(n => n.Id == TickerListViewModel.WatchlistsCategoryId);
        return vm;
    }

    [Fact]
    public void ThresholdChange_RaisesTheNotesDerivedPropertiesOnEveryRowAndAppliesTheNewLimit()
    {
        var vm = CreateViewModelShowingAapl();
        try
        {
            var row = Assert.Single(vm.DisplayItems);
            row.Notes = new string('a', 30);
            Assert.Equal(new string('a', 30), row.DisplayNotes);
            var raised = new ConcurrentQueue<string?>();
            row.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

            _settings.SetReadMoreMaxCharacters(10); // context -> messenger -> handler

            Assert.Contains(nameof(WatchlistItemViewModel.DisplayNotes), raised);
            Assert.Contains(nameof(WatchlistItemViewModel.NotesPopupText), raised);
            Assert.Equal(new string('a', 10), row.DisplayNotes);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void UnrelatedNotesSettingChange_DoesNotRaiseAnything()
    {
        var vm = CreateViewModelShowingAapl();
        try
        {
            var row = Assert.Single(vm.DisplayItems);
            var raised = new ConcurrentQueue<string?>();
            row.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

            _settings.SetBodyFontSize(20.0);

            Assert.DoesNotContain(nameof(WatchlistItemViewModel.DisplayNotes), raised);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void AfterDispose_TheHandlerDoesNothing()
    {
        var vm = CreateViewModelShowingAapl();
        var row = Assert.Single(vm.DisplayItems);
        var raised = new ConcurrentQueue<string?>();
        row.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);
        vm.Dispose();

        // Called directly: after Dispose the messenger no longer delivers, so this proves the handler's own guard.
        vm.Receive(new NotesReadMoreThresholdChangedMessage());

        Assert.DoesNotContain(nameof(WatchlistItemViewModel.DisplayNotes), raised);
    }
}
