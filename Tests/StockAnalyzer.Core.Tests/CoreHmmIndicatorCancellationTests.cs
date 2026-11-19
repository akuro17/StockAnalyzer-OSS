#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// HMM cancellation (WebAI gap review T8). The expected values below were captured from the indicator BEFORE the cancellation hook existed
/// (format "period|states|barCount|values after the warm-up", invariant culture; the first <c>period</c> bars are the null warm-up),
/// so the no-token paths are proven value-identical to the pre-change code.
/// No test asserts elapsed time (a CancelAfter race is unreliable under thread-pool starvation); cancellation is always requested before the call or at a deterministic hook.
/// </summary>
public sealed class CoreHmmIndicatorCancellationTests
{
    private static readonly string[] PreChangeCapture =
    {
        "20|2|70|98.6481,99.8784,100.0000,100.0000,97.8785,100.0000,12.5798,1.0874,5.3889,41.2570,3.9672,99.9662,5.4043,10.5127,100.0000,100.0000,100.0,0.0000,0.0005,52.3785,0.0000,0.0,0.0000,10.7028,0.0008,79.8994,94.0928,99.7687,99.9846,100.0000,33.3314,99.9472,99.9590,100.0000,99.9947,43.0861,99.9840,100.0000,100.0000,100.0000,100.0000,100.0,28.0906,97.8893,25.1952,30.0875,0.0,12.4383,0.0000,100.0000",
        "20|3|70|77.2219,0.0000,83.1032,99.9994,99.9964,99.9993,15.8887,0.1477,2.7506,30.7688,3.8773,70.7408,4.2870,0.2440,99.9960,90.8323,99.9540,0.0005,0.0001,0.0005,0.0000,0.0000,0.0000,0.0335,0.0,18.8541,19.4753,100.0000,99.9255,99.8809,0.0000,14.2598,0.6753,0.0000,48.9372,0.2971,21.1700,0.0002,33.1268,51.1131,99.9628,99.9999,0.6546,0.0042,0.0868,0.0568,0.0,0.4410,0.0000,48.8698",
        "100|2|160|9.9791,0.7961,6.8821,95.6488,65.0810,45.5291,13.2409,34.8005,98.2286,32.6701,96.9380,99.5891,46.0106,59.2440,4.2288,18.8997,99.4903,98.9498,0.8066,51.4034,4.2902,64.8579,67.2749,99.0837,59.0747,23.3191,99.3036,98.3604,96.3185,0.1042,0.3296,46.7183,46.4050,70.3112,62.9958,0.4706,82.8802,36.2997,91.5328,85.9322,3.7982,80.5294,19.0272,9.6082,55.8481,0.0036,18.2807,0.1174,0.0974,42.8664,31.5470,91.3833,44.0589,10.3788,10.5146,26.6912,3.8435,7.8648,1.2985,0.2455",
        "100|3|160|2.0586,0.0552,2.3258,84.8547,17.1058,13.3680,2.8747,11.8657,61.0978,20.4296,54.3161,67.2545,13.4179,14.4059,0.0340,0.8204,37.4166,36.2362,5.9611,14.5810,0.1811,14.9505,7.1134,50.6721,0.9690,17.6353,47.1352,35.0174,24.3032,0.0000,0.0000,9.0528,17.2657,14.7091,2.7672,0.0002,45.3403,0.2174,14.7739,0.7424,0.0002,1.3549,0.0013,0.0000,0.0000,0.0000,0.0000,0.0000,0.0000,0.0000,0.0000,0.0000,0.0012,0.0004,0.0006,1.1427,0.0000,0.0000,0.0669,0.0701",
    };

    private static List<CoreCandleData> Candles(int count)
    {
        var candles = new List<CoreCandleData>(count);
        var date = new DateTime(2023, 1, 1);
        var rand = new Random(4242);
        decimal price = 100m;
        for (int i = 0; i < count; i++)
        {
            double drift = (i < count / 2) ? 0.002 : -0.002;
            double shock = (rand.NextDouble() - 0.5) * 0.02;
            price *= (decimal)(1.0 + drift + shock);
            if (price <= 1.0m) price = 1.0m;
            candles.Add(new CoreCandleData(date.AddDays(i), price, price * 1.01m, price * 0.99m, price, 10000));
        }
        return candles;
    }

