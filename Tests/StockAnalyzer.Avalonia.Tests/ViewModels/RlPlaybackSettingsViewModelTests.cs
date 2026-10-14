using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>Rules R1-R10 of the RL Playback settings page (PLAN-A 4.6); part of the RL fixture so the registry double is shared.</summary>
public sealed partial class RlUiTests
{
    private static readonly DateTimeOffset PlaybackEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RlGeneration PlaybackGeneration(string id, int ageDays = 0, string target = RlFiles.Target)
        => new(id, "SPY", "source", RlContract.ContractHash, "evaluation", 0.1m, 0.2m, new(), PlaybackEpoch.AddDays(-ageDays)) { Target = target };

    private static ImmutableArray<RlEpisodeBlock> PlaybackBlocks(params (string Id, string Partition)[] blocks)
        => blocks.Select((b, i) => new RlEpisodeBlock(b.Id, b.Partition, i, i + 1)).ToImmutableArray();

    private static readonly ImmutableArray<RlEpisodeBlock> DefaultPlaybackBlocks = PlaybackBlocks(("v0", "validation"), ("v1", "validation"), ("o0", RlReplayDefaults.OosPartition));

    private sealed class PlaybackData(IRlModelGenerationRegistry registry) : RlDialogDataService(registry, new UnavailableQ())
    {
        public Func<string, CancellationToken, Task<ImmutableArray<RlEpisodeBlock>>> Blocks { get; set; } = (_, _) => Task.FromResult(DefaultPlaybackBlocks);
        public Func<CancellationToken, Task<RlQSnapshot>> Replay { get; set; } = _ => Task.FromResult(Snapshot(0, 1, -1));
        public List<string> BlockLoads { get; } = new();
        public List<(string Model, string Block, int Decision, string Policy)> Replays { get; } = new();
        public override async Task<RlDialogBlocks> LoadBlocksAsync(string modelId, CancellationToken ct)
        {
            lock (BlockLoads) BlockLoads.Add(modelId);
            return new(await Blocks(modelId, ct), null, null);
        }
        public override Task<RlQSnapshot> ReplayAsync(string modelId, string blockId, int decision, string policyId, CancellationToken ct)
        {
            lock (Replays) Replays.Add((modelId, blockId, decision, policyId));
            return Replay(ct);
        }
        public Func<string, CancellationToken, Task<RlSourceInfo>> Source { get; set; } = (model, _) => Task.FromResult(
            new RlSourceInfo("SPY", new DateOnly(2010, 1, 1), new DateOnly(2020, 1, 1), new DateOnly(2010, 1, 4), new DateOnly(2019, 12, 31)));
        public Func<string, string, CancellationToken, Task<RlAnchorRange>> Range { get; set; } = (_, _, _) => Task.FromResult(
            new RlAnchorRange(new DateOnly(2015, 1, 1), new DateOnly(2019, 6, 3)));
        public Func<RlExternalReplayRequest, CancellationToken, Task<RlExternalReplayResult>> External { get; set; } = (request, _) => Task.FromResult(
            new RlExternalReplayResult(Snapshot(0, 1, -1), request.AnchorDate, request.AnchorDate));
        public List<(string Model, string Symbol)> RangeReads { get; } = new();
        public List<RlExternalReplayRequest> ExternalReplays { get; } = new();
        public override Task<RlSourceInfo> LoadSourceInfoAsync(string modelId, CancellationToken ct) => Source(modelId, ct);
        public override Task<RlAnchorRange> GetAnchorRangeAsync(string modelId, string symbol, CancellationToken ct)
        {
            lock (RangeReads) RangeReads.Add((modelId, symbol));
            return Range(modelId, symbol, ct);
        }
        public override Task<RlExternalReplayResult> ReplayExternalAsync(RlExternalReplayRequest request, CancellationToken ct)
        {
            lock (ExternalReplays) ExternalReplays.Add(request);
            return External(request, ct);
        }
    }

    private sealed class RecordingReplayTarget : IRlReplayTarget
    {
        public List<RlQSnapshot> Applied { get; } = new();
        public int ClearCalls { get; private set; }
        public Exception? ApplyError { get; set; }
        public Task ApplyReplayAsync(RlQSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (ApplyError is { } error) return Task.FromException(error);
            Applied.Add(snapshot);
            return Task.CompletedTask;
        }
        public void Clear() => ClearCalls++;
    }

