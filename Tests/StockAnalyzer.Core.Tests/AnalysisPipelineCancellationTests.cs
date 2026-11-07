#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// The token-aware pipeline call (MCP Part 4 review, Run 5b): it stops before it starts an indicator once the token is cancelled, never turns the cancellation into a failed
/// indicator result, and with a token that is never cancelled gives exactly what the plain call gives (the chart and every other caller keep using the plain call).
/// </summary>
public sealed class AnalysisPipelineCancellationTests
{
    private static List<CoreCandleData> Candles(int count = 60)
        => Enumerable.Range(0, count).Select(i => new CoreCandleData(DateTime.Today.AddDays(i), 100 + i, 102 + i, 98 + i, 100 + (i % 7), 1000)).ToList();

    private static CoreIndicatorSettings Sma(string id, int period, string? sourceId = null, string? dynamicPeriodId = null) => new()
    {
        Id = id,
        IsEnabled = true,
        TypeEnum = IndicatorType.SMA,
        ParameterObject = new CoreSmaParameter { Period = period },
        SourceIndicatorId = sourceId,
        DynamicPeriodIndicatorId = dynamicPeriodId,
    };

    private static List<CoreIndicatorSettings> ThreeIndependent() => new() { Sma("a", 3), Sma("b", 5), Sma("c", 8) };

    /// <summary>A factory over the real one that counts the indicators the pipeline starts and runs a hook at the n-th creation.</summary>
    private sealed class CountingFactory
    {
        private int _created;

        public CountingFactory(Action<int>? onCreate = null)
        {
            Mock = new Mock<IIndicatorFactory>();
            Mock.Setup(f => f.Create(It.IsAny<IndicatorType>(), It.IsAny<CoreIndicatorParameterBase?>()))
                .Returns<IndicatorType, CoreIndicatorParameterBase?>((type, parameters) =>
                {
                    int number = Interlocked.Increment(ref _created);
                    onCreate?.Invoke(number);
                    return IndicatorFactory.Default.Create(type, parameters);
                });
        }

        public Mock<IIndicatorFactory> Mock { get; }

        public int Created => Volatile.Read(ref _created);
    }

    [Fact]
    public async Task WithATokenThatIsNeverCancelledTheResultEqualsThePlainCallForIndependentChainedAndDrivenIndicators()
    {
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default);
        List<CoreCandleData> candles = Candles();
        List<CoreIndicatorSettings> Settings() => new()
        {
            Sma("a", 3), Sma("b", 5), Sma("onA", 4, sourceId: "a"), Sma("driver", 6), Sma("driven", 5, dynamicPeriodId: "driver"),
        };

        Dictionary<string, IIndicatorResult> plain = await ((IAnalysisPipelineService)service).CalculateIndicatorsAsync(candles, Settings());
        Dictionary<string, IIndicatorResult> aware = await ((ICancellableIndicatorCalculation)service).CalculateIndicatorsAsync(candles, Settings(), null, CancellationToken.None);

        Assert.Equal(plain.Keys.OrderBy(k => k, StringComparer.Ordinal), aware.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(5, aware.Count);
        foreach (string id in plain.Keys)
        {
            Assert.Equal(plain[id].IsSuccessful, aware[id].IsSuccessful);
            Assert.Equal(plain[id].ErrorMessage, aware[id].ErrorMessage);
            Assert.Equal(plain[id].MainValues, aware[id].MainValues);
        }
    }

    [Fact]
    public async Task ACallCancelledBeforeItStartsCreatesNoIndicatorAndThrows()
    {
        var factory = new CountingFactory();
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: factory.Mock.Object);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CalculateIndicatorsAsync(Candles(), ThreeIndependent(), null, cancelled.Token));

        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task ACancelledCallThrowsAlsoWhenThereIsNothingToCalculate()
    {
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CalculateIndicatorsAsync(new List<CoreCandleData>(), ThreeIndependent(), null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CalculateIndicatorsAsync(Candles(), new List<CoreIndicatorSettings>(), null, cancelled.Token));
    }

    [Fact]
    public async Task ThePlainCallWithNothingToCalculateStillReturnsAnEmptyResult()
    {
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default);

        Assert.Empty(await ((IAnalysisPipelineService)service).CalculateIndicatorsAsync(new List<CoreCandleData>(), ThreeIndependent()));
    }

    [Fact]
    public async Task ACallCancelledWhileTheFirstIndicatorRunsStartsNoFurtherIndicatorAndGivesNoCrashResult()
    {
        using var source = new CancellationTokenSource();
        var factory = new CountingFactory(number => { if (number == 1) source.Cancel(); });
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: factory.Mock.Object);
        List<CoreIndicatorSettings> settings = ThreeIndependent();

        // If the check sat inside the per-indicator try/catch it would become a "Crash:" result and the call would finish: the exception must come out.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CalculateIndicatorsAsync(Candles(), settings, null, source.Token));

        Assert.Equal(1, factory.Created);
        Assert.DoesNotContain(settings, s => s.ErrorMessage is { } message && message.StartsWith("Crash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutACancellationEveryIndicatorIsStarted()
    {
        var factory = new CountingFactory();
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: factory.Mock.Object);
        using var never = new CancellationTokenSource();

        Dictionary<string, IIndicatorResult> result = await service.CalculateIndicatorsAsync(Candles(), ThreeIndependent(), null, never.Token);

        Assert.Equal(3, factory.Created);
        Assert.All(result.Values, r => Assert.True(r.IsSuccessful));
    }

    [Fact]
    public async Task ThePlainCallIsNotAffectedByTheNewMethod()
    {
        var factory = new CountingFactory();
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: factory.Mock.Object);

        Dictionary<string, IIndicatorResult> result = await ((IAnalysisPipelineService)service).CalculateIndicatorsAsync(Candles(), ThreeIndependent());

        Assert.Equal(3, factory.Created);
        Assert.Equal(3, result.Count);
    }
}
