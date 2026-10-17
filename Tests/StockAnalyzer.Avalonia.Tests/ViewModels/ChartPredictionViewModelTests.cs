using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("LocalizationSharedState")]
public sealed class ChartPredictionViewModelTests
{
    private static PredictionResult DailyPrediction() => new(string.Empty, float.NaN,
        Array.Empty<ClassScore>(), IsFallback: false)
    {
        IsRegression = true,
        PredictedLogReturn = 0.04,
        OutputContract = new PredictionOutputContract(PredictionOutputSemantic.LogReturn,
            PredictionOutputUnit.Dimensionless, 5, TimeframeType.Daily,
            ConfidenceType.None, PredictionTargetFormula.LogFutureCloseOverAnchorClose),
    };

    [Fact]
    public async Task LoadedDailyChart_ShowsCurrentRegressionPrediction()
    {
        var data = new DailyDataService();
        var prediction = new DelayedPredictionService();
        using var chart = new ChartViewModel(data, new DialogService(), null!,
            new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
            new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), prediction, null!, messenger: new StrongReferenceMessenger());

        prediction.Complete(DailyPrediction());
        await chart.LoadDataAsync();
        await prediction.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 50 && chart.CurrentPrediction is null; attempt++)
            await Task.Delay(20);

        Assert.NotNull(chart.CurrentPrediction);
        Assert.True(chart.HasPredictionDisplay);
    }

    [Fact]
    public async Task TimeframeSwitch_DoesNotShowPredictionFromPreviousLoad()
    {
        var data = new DailyDataService();
        var prediction = new DelayedPredictionService();
        using var chart = new ChartViewModel(data, new DialogService(), null!,
            new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
            new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), prediction, null!, messenger: new StrongReferenceMessenger());

        await chart.LoadDataAsync();
        await prediction.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        chart.SelectedTimeFrame = TimeframeType.Weekly;
        prediction.Complete(DailyPrediction());

        await Task.Delay(250);
        Assert.Null(chart.CurrentPrediction);
        Assert.Null(chart.PredictionDisplay);
    }

    [Fact]
    public async Task SymbolChange_DoesNotShowPredictionOfThePreviousSymbol()
    {
        // The symbol change reloads the data and predicts again, so the first request is held while later ones
        // answer with a fallback; the late answer of the first request must not reach the new symbol's chart.
        var data = new DailyDataService();
        var prediction = new HeldFirstPredictionService();
        using var chart = new ChartViewModel(data, new DialogService(), null!,
            new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
            new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), prediction, null!, messenger: new StrongReferenceMessenger());

        await chart.LoadDataAsync();
        await prediction.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        chart.Symbol = "MSFT";
        await WaitUntilAsync(() => prediction.Calls >= 2);
        prediction.CompleteFirst(DailyPrediction());

        await Task.Delay(250);
        Assert.Null(chart.CurrentPrediction);
        Assert.Null(chart.PredictionDisplay);
        Assert.False(chart.HasPredictionDisplay);
    }

    private static readonly EnsembleSpec SavedEnsemble = FixedEnsembleSettingsManager.SampleSaved;

    private static PredictionResult EnsembleClassPrediction(EnsembleSpec produced) => new("Up", .7f,
        Array.Empty<ClassScore>(), .7f, 0f, IsFallback: false)
    {
        // An ensemble result carries no generation id, only the ensemble that produced it.
        Ensemble = produced,
        ConfidenceType = ConfidenceType.ClassProbability,
        ConfidenceValue = .7f,
        OutputContract = new PredictionOutputContract(PredictionOutputSemantic.ClassProbabilities,
            PredictionOutputUnit.Probability, 5, TimeframeType.Daily, ConfidenceType.ClassProbability,
            PredictionTargetFormula.ThresholdedSimpleReturn),
    };

    private static (ChartViewModel Chart, SequencedPredictionService Service) CreateEnsembleChart(
        EnsembleSpec? saved, PredictionResult result)
    {
        var data = new DailyDataService();
        // The single-generation pointer differs from the ensemble result's (absent) model id on purpose.
        var service = new SequencedPredictionService(new[] { result }) { ActiveModelId = "single-generation-pointer" };
        var chart = new ChartViewModel(data, new DialogService(), null!,
            new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
            new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), service, null!, messenger: new StrongReferenceMessenger(),
            ensembleSettings: new FixedEnsembleSettingsManager(saved));
        return (chart, service);
    }

    [Fact]
    public async Task EnsemblePrediction_IsShownWithItsProbability_WhileItIsTheSavedEnsemble()
    {
        var (chart, _) = CreateEnsembleChart(SavedEnsemble, EnsembleClassPrediction(SavedEnsemble));
        using var _ = chart;
        await chart.LoadDataAsync();
        await WaitUntilAsync(() => chart.HasPredictionDisplay);

        Assert.Contains("Up", chart.PredictionDisplay);
        Assert.Contains("70", chart.PredictionDisplay);
        Assert.NotNull(chart.CurrentPrediction);
    }

    [Fact]
    public async Task EnsemblePrediction_IsNotShown_WhenAnotherEnsembleOrNoneIsSavedNow()
    {
        foreach (var saved in new[] { EnsembleSpec.Equal(new[] { "member-a", "member-c" }), null })
        {
            var (chart, service) = CreateEnsembleChart(saved, EnsembleClassPrediction(SavedEnsemble));
            using var _ = chart;
            await chart.LoadDataAsync();
            await WaitUntilAsync(() => service.Calls == 1);
            await Task.Delay(250);

            Assert.False(chart.HasPredictionDisplay);
            Assert.Null(chart.CurrentPrediction);
        }
    }

    /// <summary>Runs <paramref name="body"/> while the localization holds only unrelated keys, so every prediction template key is unknown; restores the language afterwards.</summary>
    private static async Task WithUnknownTemplatesAsync(Func<Task> body)
    {
        var language = LocalizationManager.Instance.CurrentLanguage;
        var directory = Path.Combine(Path.GetTempPath(), "sa_locale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string unknownLanguage = "zz";
            File.WriteAllText(Path.Combine(directory, unknownLanguage + ".json"), "{ \"Unrelated_Key\": \"unrelated\" }");
            LocalizationManager.Instance.Initialize(unknownLanguage, directory);
            await body();
        }
        finally
        {
            LocalizationManager.Instance.Initialize(language);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(PredictionDisplayFormatter.ReturnLabelKey, false)]
    [InlineData(PredictionDisplayFormatter.ClassLabelWithProbabilityKey, true)]
    public async Task PredictionWhoseTemplateKeyIsUnknown_ShowsNoText_NotTheBracketedKey(string templateKey, bool ensembleClass)
    {
        await WithUnknownTemplatesAsync(async () =>
        {
            // The premise of the test: the running localization answers an unknown key with a non-blank bracketed text.
            Assert.False(LocalizationManager.Instance.TryGet(templateKey, out _));
            Assert.Equal("[" + templateKey + "]", LocalizationManager.Instance.Get(templateKey));

            var (chart, service) = ensembleClass
                ? CreateEnsembleChart(SavedEnsemble, EnsembleClassPrediction(SavedEnsemble))
                : CreateRegressionChart();
            using var chartScope = chart;
            await chart.LoadDataAsync();
            await WaitUntilAsync(() => service.Calls == 1);
            await Task.Delay(250);                                  // negative assertion: nothing may appear

            Assert.Null(chart.PredictionDisplay);
            Assert.False(chart.HasPredictionDisplay);
            Assert.Null(chart.CurrentPrediction);                   // and no forecast is recorded for a prediction that is not shown
        });
    }

    private static (ChartViewModel Chart, SequencedPredictionService Service) CreateRegressionChart()
    {
        var (chart, _, service) = CreateChart(DailyPrediction());
        return (chart, service);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20);
        Assert.True(condition(), "the chart did not reach the expected state in time");
    }

    private static (ChartViewModel Chart, StrongReferenceMessenger Messenger, SequencedPredictionService Service) CreateChart(
        params PredictionResult[] results)
    {
        var data = new DailyDataService();
        var service = new SequencedPredictionService(results);
        var messenger = new StrongReferenceMessenger();
        var chart = new ChartViewModel(data, new DialogService(), null!,
            new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
            new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), service, null!, messenger: messenger);
        return (chart, messenger, service);
    }

    [Fact]
    public async Task ChangedPredictionConfiguration_PredictsAgainAtOnce_WithoutReloadingTheData()
    {
        var (chart, messenger, service) = CreateChart(DailyPrediction(), PredictionResult.Empty);
        using var _ = chart;
        await chart.LoadDataAsync();
        await WaitUntilAsync(() => chart.HasPredictionDisplay);
        var revision = chart.DataRevision;
        Assert.Equal(1, service.Calls);

        // e.g. the owner deactivated the model: the next prediction is a fallback, and the old text must go.
        messenger.Send(new StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage());
        await WaitUntilAsync(() => service.Calls == 2 && !chart.HasPredictionDisplay);

        Assert.Null(chart.CurrentPrediction);
        Assert.Equal(revision, chart.DataRevision); // the candles were not reloaded
    }

    [Fact]
    public async Task ChangedPredictionConfiguration_ShowsThePredictionOfAnewlyUsableModel()
    {
        var (chart, messenger, service) = CreateChart(PredictionResult.Empty, DailyPrediction());
        using var _ = chart;
        await chart.LoadDataAsync();
        await WaitUntilAsync(() => service.Calls == 1);
        Assert.False(chart.HasPredictionDisplay);

        messenger.Send(new StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage());

        await WaitUntilAsync(() => chart.HasPredictionDisplay);
        Assert.NotNull(chart.CurrentPrediction);
    }

    [Fact]
    public async Task ChangedPredictionConfiguration_IsIgnoredWithoutCandlesAndAfterDisposal()
    {
        var (chart, messenger, service) = CreateChart(DailyPrediction());
        messenger.Send(new StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage()); // nothing loaded yet
        await Task.Delay(100);
        Assert.Equal(0, service.Calls);

        await chart.LoadDataAsync();
        await WaitUntilAsync(() => service.Calls == 1);
        chart.Dispose();
        messenger.Send(new StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage());
        await Task.Delay(100);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task FallbackCausedByAWindowMismatch_ShowsANoticeWithBothWindows_AndNothingElse()
    {
        var data = new DailyDataService();
        var prediction = new DelayedPredictionService { LoadIssue = new PredictionWindowMismatchException(25, 50) };
        using var chart = new ChartViewModel(data, new DialogService(), null!,
            new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
            new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), prediction, null!, messenger: new StrongReferenceMessenger());

        prediction.Complete(PredictionResult.Empty);
        await chart.LoadDataAsync();
        await prediction.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 50 && chart.PredictionNotice is null; attempt++)
            await Task.Delay(20);

        Assert.True(chart.HasPredictionNotice);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Instance.Get("Prediction_Notice_WindowMismatch"), 25, 50), chart.PredictionNotice);
        Assert.Contains("25", chart.PredictionNotice);
        Assert.Contains("50", chart.PredictionNotice);
        Assert.False(chart.HasPredictionDisplay); // a fallback still shows no prediction
        Assert.Null(chart.CurrentPrediction);
    }

    [Fact]
    public async Task FallbackWithoutAWindowMismatch_AndASuccessfulPrediction_ShowNoNotice()
    {
        foreach (var result in new[] { PredictionResult.Empty, DailyPrediction() })
        {
            var data = new DailyDataService();
            var prediction = new DelayedPredictionService(); // LoadIssue null: not loaded yet, or another cause
            using var chart = new ChartViewModel(data, new DialogService(), null!,
                new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
                new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
                new SynchronousDispatcherService(), prediction, null!, messenger: new StrongReferenceMessenger());

            prediction.Complete(result);
            await chart.LoadDataAsync();
            await prediction.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);

            Assert.False(chart.HasPredictionNotice);
            Assert.Null(chart.PredictionNotice);
        }
    }

    private static ChartViewModel ChartWith(IDataService data, IPredictionService? prediction) => new(data, new DialogService(), null!,
        new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
        new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
        new SynchronousDispatcherService(), prediction, null!, messenger: new StrongReferenceMessenger());

    [Fact]
    public async Task RunPredictionNow_PredictsAgainAndReportsWhatTheChartShows()
    {
        var data = new DailyDataService();
        var prediction = new SequencedPredictionService(new[] { DailyPrediction() });
        using var chart = ChartWith(data, prediction);
        await chart.LoadDataAsync();
        for (var attempt = 0; attempt < 100 && chart.PredictionDisplay is null; attempt++) await Task.Delay(20);
        int before = prediction.Calls;

        var report = await chart.RunPredictionNowAsync();

        Assert.Equal(PredictionRunOutcome.Completed, report.Outcome);
        Assert.True(prediction.Calls > before, "the run asked the prediction service again");
        Assert.False(string.IsNullOrEmpty(report.Display));
        Assert.Equal(chart.PredictionDisplay, report.Display);
        Assert.Equal(chart.Symbol, report.Symbol);
        Assert.Equal(chart.SelectedTimeFrame, report.Timeframe);
    }

    [Fact]
    public async Task RunPredictionNow_WithoutCandlesOrWithoutAPredictionService_SaysSoAndPredictsNothing()
    {
        var prediction = new SequencedPredictionService(new[] { DailyPrediction() });
        using var empty = ChartWith(new DailyDataService(), prediction);
        Assert.Equal(PredictionRunOutcome.NoData, (await empty.RunPredictionNowAsync()).Outcome);
        Assert.Equal(0, prediction.Calls);

        using var unavailable = ChartWith(new DailyDataService(), null);
        await unavailable.LoadDataAsync();
        Assert.Equal(PredictionRunOutcome.Unavailable, (await unavailable.RunPredictionNowAsync()).Outcome);
    }

    /// <summary>Answers each call with the next result (the last one repeats) and counts the calls.</summary>
    private sealed class SequencedPredictionService(PredictionResult[] results) : IPredictionService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? ActiveModelId { get; init; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles) =>
            Task.FromResult(results[Math.Min(Interlocked.Increment(ref _calls) - 1, results.Length - 1)]);
    }

    /// <summary>Holds the first call until <see cref="CompleteFirst"/>; every later call answers with a fallback.</summary>
    private sealed class HeldFirstPredictionService : IPredictionService
    {
        private int _calls;
        private readonly TaskCompletionSource<PredictionResult> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync() => Task.CompletedTask;
        public Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles)
        {
            if (Interlocked.Increment(ref _calls) != 1) return Task.FromResult(PredictionResult.Empty);
            Started.TrySetResult();
            return _first.Task;
        }

        public void CompleteFirst(PredictionResult result) => _first.TrySetResult(result);
    }

    private sealed class DelayedPredictionService : IPredictionService
    {
        public PredictionWindowMismatchException? LoadIssue { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PredictionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync() => Task.CompletedTask;
        public Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles)
        {
            Started.TrySetResult();
            return _completion.Task;
        }

        public void Complete(PredictionResult result) => _completion.TrySetResult(result);
    }

    private sealed class DailyDataService : IDataService
    {
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame,
            int count = 100)
        {
            IReadOnlyList<CandleData> candles = Enumerable.Range(0, 40)
                .Select(index => new CandleData(new DateTime(2025, 1, 1).AddDays(index),
                    100m + index, 101m + index, 99m + index, 100m + index, 1000))
                .ToArray();
            return Task.FromResult(candles);
        }
    }
}