    private static (int Period, int States, int Count, decimal?[] Expected) Parse(string line)
    {
        string[] parts = line.Split('|');
        int period = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int count = int.Parse(parts[2], CultureInfo.InvariantCulture);
        decimal?[] tail = parts[3].Split(',').Select(token => (decimal?)decimal.Parse(token, CultureInfo.InvariantCulture)).ToArray();
        decimal?[] values = Enumerable.Repeat((decimal?)null, period).Concat(tail).ToArray();
        return (period, int.Parse(parts[1], CultureInfo.InvariantCulture), count, values);
    }

    public static IEnumerable<object[]> CaptureIndexes() => Enumerable.Range(0, PreChangeCapture.Length).Select(i => new object[] { i });

    private static CoreHmmIndicator Create(int period, int states) => new() { Period = period, States = states };

    [Fact]
    public void CaptureCoversPeriodsAndStatesNamedByThePlan()
    {
        var parsed = PreChangeCapture.Select(Parse).ToArray();

        Assert.Equal(new[] { (20, 2), (20, 3), (100, 2), (100, 3) }, parsed.Select(c => (c.Period, c.States)).ToArray());
        Assert.All(parsed, c => Assert.Equal(c.Count, c.Expected.Length));
    }

    [Theory]
    [MemberData(nameof(CaptureIndexes))]
    public void SynchronousCalculateEqualsTheValuesCapturedBeforeTheChange(int index)
    {
        var (period, states, count, expected) = Parse(PreChangeCapture[index]);

        var result = Create(period, states).Calculate(Candles(count));

        Assert.True(result.IsSuccessful);
        Assert.Equal(expected, result.MainValues);
    }

    [Theory]
    [MemberData(nameof(CaptureIndexes))]
    public async Task AsyncCalculateWithAContextWithoutACancellableTokenEqualsTheCapture(int index)
    {
        var (period, states, count, expected) = Parse(PreChangeCapture[index]);

        var plain = await Create(period, states).CalculateAsync(Candles(count), new CoreExecutionContext());
        var neverCancellable = await Create(period, states).CalculateAsync(Candles(count), new CoreExecutionContext(null, CancellationToken.None));

        Assert.Equal(expected, plain.MainValues);
        Assert.Equal(expected, neverCancellable.MainValues);
    }

    [Theory]
    [MemberData(nameof(CaptureIndexes))]
    public async Task AsyncCalculateWithANotCancelledTokenEqualsTheCapture(int index)
    {
        var (period, states, count, expected) = Parse(PreChangeCapture[index]);
        using var source = new CancellationTokenSource();

        var result = await Create(period, states).CalculateAsync(Candles(count), new CoreExecutionContext(null, source.Token));

        Assert.True(result.IsSuccessful);
        Assert.Equal(expected, result.MainValues);
    }

