using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("LocalizationSharedState")]
public sealed class TrainingWizardCalibrationTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;
    public TrainingWizardCalibrationTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private static (TrainingWizardViewModel Wizard, List<TrainingJobConfig> Started) Wizard()
    {
        var started = new List<TrainingJobConfig>();
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(),
                It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig config, IProgress<TrainingProgress> _, CancellationToken _) =>
            {
                started.Add(config);
                return Task.FromResult(new TrainingRunResult { RunId = "run", Success = true });
            });
        var log = new Mock<IExperimentLogService>();
        log.Setup(l => l.RecordAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<TrainingRunResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAA" });
        var wizard = new TrainingWizardViewModel(orchestrator: orchestrator.Object, experimentLogService: log.Object,
            marketDataProvider: market.Object);
        wizard.Horizon = 1;
        return (wizard, started);
    }

    [Fact]
    public async Task Calibration_IsOffByDefault_AndTheCheckedChoiceReachesTheJobConfig()
    {
        var (wizard, started) = Wizard();
        using (wizard)
        {
            Assert.False(wizard.UseCalibration);
            Assert.Null(wizard.CalibrationValidationMessage);
            await wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.False(Assert.Single(started).Calibration);

            started.Clear();
            wizard.UseCalibration = true;
            Assert.True(wizard.StartTrainingCommand.CanExecute(null));
            await wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.True(Assert.Single(started).Calibration);
        }
    }

    [Fact]
    public void Calibration_IsRejectedForRegression_AndRecoversWhenClassificationIsReselected()
    {
        var (wizard, _) = Wizard();
        using (wizard)
        {
            wizard.UseCalibration = true;
            Assert.True(wizard.StartTrainingCommand.CanExecute(null));
            wizard.SelectedTargetType = TargetType.Regression;
            Assert.False(wizard.StartTrainingCommand.CanExecute(null));
            Assert.Equal(LocalizationManager.Instance["TrainingWizard_Validation_Calibration"], wizard.CalibrationValidationMessage);
            wizard.SelectedTargetType = TargetType.Classification;
            Assert.True(wizard.StartTrainingCommand.CanExecute(null));
            Assert.Null(wizard.CalibrationValidationMessage);
        }
    }

    [Fact]
    public void Calibration_IsRejectedForAFinalizedManifestSource()
    {
        var (wizard, _) = Wizard();
        using (wizard)
        {
            wizard.SelectedTimeframe = TrainingTimeframe.Weekly;
            wizard.SourceManifestPath = "manifest.json";
            wizard.FeaturePicker.Channels.Add(new FeatureChannelRowViewModel
            {
                Kind = FeatureChannelKind.Indicator,
                Indicator = IndicatorType.SMA,
            });
            Assert.True(wizard.StartTrainingCommand.CanExecute(null));
            wizard.UseCalibration = true;
            Assert.False(wizard.StartTrainingCommand.CanExecute(null));
            Assert.NotNull(wizard.CalibrationValidationMessage);
            wizard.UseCalibration = false;
            Assert.True(wizard.StartTrainingCommand.CanExecute(null));
        }
    }
}
