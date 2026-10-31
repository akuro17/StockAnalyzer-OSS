using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// IoHydrationWebAiReview P-1 (observation only, no production change): what actually happens, in order, when the user
/// loads a workspace file by hand (<c>MainWindowViewModel.LoadWorkspaceCommand</c> -> <c>WorkspaceCoordinator.ApplyLoadedWorkspaceAsync</c>).
/// The three questions: (1) how many layout-save requests are made and how many does the scheduler ignore, (2) does
/// <c>MainWindowViewModel.Receive(TickerSelectedMessage)</c> run, (3) does the chart's LoadData run, and through which path.
/// The test prints the trace and pins only the facts the decision rule of the plan depends on.
/// </summary>
// Shares the static WeakReferenceMessenger.Default with other MainWindowViewModel/TickerListViewModel-hosting tests.
[Collection("MessengerSharedState")]
public class ManualWorkspaceLoadEventTraceTests
{
    private readonly ITestOutputHelper _output;
    public ManualWorkspaceLoadEventTraceTests(ITestOutputHelper output) => _output = output;

    /// <summary>Models the UI dispatcher: <c>PostAsync</c> runs inline (the caller is already on the UI thread), <c>Post</c>
    /// is queued until the thread is free, which is what <see cref="Flush"/> stands for.</summary>
    private sealed class QueueingDispatcher : IDispatcherService
    {
        private readonly Queue<Action> _queue = new();
        public int QueuedCount => _queue.Count;
        public void Post(Action action) => _queue.Enqueue(action);
        public void Post<T>(Action<T> action, T state) => _queue.Enqueue(() => action(state));
        public Task PostAsync(Func<Task> action) => action();
        public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
        public void Flush() { while (_queue.Count > 0) _queue.Dequeue()(); }
    }

    private sealed class Trace
    {
        private readonly LayoutStateStore _store;
        public Trace(LayoutStateStore store) => _store = store;
        public List<string> Lines { get; } = new();
        public int RequestSaveCalls { get; set; }
        public int RequestSaveIgnored { get; set; }
        public int ForceSaveCalls { get; set; }
        public int TickerSelectedSent { get; set; }
        public int DiskSaves { get; set; }
        public void Add(string text) => Lines.Add($"[{_store.LifecycleState}] {text}");
    }

    /// <summary>The real scheduler behind a recorder: whether the state gate swallowed a request is read from the store, as the scheduler does.</summary>
    private sealed class RecordingScheduler : ILayoutSaveScheduler
    {
        private readonly LayoutSaveScheduler _inner;
        private readonly LayoutStateStore _store;
        private readonly Trace _trace;

        public RecordingScheduler(LayoutStateStore store, Trace trace)
        {
            _store = store;
            _trace = trace;
            _inner = new LayoutSaveScheduler(store, NullLogger<LayoutSaveScheduler>.Instance, throttleMs: 60000);
        }

        public void RequestSave(LayoutChangeReason reason)
        {
            bool ignored = _store.LifecycleState != WorkspaceLifecycleState.Ready && reason != LayoutChangeReason.Shutdown;
            _trace.RequestSaveCalls++;
            if (ignored) _trace.RequestSaveIgnored++;
            _trace.Add($"RequestSave({reason}) {(ignored ? "IGNORED by the state gate" : "accepted (debounce timer armed)")}");
            _inner.RequestSave(reason);
        }

        public Task ForceSaveImmediateAsync()
        {
            _trace.ForceSaveCalls++;
            _trace.Add("ForceSaveImmediateAsync (no state gate in the scheduler)");
            return _inner.ForceSaveImmediateAsync();
        }

        public void RegisterSaveAction(Func<Task> saveAction) => _inner.RegisterSaveAction(saveAction);
        public void Cancel() => _inner.Cancel();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>Registered with an explicit handler (not <c>IRecipient</c>) so the messenger source generator is not involved.</summary>
    private sealed class SentTickerRecorder
    {
        private readonly Trace _trace;
        public SentTickerRecorder(Trace trace) => _trace = trace;

        public void OnTickerSelected(TickerSelectedMessage message)
        {
            _trace.TickerSelectedSent++;
            _trace.Add($"TickerSelectedMessage({message.Value}) broadcast");
        }
    }

