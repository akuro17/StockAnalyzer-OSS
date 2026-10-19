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

/// <summary>T14 wizard surface: the CPCV request, its validation and the live per-combination results.</summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardCpcvTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;
    private readonly SynchronizationContext? _previousSyncContext = SynchronizationContext.Current;

    public TrainingWizardCpcvTests()
    {
        LocalizationManager.Instance.Initialize("en");
        // Progress<T> captures the current context; the app's is the single UI thread, so deliver synchronously here too.
        SynchronizationContext.SetSynchronizationContext(new SynchronousSynchronizationContext());
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
        LocalizationManager.Instance.Initialize(_originalLanguage);
    }

    private sealed class SynchronousSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static string Text(string key) => LocalizationManager.Instance[key];

    private static (TrainingWizardViewModel Wizard, List<TrainingJobConfig> Started) Create(
        IReadOnlyList<IReadOnlyDictionary<string, double>>? metrics = null)
    {
        var started = new List<TrainingJobConfig>();
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(),
                It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig config, IProgress<TrainingProgress> progress, CancellationToken _) =>
            {
                started.Add(config);
                foreach (var metric in metrics ?? Array.Empty<IReadOnlyDictionary<string, double>>())
                    progress.Report(new TrainingProgress { Stage = "cpcv", Metric = metric });
                return Task.FromResult(new TrainingRunResult { RunId = "run", Success = true });
            });
        var log = new Mock<IExperimentLogService>();
        log.Setup(l => l.RecordAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<TrainingRunResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAA" });
        var store = StockAnalyzer.Avalonia.Tests.TestHelpers.FakeTickerStateStore.FromProfiles(Array.Empty<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var wizard = new TrainingWizardViewModel(orchestrator: orchestrator.Object, experimentLogService: log.Object,
            marketDataProvider: market.Object, tickerStore: store);
        wizard.Horizon = 1;
        return (wizard, started);
    }

    [Fact]
    public async Task Cpcv_IsOffByDefault_AndTheRequestReachesTheJobConfig()
    {
        var (wizard, started) = Create();
        using (wizard)
        {
            Assert.False(wizard.UseCpcv);
            Assert.Equal(15, wizard.CpcvCombinationCount);
            await wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.False(Assert.Single(started).Cpcv);

            started.Clear();
            wizard.UseCpcv = true;
            wizard.CpcvGroups = 5;
            wizard.CpcvTestGroups = 2;
            Assert.Equal(10, wizard.CpcvCombinationCount);
            Assert.Equal(string.Format(Text("TrainingWizard_CpcvCount"), 10), wizard.CpcvCountText);
            Assert.Null(wizard.ExtendedLearningValidationMessage);
            await wizard.StartTrainingCommand.ExecuteAsync(null);
            var config = Assert.Single(started);
            Assert.True(config.Cpcv);
            Assert.Equal((5, 2), (config.CpcvGroups, config.CpcvTestGroups));
        }
    }

    [Fact]
    public void Cpcv_RejectsOversizedMalformedAndExclusiveRequests()
    {
        var (wizard, _) = Create();
        using (wizard)
        {
            wizard.UseCpcv = true;
            Assert.True(wizard.StartTrainingCommand.CanExecute(null));

            foreach (var (groups, tests) in new[] { (10, 3), (6, 6), (6, 0), (21, 1) })
            {
                wizard.CpcvGroups = groups;
                wizard.CpcvTestGroups = tests;
                Assert.Null(wizard.CpcvCombinationCount);
                Assert.Null(wizard.CpcvCountText);
                Assert.False(wizard.StartTrainingCommand.CanExecute(null), $"{groups},{tests}");
                Assert.Contains(TrainingCpcvContract.MaxCombinations.ToString(), wizard.ExtendedLearningValidationMessage);
            }

            wizard.CpcvGroups = 6;
            wizard.CpcvTestGroups = 2;
            Assert.True(wizard.StartTrainingCommand.CanExecute(null));

            wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.Bagging;
            Assert.Equal(Text("TrainingWizard_Validation_CpcvExclusive"), wizard.ExtendedLearningValidationMessage);
            Assert.False(wizard.StartTrainingCommand.CanExecute(null));
            wizard.SelectedEnsembleMethod = TrainingEnsembleMethod.None;

            wizard.SelectedInitializationMode = TrainingInitializationMode.Resume;
            Assert.Equal(Text("TrainingWizard_Validation_CpcvExclusive"), wizard.ExtendedLearningValidationMessage);
            wizard.SelectedInitializationMode = TrainingInitializationMode.Fresh;
            Assert.Null(wizard.ExtendedLearningValidationMessage);
        }
    }

    [Fact]
    public async Task Run_CollectsOneRowPerCombination_WithTheSharedTestGroupLabels()
    {
        IReadOnlyDictionary<string, double> Row(int index, double accuracy) => new Dictionary<string, double>
        {
            ["cpcv_combination"] = index, ["cpcv_combinations"] = 6, ["cpcv_n_train"] = 900 + index, ["cpcv_n_test"] = 400,
            ["cpcv_accuracy"] = accuracy, ["cpcv_macro_f1"] = 0.3, ["cpcv_baseline_accuracy"] = 0.4,
        };
        var (wizard, started) = Create(new[] { Row(0, 0.51), Row(1, 0.52), Row(1, 0.53), Row(5, 0.54) });
        using (wizard)
        {
            wizard.UseCpcv = true;
            wizard.CpcvGroups = 4;
            wizard.CpcvTestGroups = 2;
            Assert.False(wizard.HasCpcvResults);
            await wizard.StartTrainingCommand.ExecuteAsync(null);

            Assert.True(wizard.HasCpcvResults);
            Assert.Equal(new[] { 0, 1, 5 }, wizard.CpcvResults.Select(r => r.Combination).ToArray()); // a re-sent row replaces
            Assert.Equal(0.53, wizard.CpcvResults[1].Accuracy);
            Assert.Equal(new[] { 0, 1 }, wizard.CpcvResults[0].TestGroups);
            Assert.Equal(new[] { 2, 3 }, wizard.CpcvResults[2].TestGroups);
            Assert.Equal(900, wizard.CpcvResults[0].TrainCount);
            Assert.Contains("#1", wizard.CpcvResults[0].Display);
            Assert.Contains("1,2", wizard.CpcvResults[0].Display); // groups are shown 1-based
            Assert.Empty(wizard.FoldResults); // CPCV lines are not outer-fold rows
            Assert.True(Assert.Single(started).Cpcv);

            // a later run without CPCV clears the table and ignores cpcv-looking lines
            wizard.UseCpcv = false;
            await wizard.StartTrainingCommand.ExecuteAsync(null);
            Assert.False(wizard.HasCpcvResults);
        }
    }

    [Fact]
    public void CpcvMetricRow_IgnoresOtherLines_AndRegressionRowsUseRmse()
    {
        Assert.Null(CpcvMetricRow.FromMetric(null, 6, 2));
        Assert.Null(CpcvMetricRow.FromMetric(new Dictionary<string, double> { ["fold"] = 0, ["fold_accuracy"] = 0.5 }, 6, 2));
        Assert.Null(CpcvMetricRow.FromMetric(new Dictionary<string, double> { ["cpcv_combination"] = 0 }, 6, 2));
        var regression = CpcvMetricRow.FromMetric(new Dictionary<string, double>
        {
            ["cpcv_combination"] = 14, ["cpcv_rmse"] = 0.0123, ["cpcv_mae"] = 0.0091, ["cpcv_n_train"] = 10, ["cpcv_n_test"] = 5,
        }, 6, 2)!;
        Assert.True(regression.IsRegression);
        Assert.Equal(new[] { 4, 5 }, regression.TestGroups);
        Assert.Contains("RMSE", regression.Display);
        // a combination index outside the configured shape yields no labels instead of throwing
        var stale = CpcvMetricRow.FromMetric(new Dictionary<string, double>
        {
            ["cpcv_combination"] = 99, ["cpcv_macro_f1"] = 0.3,
        }, 6, 2)!;
        Assert.Empty(stale.TestGroups);
    }

    [Fact]
    public void LocaleKeys_ExistInBothLanguages()
    {
        var keys = new[]
        {
            "TrainingWizard_Cpcv", "TrainingWizard_CpcvGroups", "TrainingWizard_CpcvTestGroups", "TrainingWizard_CpcvCount",
            "TrainingWizard_Cpcv_Note", "TrainingWizard_CpcvResults_Header", "TrainingWizard_CpcvRow",
            "TrainingWizard_CpcvRow_Regression", "TrainingWizard_Validation_CpcvExclusive",
            "TrainingWizard_Validation_CpcvGroups", "TrainingWizard_Stage_cpcv",
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

    [Fact]
    public void ResultsTexts_PresentCpcvAsADiagnostic_NotAsAnEstimateOfDeployedPerformance()
    {
        // the heading and the note keep a CPCV score from being read next to the out-of-sample results
        LocalizationManager.Instance.Initialize("en");
        Assert.Contains("diagnostic", LocalizationManager.Instance["TrainingWizard_CpcvResults_Header"]);
        Assert.Contains("not an estimate", LocalizationManager.Instance["TrainingWizard_CpcvResults_Header"]);
        Assert.Contains("robustness diagnostic", LocalizationManager.Instance["TrainingWizard_Cpcv_Note"]);
        Assert.Contains("not independent samples", LocalizationManager.Instance["TrainingWizard_Cpcv_Note"]);

        LocalizationManager.Instance.Initialize("ja"); // the Japanese texts below are escapes: the source stays English-only
        Assert.Contains("\u8a3a\u65ad", LocalizationManager.Instance["TrainingWizard_CpcvResults_Header"]);
        Assert.Contains("\u904b\u7528\u6027\u80fd\u306e\u63a8\u5b9a\u3067\u306f\u306a\u3044", LocalizationManager.Instance["TrainingWizard_CpcvResults_Header"]);
        Assert.Contains("\u9811\u5065\u6027\u306e\u8a3a\u65ad", LocalizationManager.Instance["TrainingWizard_Cpcv_Note"]);
        Assert.Contains("\u72ec\u7acb\u6a19\u672c\u3067\u306f\u306a\u304f", LocalizationManager.Instance["TrainingWizard_Cpcv_Note"]);
    }
}
