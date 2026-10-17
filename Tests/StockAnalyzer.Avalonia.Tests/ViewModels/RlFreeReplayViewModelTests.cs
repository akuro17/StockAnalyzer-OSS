using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>The "any ticker and date" card of the RL Playback page (PLAN-C2 section 5.2) and its chart labels (5.3).</summary>
public sealed partial class RlUiTests
{
    private static readonly DateOnly FreeEarliest = new(2015, 1, 1), FreeLatest = new(2019, 6, 3);

    /// <summary>Returns a key followed by the placeholders of its real format, so a test sees which text was chosen and which arguments were used.</summary>
    private sealed class ArgEchoLocalization : ILocalizationService
    {
        private static readonly Dictionary<string, string> Formats = new()
        {
            ["Settings_RlPlayback_Free_Note"] = "{0}|{1}|{2}", ["Settings_RlPlayback_Free_Range"] = "{0}|{1}", ["Settings_RlPlayback_Free_Resolved"] = "{0}",
            ["Settings_RlPlayback_Free_Reject_NotEnoughHistory"] = "{0}", ["Settings_RlPlayback_Free_Reject_NotEnoughBarsBefore"] = "{0}",
            ["Settings_RlPlayback_Free_Reject_NotEnoughBarsAfter"] = "{0}|{1}", ["Rl.Scope.OutOfRange"] = "{0}", ["Rl.Ui.GenerationComparison.Summary"] = "{0}|{1}|{2}|{3}", ["Rl.Scope.SameOverlapsParts"] = "{0}", ["Rl.Scope.ContinuityGap"] = "{0}|{1}|{2}", ["Rl.Scope.ContinuityChange"] = "{0}|{1}|{2}",
        };
        public string GetString(string key) => Formats.TryGetValue(key, out var format) ? key + "|" + format : key;
    }

    private sealed class FakeLists : IRlTickerListSource
    {
        public List<RlTickerListChoice> Lists { get; } = new();
        public Dictionary<Guid, string[]> Tickers { get; } = new();
        public IReadOnlyList<RlTickerListChoice> GetLists() => Lists.ToArray();
        public IReadOnlyList<string> GetTickers(Guid id) => Tickers.TryGetValue(id, out var tickers) ? tickers : Array.Empty<string>();
    }

    private sealed record FreeFixture(RlFreeReplayViewModel Vm, PlaybackData Data, RecordingReplayTarget Target, FakeLists Lists);

    private static async Task<FreeFixture> Free(RlGeneration? generation = null, string[]? tickers = null, Action<PlaybackData>? configure = null,
        ILocalizationService? localization = null, bool select = true)
    {
        var registry = new EmptyRegistry { Generations = Array.Empty<RlGeneration>() };
        var data = new PlaybackData(registry); configure?.Invoke(data);
        var target = new RecordingReplayTarget(); var lists = new FakeLists();
        var vm = new RlFreeReplayViewModel(data, target, new FakeMarketDataProvider(tickers ?? new[] { "SPY", "QQQ", "IWM" }), lists,
            localization ?? NullLocalizationService.Instance, new SynchronousDispatcherService());
        await PlaybackUntil(() => vm.Suggestions.Count > 0);
        if (select) vm.SetModel(generation ?? PlaybackGeneration("a"));
        return new(vm, data, target, lists);
    }

    private static Task FreeRangeLoaded(RlFreeReplayViewModel vm) => PlaybackUntil(() => vm.HasRangeText);

    [Fact]
    public async Task Free_StartsOnTheAgentsOwnTickerWithTheLatestValidDateAndOneClickWorks()
    {
        var f = await Free(); using var vm = f.Vm;
        Assert.Equal("SPY", vm.TickerText); Assert.True(vm.TickerIsKnown); Assert.True(vm.DateIsAuto); Assert.Equal(0, vm.Decision);
        Assert.Equal(RlFiles.Target, vm.SelectedPolicyOption!.Id);
        Assert.Equal(new[] { RlFiles.Target, RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.SmaBaselineId, RlContract.RandomBaselineId }, vm.PolicyOptions.Select(p => p.Id));
        await FreeRangeLoaded(vm);
        Assert.Equal(FreeLatest.ToDateTime(TimeOnly.MinValue), vm.AnchorDate);
        Assert.Equal(FreeEarliest.ToDateTime(TimeOnly.MinValue), vm.DisplayDateStart); Assert.Equal(FreeLatest.ToDateTime(TimeOnly.MinValue), vm.DisplayDateEnd);
        Assert.True(vm.DateIsAuto); Assert.True(vm.CanShowOnChart);
        Assert.Equal(("a", "SPY"), Assert.Single(f.Data.RangeReads));
        Assert.Equal(new[] { "IWM", "QQQ", "SPY" }, vm.Suggestions);
    }

