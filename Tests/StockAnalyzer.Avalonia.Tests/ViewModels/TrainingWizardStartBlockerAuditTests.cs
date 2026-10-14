using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// S4b audit of <c>TrainingWizardViewModel.CanStartTraining</c>: for every condition that can be violated from the window, the table says whether a message beside the
/// field already explains it ("message") or the footer reason (<c>StartBlockerDisplay</c>, "footer") does. A condition that is neither is a defect of the audit.
/// The conditions themselves are not changed by this work: each row starts from a startable wizard and breaks exactly one condition.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardStartBlockerAuditTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;

    public TrainingWizardStartBlockerAuditTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private static TrainingWizardViewModel Wizard(params string[] tickers)
    {
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(new TrainingRunResult { RunId = "run", Success = true }));
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(tickers.ToList());
        var wizard = new TrainingWizardViewModel(orchestrator: orchestrator.Object, marketDataProvider: market.Object) { Horizon = 1 };
        return wizard;
    }

    private static string Text(string key) => LocalizationManager.Instance[key];

    /// <summary>One audited condition: how to break it, how to repair it, and who explains it.</summary>
    private sealed record Row(string Condition, Action<TrainingWizardViewModel> Break, Action<TrainingWizardViewModel> Repair, string? FooterKey, Func<TrainingWizardViewModel, string?>? Message);

    private static readonly Row[] Table =
    [
        // --- explained by the footer reason (these had no message before S4b) ---
        new("transfer start mode without a checkpoint path", w => { w.SelectedInitializationMode = TrainingInitializationMode.Resume; w.CheckpointPath = ""; },
            w => { w.CheckpointPath = "best.json"; w.SelectedInitializationMode = TrainingInitializationMode.Fresh; }, "TrainingWizard_Start_CheckpointPath", null),
        new("fine-tune without a valid parent model id", w => { w.SelectedInitializationMode = TrainingInitializationMode.FineTune; w.CheckpointPath = "best.json"; w.ParentModelId = "not-a-hash"; },
            w => { w.ParentModelId = new string('a', 64); w.SelectedInitializationMode = TrainingInitializationMode.Fresh; }, "TrainingWizard_Start_ParentModel", null),
        new("duplicate module path to freeze", w => { w.SelectedInitializationMode = TrainingInitializationMode.FineTune; w.CheckpointPath = "best.json"; w.ParentModelId = new string('a', 64); w.FreezePathsText = "features, features"; },
            w => { w.FreezePathsText = ""; w.SelectedInitializationMode = TrainingInitializationMode.Fresh; }, "TrainingWizard_Start_FreezePaths", null),
        new("input length below 1", w => w.WindowSize = 0, w => w.WindowSize = 75, "TrainingWizard_Start_WindowSize", null),
        new("prediction length below 1", w => w.Horizon = 0, w => w.Horizon = 1, "TrainingWizard_Start_Horizon", null),
        new("validation splits below 2", w => w.NSplits = 1, w => w.NSplits = 5, "TrainingWizard_Start_Splits", null),
        new("lags that are not whole numbers", w => { w.UseFixedZScore = true; w.LagsText = "abc"; }, w => { w.LagsText = ""; w.UseFixedZScore = false; }, "TrainingWizard_Start_Lags", null),
        new("fixed z-score with a channel that has its own normalization", w =>
            {
                w.FeaturePicker.Channels.Add(new FeatureChannelRowViewModel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA, Normalization = ChannelNormalization.WindowMinMax });
                w.UseFixedZScore = true;
            },
            w => { w.UseFixedZScore = false; w.FeaturePicker.Channels.Clear(); }, "TrainingWizard_Start_ZScoreNormalization", null),
        // --- explained by a message beside the field (already there before S4b) ---
        new("calibration with a regression target", w => { w.UseCalibration = true; w.SelectedTargetType = TargetType.Regression; },
            w => { w.SelectedTargetType = TargetType.Classification; w.UseCalibration = false; }, null, w => w.CalibrationValidationMessage),
        new("clip sigma that is not a positive number", w => { w.UseFixedZScore = true; w.UseClipSigma = true; w.ClipSigmaText = "invalid"; },
            w => { w.UseClipSigma = false; w.UseFixedZScore = false; }, null, w => w.ClipSigmaValidationMessage),
        new("weekly data with indicator channels but no manifest", w =>
            {
                w.SelectedTimeframe = TrainingTimeframe.Weekly;
                w.FeaturePicker.Channels.Add(new FeatureChannelRowViewModel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA });
            },
            w => { w.SelectedTimeframe = TrainingTimeframe.Daily; w.FeaturePicker.Channels.Clear(); }, null, w => w.IsManifestMissing ? "manifest required" : null),
    ];

    [Fact]
    public void EveryAuditedConditionIsExplained_ByAMessageBesideItsFieldOrByTheFooterReason()
    {
        foreach (var row in Table)
        {
            using var wizard = Wizard("AAA");
            Assert.True(wizard.StartTrainingCommand.CanExecute(null), "baseline is startable before: " + row.Condition);
            Assert.Equal("", wizard.StartBlockerDisplay);
            row.Break(wizard);
            Assert.False(wizard.StartTrainingCommand.CanExecute(null), "the gate is closed: " + row.Condition);
            if (row.FooterKey is not null)
            {
                string expected = row.FooterKey == "TrainingWizard_Start_Splits"
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Text(row.FooterKey), TrainingResourceOverrides.MaximumEvaluationFolds)
                    : Text(row.FooterKey);
                Assert.True(expected == wizard.StartBlockerDisplay, $"footer reason for '{row.Condition}': expected '{expected}' but was '{wizard.StartBlockerDisplay}'");
                Assert.True(wizard.HasStartBlocker, row.Condition);
            }
            else
            {
                Assert.NotNull(row.Message!(wizard));          // a message beside the field already says it
                Assert.Equal("", wizard.StartBlockerDisplay);   // and the footer does not repeat it
            }
            row.Repair(wizard);
            Assert.True(wizard.StartTrainingCommand.CanExecute(null), "repaired and startable again: " + row.Condition);
            Assert.Equal("", wizard.StartBlockerDisplay);
        }
    }

    [Fact]
    public void NoTickerAtAll_IsExplainedByTheFooterReason()
    {
        using var wizard = Wizard();                       // the market has no ticker, so "All tickers" resolves to nothing
        Assert.False(wizard.StartTrainingCommand.CanExecute(null));
        Assert.Equal(Text("TrainingWizard_Start_NoTickers"), wizard.StartBlockerDisplay);
    }

    [Fact]
    public void TheFooterReasonIsEmptyWhileARunOrADeploymentIsActive_AndFollowsTheLanguage()
    {
        using var wizard = Wizard("AAA");
        wizard.Horizon = 0;
        Assert.Equal(Text("TrainingWizard_Start_Horizon"), wizard.StartBlockerDisplay);
        wizard.IsTraining = true;
        Assert.Equal("", wizard.StartBlockerDisplay);
        wizard.IsTraining = false;
        var seen = new List<string?>();
        wizard.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        LocalizationManager.Instance.Initialize("ja");
        Assert.Contains(nameof(wizard.StartBlockerDisplay), seen);
        Assert.Equal(Text("TrainingWizard_Start_Horizon"), wizard.StartBlockerDisplay);
        Assert.NotEqual("The prediction length must be at least 1 bar.", wizard.StartBlockerDisplay);
    }

    [Fact]
    public void TheStartModeLabelAppearsOnlyForANonDefaultMode()
    {
        using var wizard = Wizard("AAA");
        Assert.Empty(wizard.ModeBadges); Assert.False(wizard.HasModeBadges);                       // default: a new training, no label
        var seen = new List<string?>();
        wizard.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        wizard.SelectedInitializationMode = TrainingInitializationMode.FineTune;
        Assert.Equal(new[] { Text("Enum_TrainingInitializationMode_FineTune") }, wizard.ModeBadges);
        Assert.True(wizard.HasModeBadges);
        Assert.Contains(nameof(wizard.ModeBadges), seen);
        wizard.SelectedInitializationMode = TrainingInitializationMode.Resume;
        Assert.Equal(new[] { Text("Enum_TrainingInitializationMode_Resume") }, wizard.ModeBadges);
        wizard.SelectedInitializationMode = TrainingInitializationMode.Fresh;
        Assert.Empty(wizard.ModeBadges);
    }

    [Fact]
    public void CpcvPretrainingAndAnEnsembleAreLabelledOnlyWhileTheyAreOn()
    {
        using var wizard = Wizard("AAA");
        var seen = new List<string?>();
        wizard.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        Assert.Empty(wizard.ModeBadges);                                                            // the defaults: nothing is labelled
        wizard.UseCpcv = true;
        Assert.Equal(new[] { Text("TrainingWizard_Badge_Cpcv") }, wizard.ModeBadges);
        Assert.True(wizard.HasModeBadges);
        wizard.UseCpcv = false;
        Assert.Empty(wizard.ModeBadges); Assert.False(wizard.HasModeBadges);
        wizard.UsePretraining = true;
        Assert.Equal(new[] { Text("TrainingWizard_Badge_Pretraining") }, wizard.ModeBadges);
        wizard.UsePretraining = false;
        Assert.Empty(wizard.ModeBadges);
        wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Bagging;
        Assert.Equal(new[] { Text("Enum_TrainingEnsembleMethod_Bagging") }, wizard.ModeBadges);
        wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Stacking;
        Assert.Equal(new[] { Text("Enum_TrainingEnsembleMethod_Stacking") }, wizard.ModeBadges);
        wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.None;
        Assert.Empty(wizard.ModeBadges);
        Assert.Contains(nameof(wizard.ModeBadges), seen);
        Assert.Contains(nameof(wizard.HasModeBadges), seen);
        wizard.UsePretraining = true; wizard.SelectedInitializationMode = TrainingInitializationMode.Resume;
        Assert.Equal(new[] { Text("Enum_TrainingInitializationMode_Resume"), Text("TrainingWizard_Badge_Pretraining") }, wizard.ModeBadges);   // after the start mode
        LocalizationManager.Instance.Initialize("ja");
        Assert.Equal(new[] { Text("Enum_TrainingInitializationMode_Resume"), Text("TrainingWizard_Badge_Pretraining") }, wizard.ModeBadges);
        Assert.NotEqual("Pretraining", wizard.ModeBadges[1]);                                      // the labels follow the language
    }
}
