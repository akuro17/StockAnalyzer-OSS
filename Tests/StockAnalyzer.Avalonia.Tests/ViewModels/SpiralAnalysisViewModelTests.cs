using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class SpiralAnalysisViewModelTests
{
    [AvaloniaFact]
    public void NewCandleRevision_UsesActualRangeAndFirstPositiveClose()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(CreateCandles(0m, 125m, 130m));
        using var viewModel = new SpiralAnalysisViewModel(source, new SynchronousDispatcherService());

        Assert.Equal(PriceType.Typical, viewModel.SelectedPriceType);
        Assert.False(viewModel.IsClockwise);
        Assert.Equal(0u, viewModel.StartIndex);
        Assert.Equal(2u, viewModel.EndIndex);
        Assert.True(viewModel.AutoInitialRadius);
        Assert.Equal(125m, viewModel.InitialRadius);

        viewModel.StartIndex = 2;
        viewModel.InitialRadius = 999m;
        source.SetCandles(CreateCandles(40m, 42m, 44m));

        Assert.Equal(0u, viewModel.StartIndex);
        Assert.Equal(2u, viewModel.EndIndex);
        Assert.True(viewModel.AutoInitialRadius);
        Assert.Equal(40m, viewModel.InitialRadius);

        source.SetCandles(CreateCandles(60m, 62m));

        Assert.Equal(0u, viewModel.StartIndex);
        Assert.Equal(1u, viewModel.EndIndex);
        Assert.Equal(60m, viewModel.InitialRadius);
    }

    [AvaloniaFact]
    public async Task PropertyChanges_TriggerCalculationAndPreserveManualRadius()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(CreateCandles(10m, 12m, 14m));
        using var viewModel = new SpiralAnalysisViewModel(source, new SynchronousDispatcherService())
        {
            StartIndex = 1,
            InitialRadius = 50m
        };

        await WaitForResultAsync(viewModel, result => result.Parameters.InitialRadius == 50m && result.Parameters.StartIndex == 1);

        Assert.Equal(1u, viewModel.StartIndex);
        Assert.Equal(2u, viewModel.EndIndex);
        Assert.False(viewModel.AutoInitialRadius);
        Assert.Equal(50m, viewModel.InitialRadius);

        viewModel.BarsPerTurn = 8;
        await WaitForResultAsync(viewModel, result => result.Parameters.BarsPerTurn == 8);
        Assert.Equal(8u, viewModel.Result!.Parameters.BarsPerTurn);
    }

    [AvaloniaFact]
    public void AutoAndManualModes_ControlRangeDrivenRadiusUpdates()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(CreateCandles(10m, 20m, 30m));
        using var viewModel = new SpiralAnalysisViewModel(source, new SynchronousDispatcherService());

        viewModel.StartIndex = 1;
        Assert.Equal(20m, viewModel.InitialRadius);

        viewModel.InitialRadius = 75m;
        viewModel.StartIndex = 0;
        Assert.False(viewModel.AutoInitialRadius);
        Assert.Equal(75m, viewModel.InitialRadius);

        viewModel.AutoInitialRadius = true;
        Assert.True(viewModel.AutoInitialRadius);
        Assert.Equal(10m, viewModel.InitialRadius);
    }

    [AvaloniaFact]
    public void AutoMode_ClearsRadiusWhenRangeHasNoPositiveClose()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(CreateCandles(10m, 0m, -1m));
        using var viewModel = new SpiralAnalysisViewModel(source, new SynchronousDispatcherService());

        viewModel.StartIndex = 1;

        Assert.True(viewModel.AutoInitialRadius);
        Assert.Null(viewModel.InitialRadius);

        source.SetCandles(CreateCandles(5m));

        Assert.Null(viewModel.StartIndex);
        Assert.Null(viewModel.EndIndex);
        Assert.Null(viewModel.InitialRadius);
    }

    [AvaloniaFact]
    public async Task AppliedPriceAndDirectionChanges_RecalculateResultAndDynamicRadius()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(new[]
        {
            new CoreCandleData(new DateTime(2024, 1, 1), 40m, 90m, 30m, 60m, 1L),
            new CoreCandleData(new DateTime(2024, 1, 2), 42m, 96m, 33m, 63m, 1L)
        });
        using var viewModel = new SpiralAnalysisViewModel(source, new SynchronousDispatcherService());

        Assert.Equal(60m, viewModel.InitialRadius);
        viewModel.SelectedPriceType = PriceType.Open;
        viewModel.IsClockwise = true;

        await WaitForResultAsync(viewModel, result =>
            result.Parameters.AppliedPriceType == PriceType.Open && result.Parameters.IsClockwise);

        Assert.Equal(40m, viewModel.InitialRadius);
        Assert.Equal(40m, viewModel.Result!.Samples[0].Price);
        Assert.True(viewModel.Result.Samples[1].AngleRadians < 0d);
    }

    [AvaloniaFact]
    public async Task RapidPropertyChanges_AreDebouncedAndOnlyLatestResultIsApplied()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(CreateCandles(10m, 12m, 14m));
        var dispatcher = new SynchronousDispatcherService();
        using var viewModel = new SpiralAnalysisViewModel(source, dispatcher);
        await WaitForResultAsync(viewModel, result => result.Parameters.BarsPerTurn == 20);

        viewModel.BarsPerTurn = 4;
        viewModel.BarsPerTurn = 6;
        viewModel.BarsPerTurn = 8;

        Assert.Equal(20u, viewModel.Result!.Parameters.BarsPerTurn);
        await WaitForResultAsync(viewModel, result => result.Parameters.BarsPerTurn == 8);
        Assert.True(dispatcher.PostAsyncCallCount > 0);
    }

    [AvaloniaFact]
    public async Task InvalidRange_ClearsCurrentResultAndShowsInputError()
    {
        var source = new SpiralAnalysisDataSource();
        source.SetCandles(CreateCandles(10m, 12m, 14m));
        using var viewModel = new SpiralAnalysisViewModel(source, new SynchronousDispatcherService());
        await WaitForResultAsync(viewModel, result => result.Parameters.EndIndex == 2);

        viewModel.EndIndex = viewModel.StartIndex;

        Assert.Null(viewModel.Result);
        Assert.True(viewModel.HasInvalidInput);
    }

    private static CoreCandleData[] CreateCandles(params decimal[] closes)
    {
        var candles = new CoreCandleData[closes.Length];
        for (int index = 0; index < closes.Length; index++)
        {
            decimal close = closes[index];
            candles[index] = new CoreCandleData(new DateTime(2024, 1, 1).AddDays(index), close, close, close, close, 1L);
        }

        return candles;
    }

    private static async Task WaitForResultAsync(SpiralAnalysisViewModel viewModel, Func<SpiralAnalysisResult, bool> predicate)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (viewModel.Result is { } result && predicate(result))
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(viewModel.Result is { } finalResult && predicate(finalResult));
    }
}