    private sealed class PrefixLocalization : ILocalizationService
    {
        public string Prefix { get; set; } = "";
        public string GetString(string key) => Prefix + key;
    }

    private sealed record PlaybackFixture(RlPlaybackSettingsViewModel Vm, PlaybackData Data, RecordingReplayTarget Target, EmptyRegistry Registry, int[] CloseCount);

    private static async Task<PlaybackFixture> Playback(IReadOnlyList<RlGeneration>? generations = null, Action<PlaybackData>? configure = null,
        ILocalizationService? localization = null, Exception? listError = null)
    {
        var registry = new EmptyRegistry { Generations = generations ?? new[] { PlaybackGeneration("a") }, ListError = listError };
        var data = new PlaybackData(registry); configure?.Invoke(data);
        var target = new RecordingReplayTarget();
        var vm = new RlPlaybackSettingsViewModel(registry, data, target, localization ?? NullLocalizationService.Instance, new SynchronousDispatcherService());
        var closed = new int[1]; vm.CloseRequested += () => closed[0]++;
        await vm.RefreshCommand.ExecuteAsync(null);
        await PlaybackUntil(() => vm.ModelsState != RlDialogLoadState.Loading && vm.BlocksState != RlDialogLoadState.Loading);
        return new(vm, data, target, registry, closed);
    }

