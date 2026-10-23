using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The default value inventory of the Training Wizard (docs/SA_UI_INTERACTION.md, "Default value inventory"). Every expected value below is the literal of the inventory
/// table, NOT read back from the code, so changing a default (or re-ordering a list that used to define one implicitly) fails here and has to be a deliberate decision.
/// The RL and Portfolio RL rows are pinned beside their view-model tests: <c>RlUiTests.DefaultValueInventory_*</c> and <c>PortfolioRlDialogViewModelTests.DefaultValueInventory_*</c>.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class DefaultValueInventoryTests
{
    private static async Task<(TrainingWizardViewModel Wizard, TrainingJobConfig Config)> StartedWizardAsync()
    {
        TrainingJobConfig? captured = null;
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig config, IProgress<TrainingProgress> _, CancellationToken _) =>
            {
                captured = config;
                return Task.FromResult(new TrainingRunResult { RunId = "run", Success = true });
            });
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new System.Collections.Generic.List<string> { "AAA" });
        var tickerStore = FakeTickerStateStore.FromProfiles(Array.Empty<WatchlistProfile>());     // the wizard's symbols come from the ticker store (All Tickers node)
        var wizard = new TrainingWizardViewModel(orchestrator: orchestrator.Object, marketDataProvider: market.Object, tickerStore: tickerStore);
        Assert.True(wizard.StartTrainingCommand.CanExecute(null), "the wizard starts at its defaults with a ticker and nothing else");
        await wizard.StartTrainingCommand.ExecuteAsync(null);
        Assert.NotNull(captured);
        return (wizard, captured!);
    }

    [Fact]
    public async Task Wizard_StartsEveryOptionAtItsInventoryDefault()
    {
        var (wizard, config) = await StartedWizardAsync();
        using (wizard)
        {
            Assert.Equal(TrainingTimeframe.Daily, config.Timeframe);
            Assert.Equal(TargetType.Classification, config.TargetType);
            Assert.Equal(TrainingFramework.PyTorch, config.Framework);
            Assert.Equal("lstm", config.Architecture);
            Assert.Equal(60, config.WindowSize);
            Assert.Equal(5, config.Horizon);
            Assert.Equal(5, config.NSplits);
            Assert.Null(config.Gap);                                  // empty = automatic purge (window + horizon - 1), see the meaning test
            Assert.Null(config.OosTailDays);                          // empty = no final test period
            Assert.Equal(TrainingInitializationMode.Fresh, config.InitializationMode);
            Assert.False(config.Calibration);
            Assert.False(config.Cpcv);
            Assert.False(config.FixedZScore);
            Assert.Null(config.ClipSigma);
            Assert.Equal(TrainingPretraining.None, config.Pretraining);
            Assert.Equal(PredictionFeatureMode.OhlcvMinMax, config.FeatureMode);
            Assert.Equal(6, config.CpcvGroups);
            Assert.Equal(2, config.CpcvTestGroups);
            Assert.Empty(config.Lags!);
            Assert.Equal(TrainingEnsembleMethod.None, config.EnsembleMethod);
            Assert.Empty(config.Hyperparameters);
            Assert.Empty(wizard.ModeBadges);                          // nothing is labelled while everything is at its default
        }
    }

    [Theory]
    [InlineData(TrainingFramework.PyTorch, "lstm")]
    [InlineData(TrainingFramework.LightGBM, "gbdt")]
    [InlineData(TrainingFramework.TensorFlow, "lstm")]
    public void Wizard_EachFrameworkStartsWithItsExplicitDefaultArchitecture(TrainingFramework framework, string expected)
    {
        using var wizard = new TrainingWizardViewModel();
        wizard.SelectedFramework = framework == TrainingFramework.PyTorch ? TrainingFramework.TensorFlow : TrainingFramework.PyTorch;   // leave the framework, then come back
        wizard.SelectedFramework = framework;
        Assert.Equal(expected, wizard.SelectedArchitecture);
        Assert.Contains(wizard.SelectedArchitecture, wizard.AvailableArchitectures);                // the default is one of the offered architectures
    }

    [Fact]
    public void Wizard_ANewWizardStartsWithThePyTorchDefaultArchitecture()
    {
        using var wizard = new TrainingWizardViewModel();
        Assert.Equal((TrainingFramework.PyTorch, "lstm"), (wizard.SelectedFramework, wizard.SelectedArchitecture));
    }

    [Fact]
    public void EmptyGap_MeansTheAutomaticPurgeOfWindowPlusHorizonMinusOne()
    {
        const int window = 75, horizon = 5;
        Assert.Equal(WalkForwardDataRequirement.MinimumRawBarsForTraining(window, horizon, gap: window + horizon - 1),
            WalkForwardDataRequirement.MinimumRawBarsForTraining(window, horizon, gap: null));
        Assert.Equal(WalkForwardDataRequirement.MinimumRawBars(window, horizon, gap: 79), WalkForwardDataRequirement.MinimumRawBars(window, horizon, gap: null));
    }

    [Fact]
    public void TheGuideStatesTheMeaningOfTheEmptyGapAndTheEmptyFinalTestPeriod()
    {
        string guide = File.ReadAllText(GuidePath());
        var gap = guide.Split('\n').Single(line => line.StartsWith("| Exclusion gap (bars)", StringComparison.Ordinal));
        var tail = guide.Split('\n').Single(line => line.StartsWith("| Final test period (days)", StringComparison.Ordinal));
        Assert.Contains("Empty leaves the split at its default (window + horizon - 1).", gap);
        Assert.Contains("empty disables the holdout", tail);
    }

    private static string GuidePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "TRAINING_AND_REINFORCEMENT_LEARNING_GUIDE.md");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("docs/TRAINING_AND_REINFORCEMENT_LEARNING_GUIDE.md was not found above the test output directory.");
    }
}
