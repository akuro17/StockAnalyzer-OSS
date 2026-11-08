using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("MessengerSharedState")]
public sealed class RlTrainingTempModeTests : IDisposable
{
    private readonly List<RlTrainingDialogViewModel> _viewModels = new();
    private readonly List<ChartViewModel> _charts = new();

    public void Dispose()
    {
        foreach (var vm in _viewModels) vm.Dispose();
        foreach (var chart in _charts) chart.Dispose();
    }

    private sealed class ImmediateData : IDataService
    {
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame frame, int count = 100)
            => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
    }

    private sealed class UnavailableQ : IQValueService
    {
        public Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No generation supplied.");
    }

    private sealed class FakeTrainingService : IRlResumableTrainingService
    {
        public RlTrainingJob? LastJob { get; private set; }
        public int RunCalls { get; private set; }

        public Task<RlTrainingResult> RunAsync(RlTrainingJob job, IProgress<RlTrainingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LastJob = job;
            RunCalls++;
            return Task.FromResult(new RlTrainingResult(Guid.NewGuid(), "dir", "model", "eval", null, true, "rev"));
        }

        public Task<RlTrainingResult> RunResumableAsync(RlTrainingJob job, CancellationToken pauseRequest, IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
            => RunAsync(job, progress, ct);

        public Task<RlTrainingResult> ResumeAsync(RlCheckpointReview review, CancellationToken pauseRequest, IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeRegistry : IRlModelGenerationRegistry
    {
        public int CaptureSnapshotCalls { get; private set; }
        public RlRegistryCapabilities Capabilities { get; init; } = new(false, false, false);

        public RlRegistrySnapshot CaptureRlSnapshot(CancellationToken ct = default)
        {
            CaptureSnapshotCalls++;
            return new(ImmutableArray<RlGeneration>.Empty, null, null, false, false) { Capabilities = Capabilities };
        }

        public IReadOnlyList<RlCheckpointReview> ListRlCheckpoints() => Array.Empty<RlCheckpointReview>();
        public Task AbandonRlResumeAsync(RlCheckpointReview reviewed, CancellationToken ct = default) => Task.CompletedTask;
        public RlArtifactPolicy? RlArtifactPolicy { get; set; }
        public Task SaveRlArtifactPolicyAsync(RlArtifactPolicy policy, CancellationToken ct = default) => Task.CompletedTask;
        public RlArtifactPreview? PreviewRlArtifacts() => null;
        public Task<RlArtifactCleanupResult> CleanupRlArtifactsAsync(RlArtifactPreview reviewed, CancellationToken ct = default)
            => Task.FromResult(new RlArtifactCleanupResult(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, 0));
        public RlPointerRecovery? PreviewRlRecovery(string target) => null;
        public Task<bool> RecoverRlPointerAsync(RlPointerRecovery reviewed, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public IReadOnlyList<RlGeneration> ListRl() => Array.Empty<RlGeneration>();
        public IReadOnlySet<string> RlModelIdsWithoutWorkFolder() => ImmutableHashSet<string>.Empty;
        public Task<RlGenerationRemoval> RemoveRlGenerationAsync(string modelId, CancellationToken ct = default)
            => Task.FromResult(new RlGenerationRemoval(true, ImmutableArray<string>.Empty));
        public string? RlActiveId => null;
        public ModelGenerationLease? AcquireRl(string id) => null;
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string id) => null;
        public Task<bool> ActivateRlAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RlGeneration> RegisterRlAsync(string directory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private ChartViewModel CreateChart()
    {
        var data = new ImmediateData();
        var chart = new ChartViewModel(
            data,
            new DialogService(),
            null!,
            new MockStockAnalyzerSettings(),
            new TimeFrameManager(data),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            new MockChartSettingsManager(),
            new SynchronousDispatcherService(),
            null,
            null!,
            null,
            null,
            null,
            messenger: new StrongReferenceMessenger());
        _charts.Add(chart);
        return chart;
    }

    private (RlTrainingDialogViewModel Vm, FakeTrainingService Training, FakeRegistry Registry) CreateVm()
    {
        var training = new FakeTrainingService();
        var registry = new FakeRegistry();
        var chart = CreateChart();
        var vm = new RlTrainingDialogViewModel(
            training,
            registry,
            new UnavailableQ(),
            new FakeBacktestDefaultSettingsManager(),
            chart,
            NullLocalizationService.Instance);
        _viewModels.Add(vm);
        return (vm, training, registry);
    }

    [Fact]
    public void StorageMode_DefaultsToArtifacts()
    {
        var (vm, _, _) = CreateVm();

        Assert.Equal(OnnxArtifactStorageMode.Artifacts, vm.SelectedStorageMode);
        Assert.True(vm.IsArtifactsStorageMode);
        Assert.False(vm.IsTempStorageMode);
        Assert.Contains(OnnxArtifactStorageMode.Artifacts, vm.AvailableStorageModes);
        Assert.Contains(OnnxArtifactStorageMode.Temp, vm.AvailableStorageModes);
    }

    [Fact]
    public void StorageMode_TwoWayBinding_TogglesModeCorrectly()
    {
        var (vm, _, _) = CreateVm();

        vm.IsTempStorageMode = true;
        Assert.Equal(OnnxArtifactStorageMode.Temp, vm.SelectedStorageMode);
        Assert.False(vm.IsArtifactsStorageMode);
        Assert.True(vm.IsTempStorageMode);

        vm.IsArtifactsStorageMode = true;
        Assert.Equal(OnnxArtifactStorageMode.Artifacts, vm.SelectedStorageMode);
        Assert.True(vm.IsArtifactsStorageMode);
        Assert.False(vm.IsTempStorageMode);

        vm.SelectedStorageMode = OnnxArtifactStorageMode.Temp;
        Assert.False(vm.IsArtifactsStorageMode);
        Assert.True(vm.IsTempStorageMode);
    }

    [Fact]
    public async Task TrainCommand_InArtifactsMode_PassesArtifactsStorageModeToTraining()
    {
        var (vm, training, registry) = CreateVm();
        await vm.InitializeAsync();
        vm.Symbol = "SPY";
        vm.StartDate = "2020-01-01";
        vm.EndDateExclusive = "2021-01-01";

        int initialSnapshots = registry.CaptureSnapshotCalls;

        await vm.TrainCommand.ExecuteAsync(null);

        Assert.NotNull(training.LastJob);
        Assert.Equal(OnnxArtifactStorageMode.Artifacts, training.LastJob.StorageMode);
        Assert.True(registry.CaptureSnapshotCalls > initialSnapshots, "Generations should be refreshed after persistent training.");
    }

    [Fact]
    public async Task TrainCommand_InTempMode_PassesTempStorageModeToTraining_AndBypassesRefreshGenerations()
    {
        var (vm, training, registry) = CreateVm();
        await vm.InitializeAsync();
        vm.Symbol = "SPY";
        vm.StartDate = "2020-01-01";
        vm.EndDateExclusive = "2021-01-01";

        vm.IsTempStorageMode = true;
        int initialSnapshots = registry.CaptureSnapshotCalls;

        await vm.TrainCommand.ExecuteAsync(null);

        Assert.NotNull(training.LastJob);
        Assert.Equal(OnnxArtifactStorageMode.Temp, training.LastJob.StorageMode);
        Assert.Equal(initialSnapshots, registry.CaptureSnapshotCalls);
    }

    [Fact]
    public void RlTrainingValidation_Freeze_InvalidStorageMode_ThrowsArgumentOutOfRangeException()
    {
        var job = new RlTrainingJob(
            "SPY",
            new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1),
            new BacktestConfiguration())
        {
            StorageMode = (OnnxArtifactStorageMode)999
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => RlTrainingValidation.Freeze(job));
    }

    [Fact]
    public void RlTrainingJob_Serialization_OmitsStorageModeFromJson()
    {
        var job = new RlTrainingJob(
            "SPY",
            new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1),
            new BacktestConfiguration())
        {
            StorageMode = OnnxArtifactStorageMode.Temp
        };

        string json = JsonSerializer.Serialize(job);
        Assert.DoesNotContain("StorageMode", json);
        Assert.DoesNotContain("storage_mode", json);
    }

    [Fact]
    public async Task RlTrainingService_RunResumableAsync_TempMode_ThrowsInvalidOperationException()
    {
        var service = new RlTrainingService(new FakeRegistry(), new TrainingJobGate());
        var job = new RlTrainingJob(
            "SPY",
            new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1),
            new BacktestConfiguration())
        {
            StorageMode = OnnxArtifactStorageMode.Temp
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunResumableAsync(job, CancellationToken.None));

        Assert.Contains("strict continuation requires persistent T16 storage", ex.Message);
    }
}