    [Fact]
    public async Task APreCancelledTokenThrowsInsteadOfReturningAFailureResult()
    {
        var (period, states, count, _) = Parse(PreChangeCapture[0]);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(period, states).CalculateAsync(Candles(count), new CoreExecutionContext(null, source.Token)));
    }

    [Fact]
    public async Task AnInstanceIsReusableAfterACancelledCall()
    {
        var (period, states, count, expected) = Parse(PreChangeCapture[1]);
        var indicator = Create(period, states);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indicator.CalculateAsync(Candles(count), new CoreExecutionContext(null, cancelled.Token)));
        var afterwards = await indicator.CalculateAsync(Candles(count), new CoreExecutionContext(null, CancellationToken.None));
        var again = indicator.Calculate(Candles(count));

        Assert.Equal(expected, afterwards.MainValues);
        Assert.Equal(expected, again.MainValues);
    }

    [Fact]
    public async Task TheTokenIsNotKeptOnTheInstanceBetweenCalls()
    {
        var (period, states, count, expected) = Parse(PreChangeCapture[0]);
        var indicator = Create(period, states);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indicator.CalculateAsync(Candles(count), new CoreExecutionContext(null, cancelled.Token)));

        // A plain context carries no token, so the earlier cancelled token must not leak into this call.
        var plain = await indicator.CalculateAsync(Candles(count), new CoreExecutionContext());

        Assert.Equal(expected, plain.MainValues);
    }

    [Fact]
    public async Task NullAndEmptyCandlesKeepTheBaseWrapperResultOnTheCancellablePath()
    {
        using var source = new CancellationTokenSource();
        var context = new CoreExecutionContext(null, source.Token);
        var cancellable = Create(20, 2);
        var baseline = Create(20, 2);

        var nullResult = await cancellable.CalculateAsync(null!, context);
        var nullBaseline = baseline.Calculate(null!);
        var emptyResult = await cancellable.CalculateAsync(new List<CoreCandleData>(), context);
        var emptyBaseline = baseline.Calculate(new List<CoreCandleData>());

        Assert.False(nullResult.IsSuccessful);
        Assert.Equal(nullBaseline.ErrorMessage, nullResult.ErrorMessage);
        Assert.Equal(emptyBaseline.IsSuccessful, emptyResult.IsSuccessful);
        Assert.Equal(emptyBaseline.MainValues, emptyResult.MainValues);
    }

    [Fact]
    public async Task ShortInputWithACancellableTokenStillReturnsAllNullWarmUp()
    {
        using var source = new CancellationTokenSource();

        var result = await Create(20, 2).CalculateAsync(Candles(20), new CoreExecutionContext(null, source.Token));

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, result.MainValues.Count);
        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    private static CoreIndicatorSettings Hmm(IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart) => new()
    {
        Id = "hmm",
        IsEnabled = true,
        TypeEnum = IndicatorType.HiddenMarkovModel,
        ParameterObject = new CoreHmmParameter { Period = 10, States = 2 },
        Timeframe = timeframe,
    };

    /// <summary>A pipeline whose factory cancels the source when the indicator is created, i.e. cancellation arrives while the indicator is about to run.</summary>
    private static AnalysisPipelineService PipelineCancellingOnCreate(CancellationTokenSource source)
    {
        var mock = new Mock<IIndicatorFactory>();
        mock.Setup(f => f.Create(It.IsAny<IndicatorType>(), It.IsAny<CoreIndicatorParameterBase?>()))
            .Returns<IndicatorType, CoreIndicatorParameterBase?>((type, parameters) =>
            {
                source.Cancel();
                return IndicatorFactory.Default.Create(type, parameters);
            });
        return new AnalysisPipelineService(pythonService: null, indicatorFactory: mock.Object);
    }

    [Fact]
    public async Task PipelineCancelledWhileTheOnlyHmmRunsThrowsOnTheDirectPath()
    {
        using var source = new CancellationTokenSource();
        ICancellableIndicatorCalculation service = PipelineCancellingOnCreate(source);
        var settings = new List<CoreIndicatorSettings> { Hmm() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CalculateIndicatorsAsync(Candles(120), settings, null, source.Token));

        Assert.DoesNotContain(settings, s => s.ErrorMessage is { } message && (message.StartsWith("Crash", StringComparison.Ordinal) || message.StartsWith("Calculation error", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PipelineCancelledWhileTheOnlyHmmRunsThrowsOnTheAlignedTimeframePath()
    {
        using var source = new CancellationTokenSource();
        ITimeFrameAwareIndicatorCalculation service = PipelineCancellingOnCreate(source);
        var settings = new List<CoreIndicatorSettings> { Hmm(IndicatorTimeframe.Weekly) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CalculateIndicatorsAsync(Candles(240), settings, null, TimeFrame.D1, source.Token));

        Assert.DoesNotContain(settings, s => s.ErrorMessage is { } message && (message.StartsWith("Crash", StringComparison.Ordinal) || message.StartsWith("Calculation error", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PipelineWithANotCancelledTokenEqualsThePlainCallForHmm()
    {
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default);
        using var never = new CancellationTokenSource();

        var plain = await ((IAnalysisPipelineService)service).CalculateIndicatorsAsync(Candles(120), new List<CoreIndicatorSettings> { Hmm() });
        var aware = await ((ICancellableIndicatorCalculation)service).CalculateIndicatorsAsync(Candles(120), new List<CoreIndicatorSettings> { Hmm() }, null, never.Token);

        Assert.True(plain["hmm"].IsSuccessful);
        Assert.True(aware["hmm"].IsSuccessful);
        Assert.Equal(plain["hmm"].MainValues, aware["hmm"].MainValues);
    }
}
