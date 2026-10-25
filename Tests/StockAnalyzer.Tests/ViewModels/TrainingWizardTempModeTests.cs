using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public sealed class TrainingWizardTempModeTests
{
    private sealed class MockModelDeploymentService : IModelDeploymentService
    {
        public bool PromoteCalled { get; private set; }
        public TrainingRunResult? LastPromotedInput { get; private set; }
        public string? TargetDirUsed { get; private set; }

        public Task<string> DeployAsync(string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default)
            => Task.FromResult("deployed");

        public Task<ModelGenerationManifest> RegisterGenerationAsync(string onnxSourcePath, string? metricsSourcePath = null, CancellationToken ct = default)
        {
            var manifest = new ModelGenerationManifest(
                SchemaVersion: 1,
                ModelId: "gen-test-1",
                ModelFile: Path.GetFileName(onnxSourcePath),
                ModelHash: "dummyhash",
                RequiredSidecars: Array.Empty<ModelSidecar>(),
                ContractHash: "dummycontract",
                EvaluationRevision: "v1",
                TargetType: "Binary",
                Timeframe: "Daily",
                Horizon: 1,
                ClassOrder: "Up,Down",
                Score: 0.75);
            return Task.FromResult(manifest);
        }

        public Task<TrainingRunResult> PromoteTempRunToTrainingArtifactsAsync(
            TrainingRunResult tempResult,
            string? targetArtifactsDirectory = null,
            CancellationToken cancellationToken = default)
        {
            PromoteCalled = true;
            LastPromotedInput = tempResult;
            TargetDirUsed = targetArtifactsDirectory;

            var promotedDir = targetArtifactsDirectory ?? PathDiscovery.ResolveTrainingWizardArtifactsDirectory();
            var promotedOnnx = Path.Combine(promotedDir, Path.GetFileName(tempResult.OnnxArtifactPath!));
            var promotedMetrics = tempResult.MetricsArtifactPath is not null
                ? Path.Combine(promotedDir, Path.GetFileName(tempResult.MetricsArtifactPath))
                : null;

            return Task.FromResult(tempResult with
            {
                OnnxArtifactPath = promotedOnnx,
                MetricsArtifactPath = promotedMetrics,
            });
        }
    }

    private sealed class MockExperimentLogService : IExperimentLogService
    {
        public List<TrainingJobConfig> RecordedConfigs { get; } = new();
        public List<TrainingRunResult> RecordedResults { get; } = new();

        public Task RecordAsync(TrainingJobConfig config, TrainingRunResult result, CancellationToken ct = default)
        {
            RecordedConfigs.Add(config);
            RecordedResults.Add(result);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void StorageMode_DefaultsToArtifacts()
    {
        var vm = new TrainingWizardViewModel();
        Assert.Equal(OnnxArtifactStorageMode.Artifacts, vm.SelectedStorageMode);
        Assert.Contains(OnnxArtifactStorageMode.Temp, vm.AvailableStorageModes);
        Assert.Contains(OnnxArtifactStorageMode.Artifacts, vm.AvailableStorageModes);
    }

    [Fact]
    public void CreateCurrentTrainingConfig_TempMode_SetsArtifactsDirectoryAndRunId()
    {
        var vm = new TrainingWizardViewModel();
        vm.SelectedStorageMode = OnnxArtifactStorageMode.Temp;

        var tempRunId = TrainingRunId.New(DateTimeOffset.UtcNow);
        var tempJobDir = Path.Combine(EphemeralSandbox.Root, "test-session", tempRunId);

        var config = vm.CreateCurrentTrainingConfig(tempJobDir, tempRunId);
        Assert.NotNull(config);
        Assert.Equal(tempJobDir, config.ArtifactsDirectory);
        Assert.Equal(tempRunId, config.RunId);
        Assert.Equal(EphemeralTrainingRequest.OutputStem, config.OutputName);
    }

    [Fact]
    public void CreateCurrentTrainingConfig_ArtifactsMode_LeavesArtifactsDirectoryNull()
    {
        var vm = new TrainingWizardViewModel();
        vm.SelectedStorageMode = OnnxArtifactStorageMode.Artifacts;

        var config = vm.CreateCurrentTrainingConfig(null, null);
        Assert.NotNull(config);
        Assert.Null(config.ArtifactsDirectory);
        Assert.Null(config.RunId);
        Assert.Null(config.OutputName);
    }

    [Fact]
    public async Task DeployToModels_WhenTempMode_PromotesAndRecordsDeferredExperimentLog()
    {
        var deployment = new MockModelDeploymentService();
        var expLog = new MockExperimentLogService();
        var vm = new TrainingWizardViewModel(modelDeploymentService: deployment, experimentLogService: expLog);

        var runId = TrainingRunId.New(DateTimeOffset.UtcNow);
        var tempDir = Path.Combine(EphemeralSandbox.Root, "test-session", runId);
        Directory.CreateDirectory(tempDir);
        var tempOnnx = Path.Combine(tempDir, "model.onnx");
        var tempMetrics = Path.Combine(tempDir, "model.onnx.metrics.json");
        File.WriteAllText(tempOnnx, "dummy onnx");
        File.WriteAllText(tempMetrics, "{}");

        var initialResult = new TrainingRunResult
        {
            RunId = runId,
            Success = true,
            ExitCode = 0,
            OnnxArtifactPath = tempOnnx,
            MetricsArtifactPath = tempMetrics,
        };

        vm.LastResult = initialResult;
        vm.LastRunStorageMode = OnnxArtifactStorageMode.Temp;
        vm.LastRunConfig = new TrainingJobConfig
        {
            RunId = runId,
            Symbols = new[] { "AAPL" },
            Architecture = "mlp",
            WindowSize = 20,
            Horizon = 1,
            ArtifactsDirectory = tempDir,
        };

        Assert.True(vm.DeployToModelsCommand.CanExecute(null));
        await vm.DeployToModelsCommand.ExecuteAsync(null);

        Assert.True(deployment.PromoteCalled);
        Assert.Equal(tempOnnx, deployment.LastPromotedInput?.OnnxArtifactPath);
        Assert.Equal(OnnxArtifactStorageMode.Artifacts, vm.LastRunStorageMode);
        Assert.Single(expLog.RecordedConfigs);
        Assert.Single(expLog.RecordedResults);
        Assert.Equal(runId, expLog.RecordedResults[0].RunId);
        Assert.Null(expLog.RecordedConfigs[0].ArtifactsDirectory);

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void PredictionModelsViewModel_StorageModes_DefaultAndOptions()
    {
        var settingsManager = new PredictionSettingsManager();
        using var vm = new PredictionModelsViewModel(settingsManager);

        Assert.Equal(OnnxArtifactStorageMode.Artifacts, vm.SelectedArtifactSourceMode);
        Assert.Equal(2, vm.AvailableArtifactSourceModes.Count);
        Assert.Contains(OnnxArtifactStorageMode.Artifacts, vm.AvailableArtifactSourceModes);
        Assert.Contains(OnnxArtifactStorageMode.Temp, vm.AvailableArtifactSourceModes);

        vm.SelectedArtifactSourceMode = OnnxArtifactStorageMode.Temp;
        Assert.Equal(OnnxArtifactStorageMode.Temp, vm.SelectedArtifactSourceMode);
    }
}
