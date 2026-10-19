using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>T13 wizard surface: ensemble method, member count, stacking bases, pretraining, and the explicit ensemble deployment.</summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardExtendedLearningTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;
    public TrainingWizardExtendedLearningTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private sealed record Harness(
        TrainingWizardViewModel Wizard, List<TrainingJobConfig> Started,
        Mock<IModelDeploymentService> Deployment, Mock<IEnsembleSettingsManager> Settings);

    private static string Text(string key) => LocalizationManager.Instance[key];

    private static Harness Create(TrainingRunResult? result = null, CommunityToolkit.Mvvm.Messaging.IMessenger? messenger = null)
    {
        var started = new List<TrainingJobConfig>();
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(),
                It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig config, IProgress<TrainingProgress> _, CancellationToken _) =>
            {
                started.Add(config);
                return Task.FromResult(result ?? new TrainingRunResult { RunId = "run", Success = true });
            });
        var log = new Mock<IExperimentLogService>();
        log.Setup(l => l.RecordAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<TrainingRunResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAA" });
        var deployment = new Mock<IModelDeploymentService>();
        var settings = new Mock<IEnsembleSettingsManager>();
        settings.Setup(s => s.SaveAsync(It.IsAny<EnsembleSpec?>())).Returns(Task.CompletedTask);
        var store = StockAnalyzer.Avalonia.Tests.TestHelpers.FakeTickerStateStore.FromProfiles(Array.Empty<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var wizard = new TrainingWizardViewModel(orchestrator: orchestrator.Object, modelDeploymentService: deployment.Object,
            experimentLogService: log.Object, marketDataProvider: market.Object, ensembleSettings: settings.Object, messenger: messenger, tickerStore: store);
        wizard.Horizon = 1;
        return new Harness(wizard, started, deployment, settings);
    }

    [Fact]
    public async Task Defaults_RunASingleModel_AndNothingBlocksStart()
    {
        var h = Create();
        using (h.Wizard)
        {
            Assert.Equal(TrainingEnsembleMethod.None, h.Wizard.SelectedEnsembleMethod);
            Assert.False(h.Wizard.UsePretraining);
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);
            Assert.True(h.Wizard.StartTrainingCommand.CanExecute(null));
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            var config = Assert.Single(h.Started);
            Assert.Equal(TrainingEnsembleMethod.None, config.EnsembleMethod);
            Assert.Equal(TrainingPretraining.None, config.Pretraining);
            Assert.Empty(config.StackingBases);
        }
    }

    [Fact]
    public async Task Bagging_PropagatesMembers_AndRejectsAnOutOfRangeCount()
    {
        var h = Create();
        using (h.Wizard)
        {
            h.Wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Bagging;
            Assert.True(h.Wizard.IsBaggingSelected);
            Assert.False(h.Wizard.IsStackingSelected);
            h.Wizard.EnsembleMembers = 3;
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            var config = Assert.Single(h.Started);
            Assert.Equal(TrainingEnsembleMethod.Bagging, config.EnsembleMethod);
            Assert.Equal(3, config.EnsembleMembers);
            Assert.Empty(config.StackingBases);

            foreach (var invalid in new[] { 1, TrainingResourceOverrides.MaximumEnsembleMembers + 1 })
            {
                h.Wizard.EnsembleMembers = invalid;
                Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null));
                Assert.Contains(TrainingEnsembleContract.MinimumMembers.ToString(), h.Wizard.ExtendedLearningValidationMessage);
                Assert.Contains(TrainingResourceOverrides.MaximumEnsembleMembers.ToString(), h.Wizard.ExtendedLearningValidationMessage);
            }

            h.Wizard.EnsembleMembers = 2;
            Assert.True(h.Wizard.StartTrainingCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task Stacking_ParsesTheBasesList_AndRejectsMalformedOrDuplicateEntries()
    {
        var h = Create();
        using (h.Wizard)
        {
            h.Wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Stacking;
            Assert.True(h.Wizard.IsStackingSelected);
            Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null)); // no bases yet
            Assert.NotNull(h.Wizard.ExtendedLearningValidationMessage);
            foreach (var invalid in new[] { "lightgbm", "pytorch, lightgbm", "pytorch:lstm, pytorch:lstm", "xgboost:a, lightgbm" })
            {
                h.Wizard.StackingBasesText = invalid;
                Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null), invalid);
            }

            h.Wizard.StackingBasesText = " pytorch:lstm , pytorch:cnn,lightgbm ";
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);
            Assert.True(h.Wizard.StartTrainingCommand.CanExecute(null));
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            var config = Assert.Single(h.Started);
            Assert.Equal(TrainingEnsembleMethod.Stacking, config.EnsembleMethod);
            Assert.Equal(new[] { "pytorch:lstm", "pytorch:cnn", "lightgbm" }, config.StackingBases);
        }
    }

    [Fact]
    public async Task StackingWeightSource_ReachesTheJobConfigForStackingOnly()
    {
        var h = Create();
        using (h.Wizard)
        {
            Assert.Equal(new[] { StackingWeightSource.PastOutOfFold, StackingWeightSource.ReleaseMetaSegment },
                TrainingWizardViewModel.AvailableStackingWeightSources);
            h.Wizard.SelectedStackingWeightSource = StackingWeightSource.ReleaseMetaSegment;
            h.Wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Stacking;
            h.Wizard.StackingBasesText = "pytorch:lstm, lightgbm";
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.Equal(StackingWeightSource.ReleaseMetaSegment, h.Started[0].StackingWeightSource);
            h.Started[0].Validate();

            // leaving stacking never leaks the choice into another job (the config validator would reject it)
            h.Wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Bagging;
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.Equal(StackingWeightSource.PastOutOfFold, h.Started[1].StackingWeightSource);
            h.Started[1].Validate();
        }
    }

    [Fact]
    public async Task DeployingAMetaSegmentStackingRun_OmitsTheWeightTransferNote()
    {
        var spec = new EnsembleSpec(new[] { new EnsembleMember("model-a", 0.7), new EnsembleMember("model-b", 0.3) });
        var generations = spec.Members.Select((m, i) => new ModelGenerationManifest(
            ModelGenerationRegistry.SchemaVersion, m.ModelId, $"m{i}.onnx", new string('a', 64),
            Array.Empty<ModelSidecar>(), new string('c', 64), new string('d', 64), "classification", "daily", 5,
            "Up,Down,Neutral", 0.5)).ToArray();
        var h = Create(new TrainingRunResult
        {
            RunId = "run", Success = true, OnnxArtifactPath = @"C:\a\m01.onnx", MetricsArtifactPath = @"C:\a\m01.onnx.metrics.json",
            EnsembleManifestPath = @"C:\a\group.ensemble.json",
        });
        h.Deployment.Setup(d => d.RegisterEnsembleAsync(@"C:\a\group.ensemble.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnsembleRegistration(TrainingEnsembleMethod.Stacking, spec, generations));
        using (h.Wizard)
        {
            h.Wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Stacking;
            h.Wizard.StackingBasesText = "pytorch:lstm, lightgbm";
            h.Wizard.SelectedStackingWeightSource = StackingWeightSource.ReleaseMetaSegment;
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            await h.Wizard.DeployToModelsCommand.ExecuteAsync(null);
            Assert.Equal(string.Format(Text("TrainingWizard_Status_EnsembleRegistered"), 2), h.Wizard.StatusMessage);
            Assert.DoesNotContain(Text("TrainingWizard_Status_EnsembleWeightTransfer"), h.Wizard.StatusMessage);
        }
    }

    [Fact]
    public void EnsembleIsExclusiveWithCalibrationPretrainingRegressionAndTransfer()
    {
        var h = Create();
        using (h.Wizard)
        {
            h.Wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Bagging;
            Assert.True(h.Wizard.StartTrainingCommand.CanExecute(null));

            h.Wizard.UseCalibration = true;
            Assert.Equal(Text("TrainingWizard_Validation_EnsembleExclusive"), h.Wizard.ExtendedLearningValidationMessage);
            Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null));
            h.Wizard.UseCalibration = false;

            h.Wizard.UsePretraining = true;
            Assert.Equal(Text("TrainingWizard_Validation_EnsembleExclusive"), h.Wizard.ExtendedLearningValidationMessage);
            h.Wizard.UsePretraining = false;

            h.Wizard.SelectedTargetType = TargetType.Regression;
            Assert.Equal(Text("TrainingWizard_Validation_EnsembleTarget"), h.Wizard.ExtendedLearningValidationMessage);
            Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null));
            h.Wizard.SelectedTargetType = TargetType.Classification;
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);

            h.Wizard.SelectedInitializationMode = TrainingInitializationMode.Resume;
            Assert.Equal(Text("TrainingWizard_Validation_EnsembleExclusive"), h.Wizard.ExtendedLearningValidationMessage);
            Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task Pretraining_RequiresPyTorch_AndReachesTheJobConfig()
    {
        var h = Create();
        using (h.Wizard)
        {
            h.Wizard.UsePretraining = true;
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.Equal(TrainingPretraining.MaskedReconstruction, Assert.Single(h.Started).Pretraining);

            h.Wizard.SelectedFramework = TrainingFramework.LightGBM;
            Assert.Equal(Text("TrainingWizard_Validation_Pretraining"), h.Wizard.ExtendedLearningValidationMessage);
            Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null));
            h.Wizard.SelectedFramework = TrainingFramework.PyTorch;
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);
            Assert.True(h.Wizard.StartTrainingCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task DeployingAModelAndUsingAnEnsemble_TellTheCharts_ButRegisteringEnsembleMembersDoesNot()
    {
        var messenger = new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger();
        var count = 0;
        var recipient = new object();
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Register<StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage>(
            messenger, recipient, (_, _) => count++);

        // a single model: the registration activates it when it may, so the charts must predict again
        var single = Create(new TrainingRunResult
        {
            RunId = "run", Success = true, OnnxArtifactPath = @"C:\a\m.onnx", MetricsArtifactPath = @"C:\a\m.onnx.metrics.json",
        }, messenger);
        single.Deployment.Setup(d => d.RegisterGenerationAsync(@"C:\a\m.onnx", @"C:\a\m.onnx.metrics.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelGenerationManifest(ModelGenerationRegistry.SchemaVersion, new string('1', 64), "m.onnx", new string('a', 64),
                Array.Empty<ModelSidecar>(), new string('c', 64), new string('d', 64), "classification", "daily", 5, "Up,Down,Neutral", 0.5));
        using (single.Wizard)
        {
            await single.Wizard.StartTrainingCommand.ExecuteAsync(null);
            await single.Wizard.DeployToModelsCommand.ExecuteAsync(null);
        }
        Assert.Equal(1, count);

        // an ensemble: registering the members activates nothing; "use as ensemble" does
        var spec = new EnsembleSpec(new[] { new EnsembleMember("model-a", 0.5), new EnsembleMember("model-b", 0.5) });
        var generations = spec.Members.Select((m, i) => new ModelGenerationManifest(
            ModelGenerationRegistry.SchemaVersion, m.ModelId, $"m{i}.onnx", new string('a', 64),
            Array.Empty<ModelSidecar>(), new string('c', 64), new string('d', 64), "classification", "daily", 5, "Up,Down,Neutral", 0.5)).ToArray();
        var ensemble = Create(new TrainingRunResult
        {
            RunId = "run", Success = true, OnnxArtifactPath = @"C:\a\m01.onnx", MetricsArtifactPath = @"C:\a\m01.onnx.metrics.json",
            EnsembleManifestPath = @"C:\a\group.ensemble.json",
        }, messenger);
        ensemble.Deployment.Setup(d => d.RegisterEnsembleAsync(@"C:\a\group.ensemble.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnsembleRegistration(TrainingEnsembleMethod.Bagging, spec, generations));
        using (ensemble.Wizard)
        {
            await ensemble.Wizard.StartTrainingCommand.ExecuteAsync(null);
            await ensemble.Wizard.DeployToModelsCommand.ExecuteAsync(null);
            Assert.Equal(1, count);
            await ensemble.Wizard.UseAsEnsembleCommand.ExecuteAsync(null);
            Assert.Equal(2, count);
        }
        GC.KeepAlive(recipient);
    }

    [Fact]
    public async Task AttentionArchitecture_IsOfferedForPyTorchOnly_ReachesTheJobConfig_AndExcludesPretraining()
    {
        var h = Create();
        using (h.Wizard)
        {
            Assert.Contains(AttentionContract.Architecture, h.Wizard.AvailableArchitectures);
            h.Wizard.SelectedFramework = TrainingFramework.TensorFlow;
            Assert.DoesNotContain(AttentionContract.Architecture, h.Wizard.AvailableArchitectures);
            h.Wizard.SelectedFramework = TrainingFramework.PyTorch;

            h.Wizard.SelectedArchitecture = AttentionContract.Architecture;
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);
            h.Wizard.Hyperparameters.Add(new TrainingHyperparameterEntry { Key = "heads", Value = "2" });
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            var started = Assert.Single(h.Started);
            Assert.Equal(AttentionContract.Architecture, started.Architecture);
            Assert.Equal("2", started.Hyperparameters["heads"]);
            started.Validate();

            // the trainer refuses --pretrain for this architecture: Start is disabled with a message instead of a launch failure
            h.Wizard.UsePretraining = true;
            Assert.Equal(string.Format(Text("TrainingWizard_Validation_PretrainingAttention"), AttentionContract.Architecture),
                h.Wizard.ExtendedLearningValidationMessage);
            Assert.False(h.Wizard.StartTrainingCommand.CanExecute(null));
            h.Wizard.SelectedArchitecture = "lstm";
            Assert.Null(h.Wizard.ExtendedLearningValidationMessage);
            Assert.True(h.Wizard.StartTrainingCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task DeployingAnEnsembleRun_RegistersTheMembers_AndNeverChangesTheActiveEnsembleUntilAsked()
    {
        var spec = new EnsembleSpec(new[] { new EnsembleMember("model-a", 0.5), new EnsembleMember("model-b", 0.5) });
        var generations = spec.Members.Select((m, i) => new ModelGenerationManifest(
            ModelGenerationRegistry.SchemaVersion, m.ModelId, $"m{i}.onnx", new string('a', 64),
            Array.Empty<ModelSidecar>(), new string('c', 64), new string('d', 64), "classification", "daily", 5,
            "Up,Down,Neutral", 0.5)).ToArray();
        var h = Create(new TrainingRunResult
        {
            RunId = "run", Success = true, OnnxArtifactPath = @"C:\a\m01.onnx", MetricsArtifactPath = @"C:\a\m01.onnx.metrics.json",
            EnsembleManifestPath = @"C:\a\group.ensemble.json",
        });
        h.Deployment.Setup(d => d.RegisterEnsembleAsync(@"C:\a\group.ensemble.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnsembleRegistration(TrainingEnsembleMethod.Bagging, spec, generations));
        using (h.Wizard)
        {
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.False(h.Wizard.HasRegisteredEnsemble);
            Assert.False(h.Wizard.UseAsEnsembleCommand.CanExecute(null));

            await h.Wizard.DeployToModelsCommand.ExecuteAsync(null);
            Assert.True(h.Wizard.HasRegisteredEnsemble);
            Assert.Same(spec, h.Wizard.RegisteredEnsemble!.Spec);
            Assert.Equal(string.Format(Text("TrainingWizard_Status_EnsembleRegistered"), 2), h.Wizard.StatusMessage);
            h.Deployment.Verify(d => d.RegisterGenerationAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
            h.Settings.Verify(s => s.SaveAsync(It.IsAny<EnsembleSpec?>()), Times.Never);

            Assert.True(h.Wizard.UseAsEnsembleCommand.CanExecute(null));
            await h.Wizard.UseAsEnsembleCommand.ExecuteAsync(null);
            h.Settings.Verify(s => s.SaveAsync(spec), Times.Once);
            Assert.Equal(string.Format(Text("TrainingWizard_Status_EnsembleApplied"), 2), h.Wizard.StatusMessage);

            // a new run discards the pending registration
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.False(h.Wizard.HasRegisteredEnsemble);
        }
    }

    [Fact]
    public async Task DeployingAStackingRun_AddsTheWeightTransferNoteAfterTheRegistrationStatus()
    {
        var spec = new EnsembleSpec(new[] { new EnsembleMember("model-a", 0.7), new EnsembleMember("model-b", 0.3) });
        var generations = spec.Members.Select((m, i) => new ModelGenerationManifest(
            ModelGenerationRegistry.SchemaVersion, m.ModelId, $"m{i}.onnx", new string('a', 64),
            Array.Empty<ModelSidecar>(), new string('c', 64), new string('d', 64), "classification", "daily", 5,
            "Up,Down,Neutral", 0.5)).ToArray();
        var h = Create(new TrainingRunResult
        {
            RunId = "run", Success = true, OnnxArtifactPath = @"C:\a\m01.onnx", MetricsArtifactPath = @"C:\a\m01.onnx.metrics.json",
            EnsembleManifestPath = @"C:\a\group.ensemble.json",
        });
        h.Deployment.Setup(d => d.RegisterEnsembleAsync(@"C:\a\group.ensemble.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnsembleRegistration(TrainingEnsembleMethod.Stacking, spec, generations));
        using (h.Wizard)
        {
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            await h.Wizard.DeployToModelsCommand.ExecuteAsync(null);

            var lines = h.Wizard.StatusMessage!.Split(Environment.NewLine);
            Assert.Equal(2, lines.Length);
            Assert.Equal(string.Format(Text("TrainingWizard_Status_EnsembleRegistered"), 2), lines[0]);
            Assert.Equal(Text("TrainingWizard_Status_EnsembleWeightTransfer"), lines[1]);
        }
    }

    [Fact]
    public async Task DeployingASingleModelRun_KeepsTheLegacyRegistration()
    {
        var h = Create(new TrainingRunResult
        {
            RunId = "run", Success = true, OnnxArtifactPath = @"C:\a\m.onnx", MetricsArtifactPath = @"C:\a\m.onnx.metrics.json",
        });
        var generation = new ModelGenerationManifest(ModelGenerationRegistry.SchemaVersion, new string('e', 64), "m.onnx",
            new string('a', 64), Array.Empty<ModelSidecar>(), new string('c', 64), new string('d', 64), "classification",
            "daily", 5, "Up,Down,Neutral", 0.5);
        h.Deployment.Setup(d => d.RegisterGenerationAsync(@"C:\a\m.onnx", @"C:\a\m.onnx.metrics.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(generation);
        using (h.Wizard)
        {
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            await h.Wizard.DeployToModelsCommand.ExecuteAsync(null);
            Assert.False(h.Wizard.HasRegisteredEnsemble);
            h.Deployment.Verify(d => d.RegisterGenerationAsync(@"C:\a\m.onnx", @"C:\a\m.onnx.metrics.json", It.IsAny<CancellationToken>()),
                Times.Once);
            h.Deployment.Verify(d => d.RegisterEnsembleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task EnsembleRegistrationFailure_IsReportedAsAStatus_NotThrown()
    {
        var h = Create(new TrainingRunResult
        {
            RunId = "run", Success = true, OnnxArtifactPath = @"C:\a\m01.onnx", MetricsArtifactPath = @"C:\a\m01.onnx.metrics.json",
            EnsembleManifestPath = @"C:\a\group.ensemble.json",
        });
        h.Deployment.Setup(d => d.RegisterEnsembleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.InvalidDataException("member differs from the scored file"));
        using (h.Wizard)
        {
            await h.Wizard.StartTrainingCommand.ExecuteAsync(null);
            await h.Wizard.DeployToModelsCommand.ExecuteAsync(null);
            Assert.False(h.Wizard.HasRegisteredEnsemble);
            Assert.Contains("member differs from the scored file", h.Wizard.StatusMessage);
            Assert.False(h.Wizard.IsDeploying);
        }
    }

    [Fact]
    public void LocaleKeys_ExistInBothLanguages()
    {
        var keys = new[]
        {
            "Enum_TrainingEnsembleMethod_None", "Enum_TrainingEnsembleMethod_Bagging", "Enum_TrainingEnsembleMethod_Stacking",
            "TrainingWizard_Ensemble", "TrainingWizard_EnsembleMembers", "TrainingWizard_StackingBases",
            "Enum_StackingWeightSource_PastOutOfFold", "Enum_StackingWeightSource_ReleaseMetaSegment",
            "TrainingWizard_StackingWeightSource", "TrainingWizard_StackingWeightSource_Note",
            "TrainingWizard_Ensemble_Note", "TrainingWizard_Pretraining", "TrainingWizard_Pretraining_Note",
            "TrainingWizard_Validation_EnsembleTarget", "TrainingWizard_Validation_EnsembleExclusive",
            "TrainingWizard_Validation_EnsembleMembers", "TrainingWizard_Validation_EnsembleBases",
            "TrainingWizard_Validation_Pretraining", "TrainingWizard_Validation_PretrainingAttention", "TrainingWizard_Status_EnsembleRegistered",
            "TrainingWizard_Status_EnsembleWeightTransfer", "TrainingWizard_Status_EnsembleApplied", "TrainingWizard_Status_EnsembleError", "TrainingWizard_UseAsEnsembleButton",
        };
        foreach (var language in new[] { "en", "ja" })
        {
            LocalizationManager.Instance.Initialize(language);
            foreach (var key in keys)
            {
                var value = LocalizationManager.Instance[key];
                Assert.False(string.IsNullOrWhiteSpace(value) || value == $"[{key}]", $"{language}:{key}");
            }
        }
    }
}