    private static readonly FieldInfo PredictionRequestId =
        typeof(ChartViewModel).GetField("_predictionRequestId", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Every <c>Symbol</c> change and every started chart load bumps this counter in its synchronous part.</summary>
    private static int ChartLoadMarker(ChartViewModel chart) => (int)PredictionRequestId.GetValue(chart)!;

    private const string Loaded = "MSFT";
    private const string Before = "AAPL";

    private sealed class Rig : IDisposable
    {
        public LayoutStateStore Store { get; } = new();
        public Trace Trace { get; }
        public QueueingDispatcher Dispatcher { get; } = new();
        public Mock<IWorkspaceSerializationService> Serialization { get; } = new();
        public MainWindowViewModelParts Parts { get; }
        public MainWindowViewModel Vm => Parts.Vm;
        public SentTickerRecorder Recorder { get; }

        public Rig()
        {
            Trace = new Trace(Store);
            Serialization.Setup(s => s.ImportPortableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WorkspaceSettings { SelectedTicker = Loaded });
            Serialization.Setup(s => s.SaveAsync(It.IsAny<WorkspaceSettings>(), It.IsAny<string>()))
                .Callback(() => { Trace.DiskSaves++; Trace.Add("workspace SaveAsync reached the serialization service"); })
                .Returns(Task.CompletedTask);

            var watchlist = new Mock<IWatchlistManager>();
            watchlist.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
            var coordinator = new WorkspaceCoordinator(
                Store,
                Serialization.Object,
                watchlist.Object,
                Options.Create(new WorkspaceCoordinatorOptions { WorkspacePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sa_p1_unused_" + Guid.NewGuid().ToString("N") + ".json") }),
                Dispatcher,
                NullLogger<WorkspaceCoordinator>.Instance);

            Parts = MainWindowViewModelFactory.Create(
                dispatcher: Dispatcher,
                stateStore: Store,
                layoutSaveScheduler: new RecordingScheduler(Store, Trace),
                workspaceCoordinator: coordinator,
                serializationService: Serialization.Object,
                backtestColumnSettingsManager: new Mock<StockAnalyzer.Core.Services.Backtest.Configuration.IBacktestColumnSettingsManager>().Object);
            Recorder = new SentTickerRecorder(Trace);
            WeakReferenceMessenger.Default.Register<SentTickerRecorder, TickerSelectedMessage>(Recorder, static (r, m) => r.OnTickerSelected(m));

            // Startup is over: the app is in the Ready state with a ticker selected, as when the user opens the menu.
            Store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
            Store.LifecycleState = WorkspaceLifecycleState.Ready;
            Vm.SelectedTicker = Before;
            Dispatcher.Flush();
        }

        public void Dispose()
        {
            WeakReferenceMessenger.Default.Unregister<TickerSelectedMessage>(Recorder);
            Vm.Dispose();
        }
    }

    [Fact]
    public async Task ManualLoad_TraceOfSaveRequestsTickerBroadcastAndChartLoad()
    {
        using var rig = new Rig();
        var chart = rig.Vm.ChartViewModel;
        rig.Parts.Dialog
            .Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string[]>()))
            .ReturnsAsync("workspace.json");
        rig.Trace.Lines.Clear();
        int markerBefore = ChartLoadMarker(chart);
        rig.Trace.RequestSaveCalls = rig.Trace.RequestSaveIgnored = rig.Trace.ForceSaveCalls = rig.Trace.TickerSelectedSent = rig.Trace.DiskSaves = 0;

        await rig.Vm.LoadWorkspaceCommand.ExecuteAsync(null);
        int queuedAtCommandEnd = rig.Dispatcher.QueuedCount;
        int markerAfterCommand = ChartLoadMarker(chart);
        rig.Trace.Add($"-- command finished; {queuedAtCommandEnd} Post callback(s) still queued on the dispatcher --");
        rig.Dispatcher.Flush();
        int markerAfterFlush = ChartLoadMarker(chart);

        _output.WriteLine($"Symbol after the load: {chart.Symbol}; SelectedTicker: {rig.Vm.SelectedTicker}");
        _output.WriteLine($"chart load/symbol marker: before={markerBefore} afterCommand={markerAfterCommand} afterFlush={markerAfterFlush}");
        _output.WriteLine($"RequestSave calls={rig.Trace.RequestSaveCalls} ignored={rig.Trace.RequestSaveIgnored}; ForceSaveImmediate calls={rig.Trace.ForceSaveCalls}; TickerSelectedMessage broadcasts={rig.Trace.TickerSelectedSent}; workspace written to the service={rig.Trace.DiskSaves}");
        foreach (var line in rig.Trace.Lines) _output.WriteLine(line);

        // The facts the plan's decision rule needs. (1) The loaded ticker reached the chart.
        Assert.Equal(Loaded, chart.Symbol);
        Assert.Equal(Loaded, rig.Vm.SelectedTicker);
        // (3) The chart reload was started by the setter / Symbol change in the restore itself, before any broadcast could be handled.
        Assert.True(markerAfterCommand > markerBefore, "the chart load must start inside the restore");
        // (2) The broadcast is produced by the setter because the manual path does not pause auto-save.
        Assert.True(rig.Trace.TickerSelectedSent >= 1);
    }

    [Fact]
    public void PausedAutoSave_StillLoadsTheChart_ButDoesNotBroadcastTheTicker()
    {
        // What PauseAutoSave would change if it were added to the manual path: the setter's own chart load is not behind
        // the guard, only the broadcast (and the log line) are.
        using var rig = new Rig();
        var chart = rig.Vm.ChartViewModel;
        rig.Trace.Lines.Clear();
        int markerBefore = ChartLoadMarker(chart);
        rig.Trace.RequestSaveCalls = rig.Trace.RequestSaveIgnored = rig.Trace.TickerSelectedSent = 0;

        bool loadRunningWhilePaused;
        using (rig.Vm.PauseAutoSave())
        {
            rig.Vm.SelectedTicker = Loaded;
            loadRunningWhilePaused = chart.LoadDataCommand.IsRunning; // the command is inside its debounce delay: it was started by the setter
        }
        rig.Dispatcher.Flush();
        // The load itself is not awaited: when it finishes it broadcasts on the shared messenger, and a finished load of this test would race with the
        // view models other tests keep on that messenger (an exception there surfaced in this test in a full-suite run). Started is all this test needs.

        _output.WriteLine($"LoadDataCommand running while auto-save was paused: {loadRunningWhilePaused}");
        _output.WriteLine($"chart load/symbol marker: before={markerBefore} after={ChartLoadMarker(chart)}");
        _output.WriteLine($"RequestSave calls={rig.Trace.RequestSaveCalls}; TickerSelectedMessage broadcasts={rig.Trace.TickerSelectedSent}");
        foreach (var line in rig.Trace.Lines) _output.WriteLine(line);

        Assert.Equal(Loaded, chart.Symbol);
        Assert.True(loadRunningWhilePaused, "the setter starts the chart load itself, behind no auto-save guard");
        Assert.True(ChartLoadMarker(chart) > markerBefore);
        Assert.Equal(0, rig.Trace.TickerSelectedSent);
    }
}