    private static async Task PlaybackUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "The condition did not become true in time.");
    }

    [Fact]
    public async Task Playback_ListsNewestFirstWithOrdinalIdTieBreakAndSelectsTheNewest()
    {
        var older = PlaybackGeneration("older", ageDays: 5); var newestB = PlaybackGeneration("b"); var newestA = PlaybackGeneration("a");
        var f = await Playback(new[] { older, newestB, newestA }); using var vm = f.Vm;
        Assert.Equal(new[] { "a", "b", "older" }, vm.ModelOptions.Select(o => o.Generation.ModelId));
        Assert.Equal("a", vm.SelectedModelOption!.Generation.ModelId);
        Assert.Contains("a", vm.ModelOptions[0].Name);
        Assert.Equal(RlDialogLoadState.Loaded, vm.ModelsState); Assert.True(vm.HasModels); Assert.False(vm.ShowEmpty);
    }

    [Fact]
    public async Task Playback_EmptyRegistryIsLoadedWithoutSelectionAndCannotShow()
    {
        var f = await Playback(Array.Empty<RlGeneration>()); using var vm = f.Vm;
        Assert.Equal(RlDialogLoadState.Loaded, vm.ModelsState); Assert.True(vm.ShowEmpty); Assert.False(vm.HasModels);
        Assert.Null(vm.SelectedModelOption); Assert.Equal(RlDialogLoadState.Unloaded, vm.BlocksState); Assert.False(vm.CanShowOnChart);
        Assert.Empty(f.Data.BlockLoads);
    }

    [Fact]
    public async Task Playback_UnreadableRegistryFailsWithMessageAndNoOptions()
    {
        var f = await Playback(listError: new IOException("private path")); using var vm = f.Vm;
        Assert.Equal(RlDialogLoadState.Failed, vm.ModelsState); Assert.Empty(vm.ModelOptions); Assert.Null(vm.SelectedModelOption);
        Assert.Equal("Rl.Ui.Status.IOFailure", vm.StatusMessage); Assert.False(vm.CanShowOnChart); Assert.True(vm.ShowModelsCaption);
    }

    [Fact]
    public async Task Playback_SelectionDefaultsAreTargetPolicyDecisionZeroAndFirstOutOfSampleBlock()
    {
        var f = await Playback(new[] { PlaybackGeneration("a") }); using var vm = f.Vm;
        Assert.Equal(RlFiles.Target, vm.SelectedPolicyOption!.Id); Assert.Equal(0, vm.Decision);
        Assert.Equal("o0", vm.SelectedBlockOption!.Block.Id); Assert.Equal(DefaultPlaybackBlocks.Length, vm.BlockOptions.Count);
        Assert.Equal(new[] { RlFiles.Target, RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.SmaBaselineId, RlContract.RandomBaselineId }, vm.PolicyOptions.Select(p => p.Id));
        Assert.Equal(RlContract.EpisodeHorizon - 1, vm.MaximumDecision); Assert.True(vm.CanShowOnChart);
    }

    [Fact]
    public async Task Playback_ContinuousAgentOffersThePairedBaselineAndSelectsItsOwnTarget()
    {
        var f = await Playback(new[] { PlaybackGeneration("c", target: RlContinuousContract.Target) }); using var vm = f.Vm;
        Assert.Equal(RlContinuousContract.Target, vm.SelectedPolicyOption!.Id);
        Assert.Equal(RlContinuousContract.PairedBaselineId, vm.PolicyOptions[^1].Id);
    }

    [Fact]
    public async Task Playback_ChangingTheAgentResetsInputsAndLoadsItsBlocks()
    {
        var f = await Playback(new[] { PlaybackGeneration("a"), PlaybackGeneration("b", ageDays: 1, target: RlContinuousContract.Target) }); using var vm = f.Vm;
        vm.Decision = 7; vm.SelectedBlockOption = vm.BlockOptions.First();
        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Generation.ModelId == "b");
        await PlaybackUntil(() => vm.BlocksState == RlDialogLoadState.Loaded);
        Assert.Equal(0, vm.Decision); Assert.Equal("o0", vm.SelectedBlockOption!.Block.Id);
        Assert.Equal(RlContinuousContract.Target, vm.SelectedPolicyOption!.Id); Assert.Equal(new[] { "a", "b" }, f.Data.BlockLoads);
    }

    [Fact]
    public async Task Playback_AStaleBlockLoadOfAnEarlierAgentIsDropped()
    {
        var staleGate = new TaskCompletionSource<ImmutableArray<RlEpisodeBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = await PlaybackWithPendingFirst(staleGate); using var vm = f.Vm;
        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Generation.ModelId == "b");
        await PlaybackUntil(() => vm.BlocksState == RlDialogLoadState.Loaded);
        staleGate.SetResult(PlaybackBlocks(("stale", "validation")));
        await Task.Delay(100);
        Assert.DoesNotContain(vm.BlockOptions, o => o.Block.Id == "stale"); Assert.Equal("o0", vm.SelectedBlockOption!.Block.Id);
    }

    private static async Task<PlaybackFixture> PlaybackWithPendingFirst(TaskCompletionSource<ImmutableArray<RlEpisodeBlock>> gate)
    {
        var registry = new EmptyRegistry { Generations = new[] { PlaybackGeneration("a"), PlaybackGeneration("b", ageDays: 1) } };
        var data = new PlaybackData(registry) { Blocks = (id, _) => id == "a" ? gate.Task : Task.FromResult(DefaultPlaybackBlocks) };
        var target = new RecordingReplayTarget();
        var vm = new RlPlaybackSettingsViewModel(registry, data, target, NullLocalizationService.Instance, new SynchronousDispatcherService());
        var closed = new int[1]; vm.CloseRequested += () => closed[0]++;
        await vm.RefreshCommand.ExecuteAsync(null);
        await PlaybackUntil(() => vm.ModelsState == RlDialogLoadState.Loaded);
        return new(vm, data, target, registry, closed);
    }

    [Fact]
    public async Task Playback_FailedBlockLoadShowsTheMessageAndBlocksShowing()
    {
        var f = await Playback(configure: d => d.Blocks = (_, _) => throw new FileNotFoundException("private path")); using var vm = f.Vm;
        Assert.Equal(RlDialogLoadState.Failed, vm.BlocksState); Assert.Equal("Rl.Ui.Status.DataUnavailable", vm.StatusMessage); Assert.False(vm.CanShowOnChart);
    }

    [Theory]
    [InlineData("model")] [InlineData("block")] [InlineData("policy")] [InlineData("foreignPolicy")]
    [InlineData("decisionLow")] [InlineData("decisionHigh")] [InlineData("invalidEditor")]
    public async Task Playback_CanShowOnChartIsFalseWhenAnyConjunctFails(string broken)
    {
        var f = await Playback(); using var vm = f.Vm;
        Assert.True(vm.CanShowOnChart);
        switch (broken)
        {
            case "model": vm.SelectedModelOption = null; break;
            case "block": vm.SelectedBlockOption = null; break;
            case "policy": vm.SelectedPolicyOption = null; break;
            case "foreignPolicy": vm.SelectedPolicyOption = new RlPolicyOption("not.in.the.list", "x"); break;
            case "decisionLow": vm.Decision = -1; break;
            case "decisionHigh": vm.Decision = vm.MaximumDecision + 1; break;
            case "invalidEditor": vm.SetEditorValidity(nameof(vm.Decision), false); break;
        }
        Assert.False(vm.CanShowOnChart);
        if (broken == "invalidEditor") { vm.SetEditorValidity(nameof(vm.Decision), true); Assert.True(vm.CanShowOnChart); }
    }

    [Fact]
    public async Task Playback_ShowOnChartAppliesTheReplayOnceThenRequestsCloseOnce()
    {
        var f = await Playback(); using var vm = f.Vm;
        vm.Decision = 3; vm.SelectedPolicyOption = vm.PolicyOptions.Single(p => p.Id == RlContract.HoldBaselineId);
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Equal(new[] { ("a", "o0", 3, RlContract.HoldBaselineId) }, f.Data.Replays);
        Assert.Single(f.Target.Applied); Assert.Equal(1, f.CloseCount[0]);
        Assert.Equal("Settings_RlPlayback_Status_Shown", vm.StatusMessage); Assert.Equal(RlDialogOperation.Idle, vm.Operation);
    }

    [Fact]
    public async Task Playback_FailedReplayLeavesTheChartAndTheWindowAlone()
    {
        var f = await Playback(configure: d => d.Replay = _ => throw new InvalidDataException("InsufficientData: short")); using var vm = f.Vm;
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Empty(f.Target.Applied); Assert.Equal(0, f.CloseCount[0]); Assert.Equal(0, f.Target.ClearCalls);
        Assert.Equal("Rl.Ui.Status.InsufficientData", vm.StatusMessage); Assert.Equal(RlDialogOperation.Idle, vm.Operation); Assert.True(vm.CanShowOnChart);
    }

    [Fact]
    public async Task Playback_FailureInsideTheChartApplyDoesNotClose()
    {
        var f = await Playback(); using var vm = f.Vm;
        f.Target.ApplyError = new ArgumentException("bad snapshot");
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Equal(0, f.CloseCount[0]); Assert.Equal("Rl.Ui.Status.InvalidInput", vm.StatusMessage);
    }

    [Fact]
    public async Task Playback_CancelledReplayReportsCancellationWithoutApplyingOrClosing()
    {
        var f = await Playback(configure: d => d.Replay = _ => throw new OperationCanceledException()); using var vm = f.Vm;
        await vm.ShowOnChartCommand.ExecuteAsync(null);
        Assert.Empty(f.Target.Applied); Assert.Equal(0, f.CloseCount[0]);
        Assert.Equal("Settings_RlPlayback_Status_Cancelled", vm.StatusMessage); Assert.Equal(RlDialogOperation.Idle, vm.Operation);
    }

    [Fact]
    public async Task Playback_WhileReplayingTheCommandIsBusyAndDisabled()
    {
        var gate = new TaskCompletionSource<RlQSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = await Playback(configure: d => d.Replay = _ => gate.Task); using var vm = f.Vm;
        var running = vm.ShowOnChartCommand.ExecuteAsync(null);
        await PlaybackUntil(() => f.Data.Replays.Count == 1);
        Assert.True(vm.IsBusy); Assert.False(vm.CanShowOnChart); Assert.False(vm.ShowOnChartCommand.CanExecute(null));
        gate.SetResult(Snapshot(0, 1, -1)); await running;
        Assert.False(vm.IsBusy); Assert.Single(f.Target.Applied);
    }

    [Fact]
    public async Task Playback_ClearChartRemovesTheReplayAndNeverCloses()
    {
        var f = await Playback(); using var vm = f.Vm;
        vm.ClearChartCommand.Execute(null);
        Assert.Equal(1, f.Target.ClearCalls); Assert.Equal(0, f.CloseCount[0]); Assert.Equal("Settings_RlPlayback_Status_Cleared", vm.StatusMessage);
    }

    [Fact]
    public async Task Playback_DisposeCancelsARunningReplayAndLaterCompletionPublishesNothing()
    {
        var gate = new TaskCompletionSource<RlQSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = await Playback(configure: d => d.Replay = _ => gate.Task); var vm = f.Vm;
        var running = vm.ShowOnChartCommand.ExecuteAsync(null);
        await PlaybackUntil(() => f.Data.Replays.Count == 1);
        vm.Dispose();
        gate.SetResult(Snapshot(0, 1, -1)); await running;
        Assert.Empty(f.Target.Applied); Assert.Equal(0, f.CloseCount[0]); Assert.Equal(RlDialogOperation.Disposed, vm.Operation); Assert.Equal("", vm.StatusMessage);
    }

    [Fact]
    public async Task Playback_DisposeDropsALateBlockLoadAndRaisesNoEvents()
    {
        var gate = new TaskCompletionSource<ImmutableArray<RlEpisodeBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = await PlaybackWithPendingFirst(gate); var vm = f.Vm;
        vm.Dispose(); gate.SetResult(DefaultPlaybackBlocks); await Task.Delay(100);
        Assert.Empty(vm.BlockOptions); Assert.Equal(0, f.CloseCount[0]);
    }

    [Fact]
    public async Task Playback_RefreshKeepsTheSelectedAgentAndItsInputsWhenStillListed()
    {
        var f = await Playback(new[] { PlaybackGeneration("a"), PlaybackGeneration("b", ageDays: 1) }); using var vm = f.Vm;
        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Generation.ModelId == "b");
        await PlaybackUntil(() => vm.BlocksState == RlDialogLoadState.Loaded); vm.Decision = 9;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("b", vm.SelectedModelOption!.Generation.ModelId); Assert.Equal(9, vm.Decision);
    }

    [Fact]
    public async Task Playback_LanguageChangeRebuildsTextsKeepsSelectionsAndReloadsNothing()
    {
        var text = new PrefixLocalization { Prefix = "one:" };
        var f = await Playback(localization: text); using var vm = f.Vm;
        vm.SelectedBlockOption = vm.BlockOptions.First(o => o.Block.Id == "v1"); vm.Decision = 4;
        int loads = f.Data.BlockLoads.Count;
        text.Prefix = "two:";
        StockAnalyzer.Avalonia.Services.LocalizationManager.Instance.Initialize(StockAnalyzer.Avalonia.Services.LocalizationManager.Instance.CurrentLanguage);
        Assert.StartsWith("two:", vm.ModelOptions[0].Name); Assert.StartsWith("two:", vm.BlockOptions[0].Name); Assert.StartsWith("two:", vm.PolicyOptions[0].Name);
        Assert.Equal("v1", vm.SelectedBlockOption!.Block.Id); Assert.Equal("a", vm.SelectedModelOption!.Generation.ModelId);
        Assert.Equal(RlFiles.Target, vm.SelectedPolicyOption!.Id); Assert.Equal(4, vm.Decision); Assert.Equal(loads, f.Data.BlockLoads.Count);
    }

    [Fact]
    public async Task Playback_HoldsNoEditableStateAndResetSelectsTheNewestWithoutTouchingTheChart()
    {
        var f = await Playback(new[] { PlaybackGeneration("a"), PlaybackGeneration("b", ageDays: 1) }); using var vm = f.Vm;
        vm.SelectedModelOption = vm.ModelOptions.Single(o => o.Generation.ModelId == "b"); vm.Decision = 5;
        Assert.False(vm.IsModified); await vm.SaveChangesAsync(); vm.RevertChanges(); Assert.False(vm.IsModified);
        ((ISettingsPageViewModel)vm).ResetToDefault();
        await PlaybackUntil(() => vm.BlocksState == RlDialogLoadState.Loaded);
        Assert.Equal("a", vm.SelectedModelOption!.Generation.ModelId); Assert.Equal(0, vm.Decision);
        Assert.Equal(0, f.Target.ClearCalls); Assert.Empty(f.Target.Applied);
        Assert.Equal("Settings_RlPlayback", vm.TitleKey);
    }
}