    [Fact]
    public async Task Free_SourceNoteNamesTheAgentTickerAndTrainedSpan()
    {
        var f = await Free(localization: new ArgEchoLocalization()); using var vm = f.Vm;
        await PlaybackUntil(() => vm.ShowNote);
        Assert.Equal("Settings_RlPlayback_Free_Note|SPY|2010-01-04|2019-12-31", vm.SourceNote);
    }

    [Fact]
    public async Task Free_TickerMustBeAnAvailableTickerIgnoringCaseAndTheReasonIsShownOtherwise()
    {
        var f = await Free(); using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        f.Data.RangeReads.Clear();
        vm.TickerText = "ZZZ";
        Assert.False(vm.TickerIsKnown); Assert.True(vm.ShowTickerUnknown); Assert.False(vm.CanShowOnChart);
        Assert.Equal("", vm.RangeText); Assert.Null(vm.DisplayDateStart); Assert.Null(vm.DisplayDateEnd);
        vm.TickerText = "";
        Assert.True(vm.ShowTickerUnknown); Assert.False(vm.CanShowOnChart);
        vm.TickerText = " qqq ";
        Assert.True(vm.TickerIsKnown); Assert.False(vm.ShowTickerUnknown);
        await FreeRangeLoaded(vm);
        Assert.Equal(("a", "QQQ"), Assert.Single(f.Data.RangeReads));   // the canonical spelling of the available ticker is used
        Assert.True(vm.CanShowOnChart);
    }

