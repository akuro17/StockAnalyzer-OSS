#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed partial class RlGenerationAndQValueTests
{
    [Fact]
    public async Task U01_CoherentSnapshotReportsPointerFailureWithoutFallbackOrOwnerMutation()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(Candidate().Directory);
        var initial = registry.CaptureRlSnapshot();
        Assert.Equal(registry.Capabilities, initial.Capabilities);
        Assert.Single(initial.Generations); Assert.Null(initial.DirectionActiveId); Assert.False(initial.DirectionUnavailable);
        Assert.True(await registry.ActivateRlAsync(generation.ModelId));
        var active = registry.CaptureRlSnapshot();
        Assert.Equal(generation.ModelId, active.DirectionActiveId);
        Assert.Contains(active.Generations, g => g.ModelId == active.DirectionActiveId);
        active.Generations[0].Files.Clear(); Assert.NotEmpty(registry.CaptureRlSnapshot().Generations[0].Files);
        string pointer = Path.Combine(RegistryRoot, RlFiles.RlFolder, RlFiles.Active);
        File.WriteAllText(pointer, "{");
        var failed = registry.CaptureRlSnapshot();
        Assert.Null(failed.DirectionActiveId); Assert.True(failed.DirectionUnavailable);
        Assert.False(failed.ContinuousUnavailable); Assert.Single(failed.Generations); Assert.Equal("{", File.ReadAllText(pointer));
    }
    [Fact]
    public async Task U01_BackgroundSnapshotWaitingForCompetingStoreLockHonorsCancellation()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        Assert.Empty(registry.CaptureRlSnapshot().Generations);
        string lockPath = Path.Combine(RegistryRoot, ModelGenerationRegistry.Coordination.Folder, ModelGenerationRegistry.Coordination.LockFile);
        using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancel = new CancellationTokenSource();
        var snapshot = Task.Run(() => registry.CaptureRlSnapshot(cancel.Token));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task U01_ReplayLeaseCoversAwaitAndReleasesOnFailureAndCancellation(bool cancelRequest)
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        registry.ConfigureRlArtifacts(_root, new RlArtifactSettings(Path.Combine(_root, "config", RlArtifactSettings.FileName)));
        var generation = await registry.RegisterRlAsync(Candidate().Directory);
        var replay = new DialogReplayProbe(); var data = new RlDialogDataService(registry, replay);
        using var cancel = new CancellationTokenSource();
        var work = data.ReplayAsync(generation.ModelId, "explicit-block", 0, RlContract.HoldBaselineId, cancel.Token);
        await replay.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Lease", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == generation.ModelId).Reasons);
        if (cancelRequest) cancel.Cancel(); else replay.Result.TrySetException(new InvalidDataException("owned failure"));
        if (cancelRequest) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else await Assert.ThrowsAsync<InvalidDataException>(() => work);
        Assert.DoesNotContain("Lease", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == generation.ModelId).Reasons);
        Assert.Equal(generation.ModelId, replay.Request!.ModelId);
        Assert.Equal("explicit-block", replay.Request.BlockId);
        Assert.Equal(RlContract.HoldBaselineId, replay.Request.PolicyId);
    }
    [Fact]
    public async Task U01_CancelWhileAcquisitionWaitsForStoreLockReleasesLeaseAndNeverStartsReplay()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        registry.ConfigureRlArtifacts(_root, new RlArtifactSettings(Path.Combine(_root, "config", RlArtifactSettings.FileName)));
        var generation = await registry.RegisterRlAsync(Candidate().Directory);
        string lockPath = Path.Combine(RegistryRoot, ModelGenerationRegistry.Coordination.Folder, ModelGenerationRegistry.Coordination.LockFile);
        var replay = new DialogReplayProbe(); var data = new RlDialogDataService(registry, replay);
        using var cancel = new CancellationTokenSource();
        var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Task work;
        try
        {
            work = data.ReplayAsync(generation.ModelId, "explicit-block", 0, RlContract.HoldBaselineId, cancel.Token);
            cancel.Cancel();
        }
        finally { held.Dispose(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.False(replay.Started.Task.IsCompleted);
        Assert.DoesNotContain("Lease", registry.PreviewRlArtifacts()!.Entries.Single(e => e.Id == generation.ModelId).Reasons);
    }
    private sealed class DialogReplayProbe : IQValueService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RlQSnapshot> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RlReplayRequest? Request { get; private set; }
        public Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default)
        { Request = request; Started.TrySetResult(); return Result.Task.WaitAsync(cancellationToken); }
    }
}
