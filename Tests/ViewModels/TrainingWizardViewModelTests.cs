using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Tests.ViewModels;

// Mutates the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class TrainingWizardViewModelTests : IDisposable
{
    private readonly SynchronizationContext? _previousSyncContext;
    private readonly string _previousLanguage;

    public TrainingWizardViewModelTests()
    {
        _previousLanguage = LocalizationManager.Instance.CurrentLanguage;
        LocalizationManager.Instance.Initialize("ja");

        // The VM builds a Progress<TrainingProgress> that captures SynchronizationContext.Current;
        // in the app that is the UI context, which serializes OnTrainingProgress onto one thread.
        // Without a context here, Progress<T> dispatches callbacks concurrently on the thread pool
        // and corrupts the ObservableCollections it mutates. Install a synchronous context so the
        // tests exercise the same single-threaded delivery the app has.
        _previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronousSynchronizationContext());
    }

    public void Dispose()
    {
        LocalizationManager.Instance.Initialize(_previousLanguage);
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
    }

    private sealed class SynchronousSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [Fact]
    public async Task TransferControls_ReachJobContractAndCheckpointResult()
    {
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult { RunId = "new_run", Success = true, CheckpointManifestPath = "run.json" },
        };
        using var vm = new TrainingWizardViewModel(orchestrator: orchestrator,
            marketDataProvider: new DummyMarketDataProvider("X"));
        await WaitForInitializationAsync(vm);
        vm.Horizon = 1;
        vm.SelectedArchitecture = "cnn";
        vm.SelectedInitializationMode = TrainingInitializationMode.FineTune;
        vm.CheckpointPath = "best.json";
        vm.ParentModelId = new string('a', 64);
        vm.FreezePathsText = "features, features.0";
        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        await vm.StartTrainingCommand.ExecuteAsync(null);
        Assert.Equal(TrainingInitializationMode.FineTune, orchestrator.CapturedConfig!.InitializationMode);
        Assert.Equal(new[] { "features", "features.0" }, orchestrator.CapturedConfig.FreezePaths);
        Assert.Equal("best.json", orchestrator.CapturedConfig.CheckpointPath);
        Assert.Equal("run.json", vm.CheckpointManifestPath);
        vm.SelectedFramework = TrainingFramework.TensorFlow;
        Assert.Equal(TrainingInitializationMode.Fresh, vm.SelectedInitializationMode);
        Assert.Single(vm.AvailableInitializationModes);
    }

    [Fact]
    public void StartTrainingCommand_DefaultState_CannotExecute()
    {
        // Even though Horizon now defaults to DefaultHorizon (5), Start stays disabled
        // until symbols and orchestrator are set.
        var vm = new TrainingWizardViewModel();

        Assert.False(vm.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public void WindowUnitNote_ExplicitHorizonZero_OmitsMinBarsClause()
    {
        // Horizon set to 0 (invalid); the min-bars clause needs both Window/Horizon > 0.
        var vm = new TrainingWizardViewModel { Horizon = 0 };

        var expectedMinBars = WalkForwardDataRequirement.MinimumRawBarsForTraining(
            PredictionSettingsManager.DefaultWindowSize,
            WalkForwardDataRequirement.DefaultHorizon).ToString();
        Assert.DoesNotContain(expectedMinBars, vm.WindowUnitNote);
        Assert.Equal(string.Empty, vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_DefaultState_IncludesMinBarsCount()
    {
        // Defaults to WindowSize = 60, Horizon = 5.
        var vm = new TrainingWizardViewModel();

        var expectedMinBars = WalkForwardDataRequirement.MinimumRawBarsForTraining(
            PredictionSettingsManager.DefaultWindowSize,
            WalkForwardDataRequirement.DefaultHorizon).ToString();
        Assert.Contains(expectedMinBars, vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_ValidWindowAndHorizon_IncludesMinBarsCount()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };

        Assert.Contains(WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5).ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_ShowsTheWholeRunRequirement_NotOnlyTheLastFold()
    {
        // A 75-bar window needs far more history than the last fold alone: the monthly AAPL history (551 bars)
        // failed in the trainer although the earlier hint (534) said it was enough.
        var vm = new TrainingWizardViewModel { WindowSize = 75, Horizon = 5 };
        var whole = WalkForwardDataRequirement.MinimumRawBarsForTraining(75, 5);
        Assert.True(whole > 551 && whole > WalkForwardDataRequirement.MinimumRawBars(75, 5));
        Assert.Contains(whole.ToString(), vm.WindowUnitNote);

        // the release fold's extra calibration carve is part of the requirement (it never needs less than the plain run)
        vm.UseCalibration = true;
        Assert.Contains(WalkForwardDataRequirement.MinimumRawBarsForTraining(75, 5, calibrationSegment: true).ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_RecomputesWhenHorizonChanges()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };
        var before = WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5);
        Assert.Contains(before.ToString(), vm.WindowUnitNote);

        vm.Horizon = 30;

        var after = WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 30);
        Assert.NotEqual(before, after);
        Assert.Contains(after.ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_RecomputesWhenGapChanges()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };
        var noteAtDefaultGap = vm.WindowUnitNote;
        Assert.Contains(WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5).ToString(), noteAtDefaultGap);

        vm.Gap = 0;

        var expected = WalkForwardDataRequirement.MinimumRawBarsForTraining(
            25, 5, WalkForwardDataRequirement.DefaultSplitCount, gap: 0);
        Assert.NotEqual(noteAtDefaultGap, vm.WindowUnitNote);
        Assert.Contains(expected.ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_NegativeGap_DoesNotThrow_FallsBackToDefaultGap()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };

        vm.Gap = -5; // transient invalid input; Start stays disabled, but the note must still render.

        Assert.Contains(WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5).ToString(), vm.WindowUnitNote);
    }

    [Fact]
    public void WindowUnitNote_OverLimitHistoryShowsLocalizedUnavailableMessage()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5, Gap = int.MaxValue };
        Assert.Contains(LocalizationManager.Instance["TrainingWizard_WindowUnitNote_OverLimit"], vm.WindowUnitNote);
        Assert.DoesNotContain("-", vm.WindowUnitNote);
    }

    [Fact]
    public void Gap_Change_RaisesWindowUnitNotePropertyChanged()
    {
        var vm = new TrainingWizardViewModel { WindowSize = 25, Horizon = 5 };
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Gap = 7;

        Assert.Contains(nameof(TrainingWizardViewModel.WindowUnitNote), raised);
    }

    [Fact]
    public void WindowUnitNote_UsesMaximumExplicitLagAndUpdatesWhenEdited()
    {
        var vm = new TrainingWizardViewModel
        {
            WindowSize = 25, Horizon = 5, UseFixedZScore = true, LagsText = "20,1"
        };
        var withLag20 = WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5, maxLag: 20);
        Assert.True(withLag20 > WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5));
        Assert.Contains(withLag20.ToString(), vm.WindowUnitNote);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.LagsText = "1,5";
        Assert.Contains(WalkForwardDataRequirement.MinimumRawBarsForTraining(25, 5, maxLag: 5).ToString(), vm.WindowUnitNote);
        Assert.Contains(nameof(TrainingWizardViewModel.WindowUnitNote), raised);
    }

    // ----- ticker-list tree (replaces the scope radio buttons and the profile lists) -----

    private sealed class FakeTickerStore : ITickerStateStore
    {
        public List<TickerGroupNode> Nodes { get; } = new();
        public Dictionary<Guid, string[]> Members { get; } = new();
        public int Resolutions { get; private set; }
        public IEnumerable<TickerGroupNode> Groups => Nodes;
        public IEnumerable<StockAnalyzer.Core.Models.Screener.IFilterableSymbol> DisplayItems => Array.Empty<StockAnalyzer.Core.Models.Screener.IFilterableSymbol>();
        public IReadOnlyList<string> GetTickersForNode(TickerGroupNode? node)
        {
            Resolutions++;
            return node is not null && Members.TryGetValue(node.Id, out var members) ? members : Array.Empty<string>();
        }
    }

    private static (FakeTickerStore Store, WatchlistNode Watchlist, PortfolioNode Portfolio) TreeStore()
    {
        var store = new FakeTickerStore();
        var watchlist = new WatchlistNode(MakeProfile("Tech Watch", isPortfolio: false, "AAPL", "MSFT"));
        var portfolio = new PortfolioNode(MakeProfile("Core Holdings", isPortfolio: true, "GOOGL"));
        var watchlists = new CategoryNode(TickerListViewModel.WatchlistsCategoryId, "Watchlists"); watchlists.Children!.Add(watchlist);
        var portfolios = new CategoryNode(TickerListViewModel.PortfoliosCategoryId, "Portfolios"); portfolios.Children!.Add(portfolio);
        store.Nodes.AddRange(new TickerGroupNode[] { new AllTickersNode("All"), watchlists, portfolios, new CategoryNode(Guid.NewGuid(), "Other"),
            new ActionNode(Guid.NewGuid(), "Create", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })) });
        store.Members[watchlist.Id] = new[] { "AAPL", "MSFT" };
        store.Members[portfolio.Id] = new[] { "GOOGL", "MSFT" };
        store.Members[watchlists.Id] = new[] { "AAPL", "MSFT" };
        return (store, watchlist, portfolio);
    }

    [Fact]
    public async Task Constructor_BuildsTheTickerTreeAndTicksOnlyAllTickers()
    {
        var (store, _, _) = TreeStore();
        using var vm = new TrainingWizardViewModel(watchlistManager: new DummyWatchlistManager(Array.Empty<WatchlistProfile>()),
            marketDataProvider: new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL", "AMZN"), tickerStore: store);
        await WaitForInitializationAsync(vm);

        Assert.True(vm.HasSourceTree);
        Assert.Equal(new[] { "All", "Watchlists", "Portfolios", "Other" }, vm.SourceTree.Select(i => i.Name).ToArray());     // the action node is not a row
        Assert.Equal(new[] { true, true, true, false }, vm.SourceTree.Select(i => i.IsSelectable).ToArray());
        Assert.Equal(new[] { true, false, false, false }, vm.SourceTree.Select(i => i.IsSelected).ToArray());
        Assert.Equal("Tech Watch", Assert.Single(vm.SourceTree[1].Children).Name);
        Assert.Equal("Core Holdings", Assert.Single(vm.SourceTree[2].Children).Name);
        Assert.True(vm.SourceTree[1].Children[0].HasIcon);
        Assert.False(vm.SourceTree[0].HasIcon);
    }

    [Fact]
    public async Task WatchlistsChanged_AfterConstruction_RebuildsTheTreeAndKeepsTheTickedRows()
    {
        // A wizard opened before the watchlists finished loading (or before the user created one) must not keep an empty list for its lifetime.
        var store = new FakeTickerStore();
        store.Nodes.Add(new AllTickersNode("All"));
        var watchlistManager = new DummyWatchlistManager(Array.Empty<WatchlistProfile>());
        using var vm = new TrainingWizardViewModel(watchlistManager: watchlistManager, marketDataProvider: new DummyMarketDataProvider("AAPL", "MSFT"), tickerStore: store);
        await WaitForInitializationAsync(vm);
        Assert.Single(vm.SourceTree);

        var (full, _, _) = TreeStore();
        store.Nodes.Clear(); store.Nodes.AddRange(full.Nodes.Where(n => n is not AllTickersNode)); store.Nodes.Insert(0, vm.SourceTree[0].Node);
        watchlistManager.SetProfiles(new[] { MakeProfile("Tech Watch", isPortfolio: false, "AAPL") });

        Assert.Equal(new[] { "All", "Watchlists", "Portfolios", "Other" }, vm.SourceTree.Select(i => i.Name).ToArray());
        Assert.True(vm.SourceTree[0].IsSelected);                       // the ticked row survives the rebuild
        Assert.All(vm.SourceTree.Skip(1), i => Assert.False(i.IsSelected));
    }

    [Fact]
    public async Task StartTrainingAsync_TickedLists_ResolveTheUnionOfTheirTickersWithoutDuplicates()
    {
        var (store, watchlist, portfolio) = TreeStore();
        var orchestrator = new FakeTrainingOrchestrator();
        using var vm = new TrainingWizardViewModel(orchestrator: orchestrator, watchlistManager: new DummyWatchlistManager(Array.Empty<WatchlistProfile>()),
            marketDataProvider: new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL", "AMZN"), tickerStore: store);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        vm.SourceTree[0].IsSelected = false;                            // All tickers off
        Assert.False(vm.StartTrainingCommand.CanExecute(null));          // nothing ticked: no population
        vm.SourceTree[1].Children[0].IsSelected = true;                  // Tech Watch: AAPL, MSFT
        vm.SourceTree[2].Children[0].IsSelected = true;                  // Core Holdings: GOOGL, MSFT
        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "AAPL", "GOOGL", "MSFT" }, orchestrator.CapturedConfig!.Symbols.OrderBy(s => s).ToArray());
    }

    [Fact]
    public async Task TickingAnotherList_UntickAllTickers_ButOtherChangesLeaveItAlone()
    {
        var (store, _, _) = TreeStore();
        using var vm = new TrainingWizardViewModel(watchlistManager: new DummyWatchlistManager(Array.Empty<WatchlistProfile>()),
            marketDataProvider: new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL", "AMZN"), tickerStore: store);
        await WaitForInitializationAsync(vm);
        var all = vm.SourceTree[0];
        Assert.True(all.IsSelected);

        vm.SourceTree[1].Children[0].IsSelected = true;                  // Tech Watch
        Assert.False(all.IsSelected);                                     // All tickers is off now: the run trains on the list only
        Assert.Contains("2", vm.SelectedSymbolSummary);                   // AAPL, MSFT

        vm.SourceTree[1].Children[0].IsSelected = false;                  // unticking a list changes nothing else
        Assert.False(all.IsSelected);
        all.IsSelected = true;                                            // ticking All tickers again is respected (the owner asked for everything)
        Assert.True(all.IsSelected);
        vm.SourceTree[2].IsSelected = true;                               // the Portfolios category counts as another list
        Assert.False(all.IsSelected);
    }

    [Fact]
    public async Task TheAllTickersUntickNotice_AppearsOnlyRightAfterAnAutomaticUntick()
    {
        var (store, _, _) = TreeStore();
        using var vm = new TrainingWizardViewModel(watchlistManager: new DummyWatchlistManager(Array.Empty<WatchlistProfile>()),
            marketDataProvider: new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL", "AMZN"), tickerStore: store);
        await WaitForInitializationAsync(vm);
        Assert.Null(vm.SourceTreeNotice);

        vm.SourceTree[1].Children[0].IsSelected = true;                  // a list while All tickers is ticked: unticked by itself
        Assert.Equal(LocalizationManager.Instance["TrainingWizard_AllTickersUntickedNotice"], vm.SourceTreeNotice);

        vm.SourceTree[2].Children[0].IsSelected = true;                  // the next tick unticks nothing: the notice is gone
        Assert.Null(vm.SourceTreeNotice);
    }

    private async Task<(TrainingWizardViewModel Vm, FakeTrainingOrchestrator Orchestrator)> StartableWizardAsync()
    {
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: new DummyMarketDataProvider("X"));
        await WaitForInitializationAsync(vm);
        vm.Horizon = 1;
        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        return (vm, orchestrator);
    }

    [Fact]
    public async Task AHyperparameterOutsideItsDomain_ShowsAMessage_AndBlocksStartUntilCorrected()
    {
        var (vm, _) = await StartableWizardAsync();
        using var _ = vm;
        var row = new TrainingHyperparameterEntry { Key = "dropout" };
        vm.Hyperparameters.Add(row);
        Assert.Null(row.ValidationMessage);                                 // the key's own default is valid
        Assert.True(vm.StartTrainingCommand.CanExecute(null));

        row.Value = "2.0";
        Assert.False(string.IsNullOrEmpty(row.ValidationMessage));
        Assert.Contains("dropout", row.ValidationMessage);
        Assert.False(vm.StartTrainingCommand.CanExecute(null));

        row.Value = "0.3";
        Assert.Null(row.ValidationMessage);
        Assert.True(vm.StartTrainingCommand.CanExecute(null));

        row.Value = "abc";                                                  // a real-valued key refuses text
        Assert.NotNull(row.ValidationMessage);
        Assert.False(vm.StartTrainingCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("epochs", "99999999999999999999", "9223372036854775807")]    // no maximum: the end of the 64-bit range is named
    [InlineData("hidden", "99999999999999999999", "9223372036854775807")]
    [InlineData("heads", "99999999999999999999", "16")]                        // its own maximum
    [InlineData("seed", "-99999999999999999999", "0")]                         // beyond the 64-bit range below: the minimum of the domain
    public async Task AnIntegerBeyondTheSixtyFourBitRange_ShowsAMessageNamingTheBound_AndBlocksStart(string key, string value, string bound)
    {
        var (vm, _) = await StartableWizardAsync();
        using var _ = vm;
        var row = new TrainingHyperparameterEntry { Key = key };
        vm.Hyperparameters.Add(row);

        var thrown = Record.Exception(() => row.Value = value);

        Assert.Null(thrown);
        Assert.Contains(bound, row.ValidationMessage);
        Assert.False(vm.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARowRemovedByClear_NoLongerRevalidatesTheList()
    {
        var (vm, _) = await StartableWizardAsync();
        using var _ = vm;
        var row = new TrainingHyperparameterEntry { Key = "epochs", Value = "5" };
        vm.Hyperparameters.Add(row);
        vm.Hyperparameters.Clear();
        int raised = 0;
        vm.StartTrainingCommand.CanExecuteChanged += (_, _) => raised++;

        row.Value = "6";

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task AnUnknownKey_IsNamedInANotice_ButDoesNotBlockStart()
    {
        var (vm, _) = await StartableWizardAsync();
        using var _ = vm;
        Assert.Null(vm.UnknownKeyNotice);

        vm.Hyperparameters.Add(new TrainingHyperparameterEntry { Key = "my-own-flag", Value = "1" });

        Assert.Contains("my-own-flag", vm.UnknownKeyNotice);
        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        vm.Hyperparameters.Clear();
        Assert.Null(vm.UnknownKeyNotice);
    }

    [Fact]
    public async Task TheSameFlagEnteredTwice_BlocksStart_InsteadOfFailingWhenTheRunIsBuilt()
    {
        var (vm, _) = await StartableWizardAsync();
        using var _ = vm;
        var first = new TrainingHyperparameterEntry { Key = "epochs", Value = "5" };
        var second = new TrainingHyperparameterEntry { Key = "--epochs", Value = "6" };
        vm.Hyperparameters.Add(first);
        vm.Hyperparameters.Add(second);

        Assert.NotNull(first.ValidationMessage);
        Assert.NotNull(second.ValidationMessage);
        Assert.False(vm.StartTrainingCommand.CanExecute(null));

        vm.Hyperparameters.Remove(second);
        Assert.Null(first.ValidationMessage);
        Assert.True(vm.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheAttentionArchitecture_RequiresHiddenToBeAMultipleOfHeads()
    {
        var (vm, _) = await StartableWizardAsync();
        using var _ = vm;
        vm.SelectedFramework = TrainingFramework.PyTorch;
        vm.SelectedArchitecture = AttentionContract.Architecture;
        Assert.True(vm.StartTrainingCommand.CanExecute(null));

        var hidden = new TrainingHyperparameterEntry { Key = "hidden", Value = "6" };   // default heads 4
        vm.Hyperparameters.Add(hidden);
        Assert.NotNull(vm.HyperparameterDependencyMessage);
        Assert.False(vm.StartTrainingCommand.CanExecute(null));

        hidden.Value = "64";
        Assert.Null(vm.HyperparameterDependencyMessage);
        Assert.True(vm.StartTrainingCommand.CanExecute(null));

        vm.SelectedArchitecture = "lstm";                                              // another architecture is not judged
        hidden.Value = "6";
        Assert.Null(vm.HyperparameterDependencyMessage);
    }

    [Fact]
    public async Task SelectedSymbolSummary_CountsTheUnionOfTheTickedRows_AndFollowsEveryChange()
    {
        var (store, _, _) = TreeStore();
        using var vm = new TrainingWizardViewModel(watchlistManager: new DummyWatchlistManager(Array.Empty<WatchlistProfile>()),
            marketDataProvider: new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL", "AMZN"), tickerStore: store);
        await WaitForInitializationAsync(vm);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        string Expected(int count) => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            StockAnalyzer.Avalonia.Services.LocalizationManager.Instance["TrainingWizard_SelectedTickers"], count);

        Assert.NotEqual(Expected(4), Expected(2));                        // the text really carries the count
        Assert.Equal(Expected(4), vm.SelectedSymbolSummary);              // "All tickers" is ticked first: the whole market list, not only the owner's list
        vm.SourceTree[1].Children[0].IsSelected = true;                  // Tech Watch is ticked: All tickers goes off by itself, only the list remains
        Assert.Equal(Expected(2), vm.SelectedSymbolSummary);
        vm.SourceTree[0].IsSelected = true;                              // ticking All tickers again is respected: the union is everything
        Assert.Equal(Expected(4), vm.SelectedSymbolSummary);
        Assert.Contains(nameof(TrainingWizardViewModel.SelectedSymbolSummary), changed);
    }

    [Fact]
    public async Task TickedAllTickers_UsesTheMarketDataProviderListAndTheCheckIsNotRepeatedPerChange()
    {
        var (store, _, _) = TreeStore();
        var orchestrator = new FakeTrainingOrchestrator();
        using var vm = new TrainingWizardViewModel(orchestrator: orchestrator, watchlistManager: new DummyWatchlistManager(Array.Empty<WatchlistProfile>()),
            marketDataProvider: new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL", "AMZN"), tickerStore: store);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        for (int i = 0; i < 5; i++) _ = vm.StartTrainingCommand.CanExecute(null);
        Assert.Equal(0, store.Resolutions);                             // "All tickers" never asks the store; the answer is cached
        await vm.StartTrainingCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "AAPL", "AMZN", "GOOGL", "MSFT" }, orchestrator.CapturedConfig!.Symbols.OrderBy(s => s).ToArray());
    }

    [Fact]
    public async Task StartTrainingAsync_AllTickersScope_ResolvesFullMasterListIntoConfig()
    {
        var market = new DummyMarketDataProvider("AAPL", "MSFT", "GOOGL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(
            new[] { "AAPL", "MSFT", "GOOGL" }.OrderBy(s => s),
            orchestrator.CapturedConfig!.Symbols.OrderBy(s => s));
        Assert.Equal(TrainingFeatureModeForTest, orchestrator.CapturedConfig.FeatureMode);
    }

    [Fact]
    public async Task StartTrainingAsync_DefaultValidationSettings_ConfigUsesWalkForwardDefaults()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        Assert.Equal(WalkForwardDataRequirement.DefaultSplitCount, vm.NSplits);
        Assert.Null(vm.Gap);
        Assert.Null(vm.OosTailDays);

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(WalkForwardDataRequirement.DefaultSplitCount, orchestrator.CapturedConfig!.NSplits);
        Assert.Null(orchestrator.CapturedConfig.Gap);
        Assert.Null(orchestrator.CapturedConfig.OosTailDays);
    }

    [Fact]
    public async Task StartTrainingAsync_CustomValidationSettings_FlowIntoConfig()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        vm.NSplits = 8;
        vm.Gap = 12;
        vm.OosTailDays = 90;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(8, orchestrator.CapturedConfig!.NSplits);
        Assert.Equal(12, orchestrator.CapturedConfig.Gap);
        Assert.Equal(90, orchestrator.CapturedConfig.OosTailDays);
    }

    [Fact]
    public async Task InvalidDateRangeShowsLocalizedValidationReasonWithoutStarting()
    {
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator,
            marketDataProvider: new DummyMarketDataProvider("AAPL"));
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        vm.StartDate = new DateTime(2026, 10, 2);
        vm.EndDate = new DateTime(2026, 10, 1);

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationManager.Instance["TrainingWizard_Validation_DateRange"], vm.StatusMessage);
        Assert.Null(orchestrator.CapturedConfig);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void StartTrainingCommand_NSplitsBelowTwo_CannotExecute(int nSplits)
    {
        var vm = new TrainingWizardViewModel(
            orchestrator: new FakeTrainingOrchestrator(),
            marketDataProvider: new DummyMarketDataProvider("AAPL"))
        {
            Horizon = 5,
            NSplits = nSplits,
        };

        Assert.False(vm.StartTrainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task OnTrainingProgress_FoldMetricLines_PopulateFoldResultsTable()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            MetricLines =
            {
                FoldMetric(fold: 0, splits: 3, n: 120, accuracy: 0.55, baseline: 0.40, macroF1: 0.52, logloss: 0.98),
                FoldMetric(fold: 1, splits: 3, n: 130, accuracy: 0.58, baseline: 0.41, macroF1: 0.54, logloss: 0.95),
                FoldMetric(fold: 2, splits: 3, n: 140, accuracy: 0.60, baseline: 0.42, macroF1: 0.57, logloss: 0.90),
                // out-of-sample line: not a fold row, must be ignored by the table
                new Dictionary<string, double> { ["oos_tail_days"] = 90, ["oos_n"] = 88, ["oos_accuracy"] = 0.53 },
                // final aggregate line: also not a fold row
                new Dictionary<string, double> { ["accuracy"] = 0.59, ["macro_f1"] = 0.55 },
            },
        };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);
        await WaitForInitializationAsync(vm); // pump the Progress<T> callbacks

        Assert.True(vm.HasFoldResults);
        Assert.Equal(3, vm.FoldResults.Count);
        Assert.Equal(new[] { "1/3", "2/3", "3/3" }, vm.FoldResults.Select(r => r.FoldLabel));
        Assert.Equal(120, vm.FoldResults[0].SampleCount);
        Assert.Equal(0.55, vm.FoldResults[0].Accuracy, precision: 6);
        Assert.Equal(0.42, vm.FoldResults[2].BaselineAccuracy, precision: 6);
        Assert.Equal(0.60, vm.FoldResults[2].Accuracy, precision: 6);
    }

    [Fact]
    public async Task StartTrainingAsync_SecondRun_ClearsPreviousFoldResults()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            MetricLines = { FoldMetric(fold: 0, splits: 2, n: 100, accuracy: 0.5, baseline: 0.4, macroF1: 0.5, logloss: 1.0) },
        };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);
        await WaitForInitializationAsync(vm);
        Assert.Single(vm.FoldResults);

        await vm.StartTrainingCommand.ExecuteAsync(null);
        await WaitForInitializationAsync(vm);
        Assert.Single(vm.FoldResults); // cleared then repopulated, not appended
    }

    private static Dictionary<string, double> FoldMetric(
        int fold, int splits, int n, double accuracy, double baseline, double macroF1, double logloss,
        double? holdout = null)
    {
        var metric = new Dictionary<string, double>
        {
            ["fold"] = fold,
            ["n_splits"] = splits,
            ["fold_n"] = n,
            ["fold_accuracy"] = accuracy,
            ["fold_baseline_accuracy"] = baseline,
            ["fold_macro_f1"] = macroF1,
            ["fold_multi_logloss"] = logloss,
        };
        if (holdout is { } h)
        {
            metric["fold_is_holdout"] = h;
        }

        return metric;
    }

    [Fact]
    public void FoldMetricRow_FromMetric_ParsesFoldIsHoldoutFlag()
    {
        var holdoutRow = FoldMetricRow.FromMetric(
            FoldMetric(fold: 4, splits: 5, n: 100, accuracy: 0.6, baseline: 0.4, macroF1: 0.5, logloss: 1.0, holdout: 1.0));
        var referenceRow = FoldMetricRow.FromMetric(
            FoldMetric(fold: 0, splits: 5, n: 100, accuracy: 0.9, baseline: 0.4, macroF1: 0.8, logloss: 0.5, holdout: 0.0));

        Assert.NotNull(holdoutRow);
        Assert.NotNull(referenceRow);
        Assert.True(holdoutRow!.IsHoldout);
        Assert.False(referenceRow!.IsHoldout);

        // A trainer output that predates the fold_is_holdout key must not claim holdout status.
        var legacyRow = FoldMetricRow.FromMetric(new Dictionary<string, double>
        {
            ["fold"] = 0,
            ["fold_accuracy"] = 0.5,
        });
        Assert.NotNull(legacyRow);
        Assert.False(legacyRow!.IsHoldout);
    }

    [Fact]
    public void FoldMetricRow_FromMetric_ParsesRegressionRow()
    {
        var row = FoldMetricRow.FromMetric(new Dictionary<string, double>
        {
            ["fold"] = 3,
            ["n_splits"] = 5,
            ["fold_n"] = 219,
            ["fold_rmse"] = 0.0439,
            ["fold_mae"] = 0.0325,
            ["fold_directional_accuracy"] = 0.5525,
            ["fold_rmse_baseline"] = 0.06,
            ["fold_is_holdout"] = 1.0,
        });

        Assert.NotNull(row);
        Assert.True(row!.IsRegression);
        Assert.Equal(0.0439, row.Rmse);
        Assert.Equal(0.0325, row.Mae);
        Assert.Equal(0.5525, row.DirectionalAccuracy);
        Assert.Equal(0.06, row.RmseBaseline);
        Assert.True(row.IsHoldout);
        // Classification-only fields must never fall back to 0 for a metric a regression head
        // does not estimate (per 02_ONNX.md's Confidence guardrail).
        Assert.True(double.IsNaN(row.Accuracy));
        Assert.True(double.IsNaN(row.MacroF1));
        Assert.True(double.IsNaN(row.BaselineAccuracy));
        Assert.True(double.IsNaN(row.MultiLogloss));
    }

    [Fact]
    public void FoldMetricRow_FromMetric_ClassificationRow_IsNotRegression()
    {
        var row = FoldMetricRow.FromMetric(
            FoldMetric(fold: 0, splits: 5, n: 100, accuracy: 0.6, baseline: 0.4, macroF1: 0.5, logloss: 1.0));

        Assert.NotNull(row);
        Assert.False(row!.IsRegression);
        Assert.True(double.IsNaN(row.Rmse));
        Assert.True(double.IsNaN(row.Mae));
        Assert.True(double.IsNaN(row.DirectionalAccuracy));
        Assert.True(double.IsNaN(row.RmseBaseline));
    }

    [Fact]
    public async Task StartTrainingAsync_Success_SetsLastResultAndRecordsExperimentLog()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "20260829-000000",
                Success = true,
                ExitCode = 0,
                OnnxArtifactPath = Path.Combine(Path.GetTempPath(), "artifacts", "model.onnx"),
            },
        };
        var experimentLog = new FakeExperimentLogService();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, experimentLogService: experimentLog, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.Same(orchestrator.ResultToReturn, vm.LastResult);
        Assert.False(vm.IsTraining);
        Assert.NotNull(experimentLog.Captured);
        Assert.Equal("20260829-000000", experimentLog.Captured!.Value.result.RunId);
        // The config recorded to the experiment log carries the orchestrator-assigned RunId
        // (result.RunId) even though TrainingOrchestrator.StartTrainingAsync's own internal
        // `config = config with { RunId = runId }` reassignment is local to that method and
        // never mutates the config this ViewModel passed in (regression: config.json was
        // missing run_id even though metrics.json had it, since both were previously recorded
        // from the same un-mutated reference). Otherwise it's the exact config submitted to the
        // orchestrator.
        Assert.Equal("20260829-000000", experimentLog.Captured.Value.config.RunId);
        Assert.Equal(orchestrator.CapturedConfig! with { RunId = "20260829-000000" }, experimentLog.Captured.Value.config);
    }

    [Fact]
    public async Task StartTrainingAsync_TrainerFailure_SetsStatusMessageFromResult()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "run-fail",
                Success = false,
                ExitCode = 3,
                Message = "trainer backend not available",
            },
        };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.Equal("trainer backend not available", vm.StatusMessage);
        Assert.False(vm.DeployToModelsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelTrainingCommand_WhileRunning_CancelsTheOrchestratorToken()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var gate = new TaskCompletionSource();
        var orchestrator = new FakeTrainingOrchestrator { Gate = gate };
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        var runTask = vm.StartTrainingCommand.ExecuteAsync(null);
        await orchestrator.EnteredAsync; // fake orchestrator signals it has started and is waiting on the gate

        Assert.True(vm.IsTraining);
        Assert.True(vm.CancelTrainingCommand.CanExecute(null));

        // Cancelling ct fires the fake's registration, which cancels the gate itself -- no
        // separate release needed (and calling gate.SetResult() here would race with that
        // registration and risk an InvalidOperationException on an already-completed TCS).
        vm.CancelTrainingCommand.Execute(null);

        await runTask;

        Assert.True(orchestrator.WasCancelled);
        Assert.False(vm.IsTraining);
    }

    [Fact]
    public async Task DeployToModelsAsync_AfterSuccessfulRun_CallsModelDeploymentServiceWithArtifactPaths()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var mockOnnx = Path.Combine(Path.GetTempPath(), "artifacts", "model.onnx");
        var mockMetrics = Path.Combine(Path.GetTempPath(), "artifacts", "model.onnx.metrics.json");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "run-ok",
                Success = true,
                ExitCode = 0,
                OnnxArtifactPath = mockOnnx,
                MetricsArtifactPath = mockMetrics,
            },
        };
        var deployment = new FakeModelDeploymentService();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, modelDeploymentService: deployment, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.True(vm.DeployToModelsCommand.CanExecute(null));
        await vm.DeployToModelsCommand.ExecuteAsync(null);

        Assert.Equal((mockOnnx, (string?)mockMetrics), deployment.Captured);
    }

    [Fact]
    public async Task DeployToModelsAsync_WhenRegistryIndicatesNotActive_ShowsNotActivatedMessage()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var mockOnnx = Path.Combine(Path.GetTempPath(), "artifacts", "model.onnx");
        var mockMetrics = Path.Combine(Path.GetTempPath(), "artifacts", "model.onnx.metrics.json");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "run-ok",
                Success = true,
                ExitCode = 0,
                OnnxArtifactPath = mockOnnx,
                MetricsArtifactPath = mockMetrics,
            },
        };
        var deployment = new FakeModelDeploymentService();
        var registry = new FakeGenerationRegistry { ActiveId = "different-active-id" };
        var vm = new TrainingWizardViewModel(
            orchestrator: orchestrator,
            modelDeploymentService: deployment,
            marketDataProvider: market,
            modelRegistry: registry);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.True(vm.DeployToModelsCommand.CanExecute(null));
        await vm.DeployToModelsCommand.ExecuteAsync(null);

        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("test-generation", vm.StatusMessage);
    }

    [Fact]
    public async Task DeployToModelsAsync_NotActive_MessageNamesSettingsAiPredictionsAndConfirm()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var mockOnnx = Path.Combine(Path.GetTempPath(), "artifacts", "model.onnx");
        var orchestrator = new FakeTrainingOrchestrator
        {
            ResultToReturn = new TrainingRunResult
            {
                RunId = "run-ok",
                Success = true,
                ExitCode = 0,
                OnnxArtifactPath = mockOnnx,
                MetricsArtifactPath = mockOnnx + ".metrics.json",
            },
        };
        var vm = new TrainingWizardViewModel(
            orchestrator: orchestrator,
            modelDeploymentService: new FakeModelDeploymentService(),
            marketDataProvider: market,
            modelRegistry: new FakeGenerationRegistry { ActiveId = "different-active-id" });
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        await vm.StartTrainingCommand.ExecuteAsync(null);
        await vm.DeployToModelsCommand.ExecuteAsync(null);

        // The expectation follows the resource, so the test checks the wiring (key + model id), not a literal.
        string template = LocalizationManager.Instance["TrainingWizard_Status_RegisteredNotActivated"];
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, template, "test-generation"), vm.StatusMessage);
        Assert.Contains("{0}", template);
        Assert.DoesNotContain("window size or score", template);
    }

    [Fact]
    public async Task StartTrainingAsync_ChannelSelected_ComposesFeatureSpec()
    {
        // There is no separate on/off toggle any more (the [特徴量] tab always shows the composer):
        // UseComposedFeatures is now computed from FeaturePicker.HasChannels, so selecting a
        // channel alone is enough to route the run through PredictionFeatureMode.ComposedFeatures.
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        vm.FeaturePicker.IncludeClose = true;
        Assert.True(vm.UseComposedFeatures);

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(PredictionFeatureMode.ComposedFeatures, orchestrator.CapturedConfig!.FeatureMode);
        Assert.NotNull(orchestrator.CapturedConfig.FeatureSpec);
        Assert.Single(orchestrator.CapturedConfig.FeatureSpec!.Channels);
    }

    [Fact]
    public async Task StartTrainingAsync_DefaultTargetType_ConfigUsesClassification()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(TargetType.Classification, orchestrator.CapturedConfig!.TargetType);
    }

    [Fact]
    public async Task StartTrainingAsync_RegressionTargetSelected_ConfigCarriesRegression()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        vm.SelectedTargetType = TargetType.Regression;

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.NotNull(orchestrator.CapturedConfig);
        Assert.Equal(TargetType.Regression, orchestrator.CapturedConfig!.TargetType);
    }

    [Fact]
    public async Task StartTrainingAsync_NoChannelsSelected_KeepsOhlcvMinMaxAndNullSpec()
    {
        var market = new DummyMarketDataProvider("AAPL");
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator, marketDataProvider: market);
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;

        Assert.False(vm.UseComposedFeatures);
        Assert.True(vm.StartTrainingCommand.CanExecute(null));

        await vm.StartTrainingCommand.ExecuteAsync(null);

        Assert.Equal(PredictionFeatureMode.OhlcvMinMax, orchestrator.CapturedConfig!.FeatureMode);
        Assert.Null(orchestrator.CapturedConfig.FeatureSpec);
    }

    [Fact]
    public async Task FixedPreprocessingPreservesLagOrderAndClipChoice()
    {
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator,
            marketDataProvider: new DummyMarketDataProvider("AAPL"));
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        vm.LagsText = "5,1";
        Assert.False(vm.StartTrainingCommand.CanExecute(null));
        vm.UseFixedZScore = true;
        vm.UseClipSigma = true;
        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        await vm.StartTrainingCommand.ExecuteAsync(null);
        Assert.True(orchestrator.CapturedConfig!.FixedZScore);
        Assert.Equal(new[] { 5, 1 }, orchestrator.CapturedConfig.Lags);
        Assert.Equal(3.0, orchestrator.CapturedConfig.ClipSigma);
    }

    [Theory]
    [InlineData("0.001", 0.001)]
    [InlineData("5E-324", double.Epsilon)]
    public async Task PositiveClipThresholdBelowOldUiMinimumFlowsIntoTrainingConfig(string text, double expected)
    {
        var orchestrator = new FakeTrainingOrchestrator();
        var vm = new TrainingWizardViewModel(orchestrator: orchestrator,
            marketDataProvider: new DummyMarketDataProvider("AAPL"));
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        vm.UseFixedZScore = true;
        vm.UseClipSigma = true;
        vm.ClipSigmaText = text;

        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        Assert.False(vm.HasErrors);
        Assert.Equal(expected == double.Epsilon ? 0m : 0.001m, vm.ClipSigma);
        await vm.StartTrainingCommand.ExecuteAsync(null);
        Assert.Equal(expected, orchestrator.CapturedConfig!.ClipSigma);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.001")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("invalid")]
    public async Task InvalidClipThresholdCannotStartTraining(string text)
    {
        var vm = new TrainingWizardViewModel(orchestrator: new FakeTrainingOrchestrator(),
            marketDataProvider: new DummyMarketDataProvider("AAPL"));
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        vm.UseFixedZScore = true;
        vm.UseClipSigma = true;
        vm.ClipSigmaText = text;

        Assert.False(vm.StartTrainingCommand.CanExecute(null));
        Assert.True(vm.HasErrors);
        Assert.False(string.IsNullOrWhiteSpace(vm.ClipSigmaValidationMessage));
        Assert.Single(vm.GetErrors(nameof(vm.ClipSigmaText)).Cast<string>());
        Assert.Equal(0m, vm.ClipSigma);
    }

    [Fact]
    public async Task ClipValidationFollowsDependencyAndRepairsWithoutStaleErrors()
    {
        var vm = new TrainingWizardViewModel(orchestrator: new FakeTrainingOrchestrator(),
            marketDataProvider: new DummyMarketDataProvider("AAPL"));
        await WaitForInitializationAsync(vm);
        vm.Horizon = 5;
        var changes = new List<string?>();
        vm.ErrorsChanged += (_, e) => changes.Add(e.PropertyName);

        vm.UseClipSigma = true;
        Assert.True(vm.HasErrors);
        Assert.False(vm.StartTrainingCommand.CanExecute(null));
        vm.UseFixedZScore = true;
        Assert.False(vm.HasErrors);
        vm.ClipSigmaText = "invalid";
        Assert.True(vm.HasErrors);
        vm.ClipSigmaText = "5E-324";
        Assert.False(vm.HasErrors);
        Assert.Equal(0m, vm.ClipSigma);
        Assert.True(vm.StartTrainingCommand.CanExecute(null));
        vm.UseClipSigma = false;
        Assert.Empty(vm.GetErrors(nameof(vm.ClipSigmaText)).Cast<string>());
        Assert.Contains(nameof(vm.ClipSigmaText), changes);
    }

    // --- fixtures --------------------------------------------------------

    private const PredictionFeatureMode TrainingFeatureModeForTest = PredictionFeatureMode.OhlcvMinMax;

    private static async Task WaitForInitializationAsync(TrainingWizardViewModel vm)
    {
        // The VM's constructor fires an unawaited InitializeAsync(); yield a few times so its
        // continuations (which only ever touch in-memory dummies, no real I/O) have run before
        // the test inspects the populated collections.
        for (var i = 0; i < 5; i++)
        {
            await Task.Yield();
        }
    }

    private static WatchlistProfile MakeProfile(string name, bool isPortfolio, params string[] tickers) =>
        new(Guid.NewGuid(), name, IndicatorColor.Gray, isPortfolio,
            tickers.Select(t => new WatchlistItem(t, DateTimeOffset.UtcNow)).ToList());

    private sealed class FakeTrainingOrchestrator : ITrainingOrchestrator
    {
        public TrainingJobConfig? CapturedConfig { get; private set; }
        public TrainingRunResult ResultToReturn { get; set; } = new() { RunId = "fake-run", Success = true, ExitCode = 0 };
        public TaskCompletionSource? Gate { get; set; }
        public bool WasCancelled { get; private set; }

        /// <summary>Metric payloads reported (in order) as <c>METRIC:</c>-equivalent progress updates before completion.</summary>
        public List<IReadOnlyDictionary<string, double>> MetricLines { get; } = new();

        private readonly TaskCompletionSource _entered = new();
        public Task EnteredAsync => _entered.Task;

        public async Task<TrainingRunResult> StartTrainingAsync(
            TrainingJobConfig config, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
        {
            CapturedConfig = config;
            progress?.Report(new TrainingProgress { Stage = "load", Percent = 0 });

            foreach (var metric in MetricLines)
            {
                progress?.Report(new TrainingProgress { Stage = "evaluate", Percent = 92, Metric = metric });
            }

            if (Gate is not null)
            {
                _entered.TrySetResult();
                using var registration = ct.Register(() => { WasCancelled = true; Gate.TrySetCanceled(); });
                await Gate.Task;
                ct.ThrowIfCancellationRequested();
            }

            progress?.Report(new TrainingProgress { Stage = "done", Percent = 100 });
            return ResultToReturn;
        }
    }

    private sealed class FakeModelDeploymentService : IModelDeploymentService
    {
        public (string onnx, string? metrics)? Captured { get; private set; }

        public Task<ModelGenerationManifest> RegisterGenerationAsync(
            string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default)
        {
            Captured = (onnxSourcePath, metricsSourcePath);
            return Task.FromResult(new ModelGenerationManifest(1, "test-generation", "model.onnx", "",
                Array.Empty<ModelSidecar>(), "", "", "classification", "daily", 1, "Up,Down,Neutral", 0.5));
        }

        public Task<string> DeployAsync(string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default)
        {
            Captured = (onnxSourcePath, metricsSourcePath);
            return Task.FromResult(Path.Combine(Path.GetTempPath(), "Data", "Models", "model.onnx"));
        }
    }

    private sealed class FakeGenerationRegistry : IModelGenerationRegistry
    {
        public string? ActiveId { get; set; }
        public string? PreviousId => null;
        public IReadOnlyList<ModelGenerationSummary> List() => Array.Empty<ModelGenerationSummary>();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default) => Task.FromResult(true);
        public ModelGenerationLease? AcquireActive() => null;
    }

    private sealed class FakeExperimentLogService : IExperimentLogService
    {
        public (TrainingJobConfig config, TrainingRunResult result)? Captured { get; private set; }

        public Task RecordAsync(TrainingJobConfig config, TrainingRunResult result, CancellationToken ct = default)
        {
            Captured = (config, result);
            return Task.CompletedTask;
        }
    }

    private sealed class DummyMarketDataProvider : IMarketDataProvider
    {
        private readonly List<string> _tickers;

        public DummyMarketDataProvider(params string[] tickers) => _tickers = tickers.ToList();

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => Task.FromResult<IReadOnlyList<string>>(_tickers);
        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => Task.FromResult<IReadOnlyList<string>>(_tickers);
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
        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => Task.FromResult(0);
    }

    private sealed class DummyWatchlistManager : IWatchlistManager
    {
        private readonly List<WatchlistProfile> _profiles;

        public DummyWatchlistManager(IEnumerable<WatchlistProfile> profiles) => _profiles = profiles.ToList();

        public event EventHandler? WatchlistsChanged;

        public IReadOnlyList<WatchlistProfile> GetAllProfiles() => _profiles;
        public WatchlistProfile? GetProfileById(Guid profileId) => _profiles.FirstOrDefault(p => p.Id == profileId);
        public WatchlistProfile CreateProfile(string name, IndicatorColor color, bool isPortfolio = false) => throw new NotImplementedException();
        public void UpdateProfileName(Guid profileId, string name) { }
        public void DeleteProfile(Guid profileId) { }
        public void AddTickerToProfile(Guid profileId, string ticker) { }
        public void AddTickersToProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickerFromProfile(Guid profileId, string ticker) { }
        public void RemoveTickersFromProfile(Guid profileId, IEnumerable<string> tickers) { }
        public void RemoveTickersFromAllProfiles(IEnumerable<string> tickers) { }
        public void Initialize(IEnumerable<WatchlistProfile> profiles) { }

        /// <summary>Test-only: simulates a later load/edit (e.g. <c>WorkspaceCoordinator.Initialize</c>
        /// completing after this VM was constructed, or the user creating a watchlist elsewhere) by
        /// replacing the profile set and raising <see cref="WatchlistsChanged"/>.</summary>
        public void SetProfiles(IEnumerable<WatchlistProfile> profiles)
        {
            _profiles.Clear();
            _profiles.AddRange(profiles);
            WatchlistsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