    [Fact]
    public async Task Free_ANewTickerCancelsTheReadInFlightAndOnlyTheLatestResultIsShown()
    {
        var gate = new TaskCompletionSource<RlAnchorRange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new Dictionary<string, CancellationToken>();
        var f = await Free(configure: d => d.Range = (_, symbol, ct) =>
        {
            lock (tokens) tokens[symbol] = ct;
            return symbol == "QQQ" ? gate.Task.WaitAsync(ct)
                : Task.FromResult(symbol == "IWM" ? new RlAnchorRange(new DateOnly(2016, 1, 1), new DateOnly(2018, 1, 1)) : new RlAnchorRange(FreeEarliest, FreeLatest));
        });
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);                        // SPY
        vm.TickerText = "QQQ";
        await PlaybackUntil(() => { lock (tokens) return tokens.ContainsKey("QQQ"); });
        vm.TickerText = "IWM";
        await PlaybackUntil(() => vm.AnchorDate == new DateTime(2018, 1, 1));
        Assert.True(tokens["QQQ"].IsCancellationRequested);
        gate.TrySetResult(new RlAnchorRange(new DateOnly(2000, 1, 1), new DateOnly(2001, 1, 1)));   // a late result of the cancelled read
        await Task.Delay(50);
        Assert.Equal(new DateTime(2018, 1, 1), vm.AnchorDate); Assert.Equal(new DateTime(2016, 1, 1), vm.DisplayDateStart);
    }

    [Fact]
    public async Task Free_WhileTheRangeOfTheNewTickerIsPendingNoReplayCanStartWithTheOldDate()
    {
        var gate = new TaskCompletionSource<RlAnchorRange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new HashSet<string>();
        var f = await Free(configure: d => d.Range = (_, symbol, ct) =>
        {
            lock (started) started.Add(symbol);
            return symbol == "QQQ" ? gate.Task.WaitAsync(ct)
                : Task.FromResult(symbol == "IWM" ? new RlAnchorRange(new DateOnly(2016, 1, 1), new DateOnly(2018, 1, 1)) : new RlAnchorRange(FreeEarliest, FreeLatest));
        });
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);                                     // SPY: date and range are consistent
        Assert.True(vm.CanShowOnChart);

        vm.TickerText = "QQQ";
        Assert.Equal(FreeLatest.ToDateTime(TimeOnly.MinValue), vm.AnchorDate);   // still SPY's date on screen ...
        Assert.False(vm.CanShowOnChart); Assert.False(vm.ShowOnChartCommand.CanExecute(null));   // ... but no replay with it
        await PlaybackUntil(() => { lock (started) return started.Contains("QQQ"); });
        Assert.False(vm.CanShowOnChart);                               // the read is in flight

        vm.TickerText = "IWM";                                         // a newer edit replaces the pending read
        Assert.False(vm.CanShowOnChart);
        await PlaybackUntil(() => vm.AnchorDate == new DateTime(2018, 1, 1));
        Assert.True(vm.CanShowOnChart);
        gate.TrySetResult(new RlAnchorRange(new DateOnly(2000, 1, 1), new DateOnly(2001, 1, 1)));   // the cancelled read finishes late
        await Task.Delay(50);
        Assert.True(vm.CanShowOnChart); Assert.Equal(new DateTime(2018, 1, 1), vm.AnchorDate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Free_AFailedRangeReadEndsThePendingStateSoTheReplayReportsTheReasonItself(bool rejection)
    {
        var f = await Free(localization: new ArgEchoLocalization(), configure: d => d.Range = (_, symbol, _) => symbol == "SPY"
            ? Task.FromResult(new RlAnchorRange(FreeEarliest, FreeLatest))
            : Task.FromException<RlAnchorRange>(rejection ? new RlExternalReplayException(RlExternalReplayRejection.NoLocalData, "none")
                : new System.IO.IOException("private path")));
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        vm.AnchorDate = new DateTime(2017, 3, 1);                      // the user's own date
        vm.TickerText = "QQQ";
        Assert.False(vm.CanShowOnChart);                               // pending
        await PlaybackUntil(() => vm.CanShowOnChart);                  // the failure ends it
        Assert.Equal(new DateTime(2017, 3, 1), vm.AnchorDate);
        if (rejection) Assert.Equal("Settings_RlPlayback_Free_Reject_NoLocalData", vm.RangeText);
        else Assert.Equal("Rl.Ui.Status.IOFailure", vm.StatusMessage);
    }

    [Fact]
    public async Task Free_AutomaticDateFollowsTheTickerAndAnEditedDateIsKept()
    {
        var f = await Free(configure: d => d.Range = (_, symbol, _) => Task.FromResult(symbol == "SPY"
            ? new RlAnchorRange(FreeEarliest, FreeLatest) : new RlAnchorRange(new DateOnly(2016, 1, 1), new DateOnly(2018, 1, 1))));
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        Assert.Equal(FreeLatest.ToDateTime(TimeOnly.MinValue), vm.AnchorDate);
        vm.TickerText = "QQQ";
        await PlaybackUntil(() => vm.AnchorDate == new DateTime(2018, 1, 1));
        Assert.True(vm.DateIsAuto);

        vm.AnchorDate = new DateTime(2017, 3, 1);          // the user's edit
        Assert.False(vm.DateIsAuto);
        vm.TickerText = "SPY";
        await PlaybackUntil(() => vm.DisplayDateEnd == FreeLatest.ToDateTime(TimeOnly.MinValue));
        Assert.Equal(new DateTime(2017, 3, 1), vm.AnchorDate); Assert.False(vm.DateIsAuto);
    }

    [Fact]
    public async Task Free_ARangeThatCannotBeReadShowsTheRejectionAndClearsTheRange()
    {
        var f = await Free(localization: new ArgEchoLocalization(), configure: d => d.Range = (_, _, _) => Task.FromException<RlAnchorRange>(
            new RlExternalReplayException(RlExternalReplayRejection.NotEnoughHistory, "short")));
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        Assert.Equal($"Settings_RlPlayback_Free_Reject_NotEnoughHistory|{RlContract.SupportLength}", vm.RangeText);
        Assert.Null(vm.DisplayDateStart); Assert.Null(vm.AnchorDate); Assert.False(vm.CanShowOnChart);
    }

    [Fact]
    public async Task Free_ShowOnChartReplaysTheCanonicalTickerAndDateAppliesTheSnapshotAndClosesOnce()
    {
        var f = await Free(); using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        vm.TickerText = "qqq"; await FreeRangeLoaded(vm);
        await PlaybackUntil(() => vm.CanShowOnChart);
        vm.Decision = 7; vm.SelectedPolicyOption = vm.PolicyOptions.Single(p => p.Id == RlContract.SmaBaselineId);
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        var request = Assert.Single(f.Data.ExternalReplays);
        Assert.Equal(new RlExternalReplayRequest("a", "QQQ", FreeLatest, 7, RlContract.SmaBaselineId), request);
        Assert.Single(f.Target.Applied); Assert.False(vm.IsBusy);
        Assert.Equal("Settings_RlPlayback_Status_Shown", vm.StatusMessage);
    }

    [Fact]
    public async Task Free_TheResolvedDayIsReportedOnlyWhenItDiffersFromTheRequestedDay()
    {
        var f = await Free(localization: new ArgEchoLocalization(), configure: d => d.External = (request, _) => Task.FromResult(
            new RlExternalReplayResult(Snapshot(0, 1, -1), request.AnchorDate, request.AnchorDate.AddDays(-2))));
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Equal($"Settings_RlPlayback_Free_Resolved|{FreeLatest.AddDays(-2):yyyy-MM-dd}", vm.StatusMessage);
       
    }

    [Fact]
    public async Task Free_ARejectionLeavesTheChartAndTheWindowAloneAndNamesTheDate()
    {
        var f = await Free(localization: new ArgEchoLocalization(), configure: d => d.External = (_, _) => Task.FromException<RlExternalReplayResult>(
            new RlExternalReplayException(RlExternalReplayRejection.NotEnoughBarsAfter, "late", latestAnchor: new DateOnly(2019, 6, 3))));
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Equal($"Settings_RlPlayback_Free_Reject_NotEnoughBarsAfter|2019-06-03|{RlContract.EpisodeHorizon}", vm.StatusMessage);
        Assert.Empty(f.Target.Applied); Assert.False(vm.IsBusy); Assert.True(vm.CanShowOnChart);
    }

    [Fact]
    public async Task Free_OtherFailuresAndCancellationUseTheCommonMessagesAndKeepTheWindowOpen()
    {
        var f = await Free(configure: d => d.External = (_, _) => Task.FromException<RlExternalReplayResult>(new System.IO.IOException("private path")));
        using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Equal("Rl.Ui.Status.IOFailure", vm.StatusMessage); Assert.False(vm.IsBusy);
        f.Data.External = (_, _) => Task.FromCanceled<RlExternalReplayResult>(new CancellationToken(true));
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Equal("Settings_RlPlayback_Status_Cancelled", vm.StatusMessage);
    }

    [Fact]
    public async Task Free_ContinuousAgentShowsOnlyTheNoticeAndReadsNothing()
    {
        var f = await Free(PlaybackGeneration("c", target: RlContinuousContract.Target)); using var vm = f.Vm;
        Assert.True(vm.IsContinuous); Assert.False(vm.ShowInputs); Assert.True(vm.ShowContinuousNotice); Assert.True(vm.HasModel);
        Assert.False(vm.CanShowOnChart); Assert.False(vm.ShowTickerUnknown);
        await Task.Delay(400);                                  // longer than the input throttle
        Assert.Empty(f.Data.RangeReads); Assert.False(vm.HasRangeText);
    }

    [Fact]
    public async Task Free_NoAgentHidesTheCard()
    {
        var f = await Free(select: false); using var vm = f.Vm;
        Assert.False(vm.HasModel); Assert.False(vm.CanShowOnChart);
        vm.SetModel(PlaybackGeneration("a")); Assert.True(vm.HasModel);
        vm.SetModel(null); Assert.False(vm.HasModel); Assert.Equal("", vm.TickerText); Assert.Null(vm.AnchorDate);
    }

    [Fact]
    public async Task Free_TheListChooserOnlyChangesTheSuggestionsNeverTheTickerOrTheRange()
    {
        var f = await Free(); using var vm = f.Vm;
        var id = Guid.NewGuid();
        f.Lists.Lists.Add(new RlTickerListChoice(id, "Growth")); f.Lists.Tickers[id] = new[] { "qqq", "NOPE" };
        vm.SetModel(PlaybackGeneration("a"));                      // the page refreshes the lists when the agent is applied
        await FreeRangeLoaded(vm);
        Assert.Equal(new Guid?[] { null, id }, vm.ListOptions.Select(o => o.Id));
        Assert.Equal("Growth", vm.ListOptions[1].Name);
        int reads = f.Data.RangeReads.Count;
        vm.SelectedListOption = vm.ListOptions[1];
        Assert.Equal(new[] { "QQQ" }, vm.Suggestions);                  // only tickers that exist can be suggested
        Assert.Equal("SPY", vm.TickerText); Assert.True(vm.DateIsAuto);
        await Task.Delay(400);
        Assert.Equal(reads, f.Data.RangeReads.Count);
        vm.SelectedListOption = vm.ListOptions[0];
        Assert.Equal(new[] { "IWM", "QQQ", "SPY" }, vm.Suggestions);
    }

    [Fact]
    public async Task Free_ABadDecisionEditorOrABusyPageDisablesTheButton()
    {
        var f = await Free(); using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        Assert.True(vm.CanShowOnChart);
        vm.SetEditorValidity("Decision", false); Assert.False(vm.CanShowOnChart);
        vm.SetEditorValidity("Decision", true); Assert.True(vm.CanShowOnChart);
        vm.ParentBusy = true; Assert.False(vm.CanShowOnChart);
        vm.ParentBusy = false; Assert.True(vm.CanShowOnChart);
        vm.SelectedPolicyOption = null; Assert.False(vm.CanShowOnChart);
    }

    [Fact]
    public async Task Free_ChoosingAnotherAgentStartsAgainFromItsOwnTickerAndAutomaticDate()
    {
        var f = await Free(); using var vm = f.Vm;
        await FreeRangeLoaded(vm);
        vm.TickerText = "QQQ"; vm.AnchorDate = new DateTime(2016, 5, 5);
        Assert.False(vm.DateIsAuto);
        vm.SetModel(PlaybackGeneration("b") with { Symbol = "IWM" });
        Assert.Equal("IWM", vm.TickerText); Assert.True(vm.DateIsAuto); Assert.Null(vm.AnchorDate); Assert.Equal(0, vm.Decision);
        await PlaybackUntil(() => vm.AnchorDate is not null);
        Assert.Equal(FreeLatest.ToDateTime(TimeOnly.MinValue), vm.AnchorDate);
    }

    [Fact]
    public async Task FreePage_SelectingAnAgentFeedsTheCardAndBothButtonsExcludeEachOther()
    {
        var registry = new EmptyRegistry { Generations = new[] { PlaybackGeneration("a") } };
        var data = new PlaybackData(registry); var target = new RecordingReplayTarget();
        var free = new RlFreeReplayViewModel(data, target, new FakeMarketDataProvider(new[] { "SPY" }), new FakeLists(),
            NullLocalizationService.Instance, new SynchronousDispatcherService());
        var page = new RlPlaybackViewModel(registry, data, target, NullLocalizationService.Instance, new SynchronousDispatcherService(), free);
        using var _ = page;
        await page.RefreshCommand.ExecuteAsync(null);
        await PlaybackUntil(() => page.BlocksState == RlDialogLoadState.Loaded && free.HasRangeText);
        Assert.Same(free, page.Free); Assert.True(free.HasModel); Assert.Equal("SPY", free.TickerText); Assert.True(page.CanShowOnChart); Assert.True(free.CanShowOnChart);

        var gate = new TaskCompletionSource<RlExternalReplayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        data.External = (request, _) => gate.Task;
        var running = free.ShowOnChartCommand.ExecuteAsync(null);
        await PlaybackUntil(() => free.IsBusy);
        Assert.False(page.CanShowOnChart);                          // the free replay blocks the segment replay
        gate.SetResult(new RlExternalReplayResult(Snapshot(0, 1, -1), FreeLatest, FreeLatest));
        await running;
        Assert.Single(target.Applied); Assert.True(page.CanShowOnChart);   // the free replay is on the chart and the page stays usable

        var segmentGate = new TaskCompletionSource<RlQSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        data.Replay = _ => segmentGate.Task;
        var segment = page.ShowOnChartCommand.ExecuteAsync(null);
        await PlaybackUntil(() => page.IsBusy);
        Assert.False(free.CanShowOnChart);                          // the segment replay blocks the free replay
        segmentGate.SetResult(Snapshot(0, 1, -1));
        await segment;
        Assert.True(free.CanShowOnChart);
    }

    [Fact]
    public void FreePresentation_ShowsNothingExtraForTheTrainingSourceAndNamesScopeAndOutOfRangeChannels()
    {
        var plain = RlPresentation.From(Snapshot(0, 1, -1), NullLocalizationService.Instance);
        Assert.Equal("", plain.ScopeReadout); Assert.False(plain.HasScopeReadout);

        var overlap = RlPresentation.From(Snapshot(0, 1, -1) with { ReplayScope = RlReplayScope.SameInstrumentOverlapsTraining }, NullLocalizationService.Instance);
        Assert.Equal("Rl.Scope.SameOverlaps", overlap.ScopeReadout);
        // With known parts the overlap line names them, in chronological order; the generic line stays for an unknown set.
        var parted = RlPresentation.From(Snapshot(0, 1, -1) with { ReplayScope = RlReplayScope.SameInstrumentOverlapsTraining,
            OverlappingParts = RlSourceParts.Validation | RlSourceParts.FinalEvaluation | RlSourceParts.Train }, NullLocalizationService.Instance);
        Assert.Equal("Rl.Scope.SameOverlapsParts", parted.ScopeReadout);
        var partedEcho = RlPresentation.From(Snapshot(0, 1, -1) with { ReplayScope = RlReplayScope.SameInstrumentOverlapsTraining,
            OverlappingParts = RlSourceParts.FinalEvaluation | RlSourceParts.Train }, new ArgEchoLocalization());
        Assert.Equal("Rl.Scope.SameOverlapsParts|Rl.Scope.Part.Train, Rl.Scope.Part.Final", partedEcho.ScopeReadout);
        Assert.Equal("Rl.Scope.SameAfter", RlPresentation.From(Snapshot(0, 1, -1) with { ReplayScope = RlReplayScope.SameInstrumentAfterTraining,
            OverlappingParts = RlSourceParts.Train }, NullLocalizationService.Instance).ScopeReadout);
        foreach (var (scope, key) in new[] { (RlReplayScope.SameInstrumentAfterTraining, "Rl.Scope.SameAfter"),
                     (RlReplayScope.SameInstrumentBeforeTraining, "Rl.Scope.SameBefore"), (RlReplayScope.OtherInstrument, "Rl.Scope.Other") })
            Assert.Equal(key, RlPresentation.From(Snapshot(0, 1, -1) with { ReplayScope = scope }, NullLocalizationService.Instance).ScopeReadout);

        var findings = ImmutableArray.Create(new RlScalerRangeFinding("lag0:0:price:Open", 0, 1, 2, 3), new RlScalerRangeFinding("lag0:4:indicator:Volume", 0, 1, 2, 3),
            new RlScalerRangeFinding("plain", 0, 1, 2, 3));
        var other = RlPresentation.From(Snapshot(0, 1, -1) with { ReplayScope = RlReplayScope.OtherInstrument, ScalerFindings = findings }, new ArgEchoLocalization());
        Assert.Equal("Rl.Scope.Other" + Environment.NewLine + "Rl.Scope.OutOfRange|Open, Volume, plain", other.ScopeReadout);
        Assert.True(other.HasScopeReadout);

        var continuity = ImmutableArray.Create(new RlContinuityFinding(RlContinuityKind.CalendarGap, 20m, 3m, new DateOnly(2012, 3, 5)),
            new RlContinuityFinding(RlContinuityKind.CloseChange, 0.5m, 0.04m, new DateOnly(2012, 4, 9)));
        var shown = RlPresentation.From(Snapshot(0, 1, -1) with { ReplayScope = RlReplayScope.OtherInstrument, ContinuityFindings = continuity }, new ArgEchoLocalization());
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Equal(string.Join(Environment.NewLine, "Rl.Scope.Other", "Rl.Scope.ContinuityGap|20|2012-03-05|3",
            "Rl.Scope.ContinuityChange|" + 0.5m.ToString("P1", culture) + "|2012-04-09|" + 0.04m.ToString("P1", culture)), shown.ScopeReadout);

        var onlyFindings = RlPresentation.From(Snapshot(0, 1, -1) with { ScalerFindings = findings }, NullLocalizationService.Instance);
        Assert.Equal("Rl.Scope.OutOfRange", onlyFindings.ScopeReadout);
    }
}
