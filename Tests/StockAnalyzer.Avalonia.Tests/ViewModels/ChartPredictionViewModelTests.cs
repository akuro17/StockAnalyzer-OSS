using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

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

    private sealed class DelayedPredictionService : IPredictionService
    {
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
