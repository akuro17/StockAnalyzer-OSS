using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Integration-level proof (through the real, public <see cref="BacktestEngine.Run"/> entry point) for
/// Task 6a of Y:\Temp\sa_implementation_plan_BacktestOutputNameSupport.md: <see cref="StrategyIndicatorRequest.OutputName"/>
/// must select the named series of a multi-series indicator's <see cref="IIndicatorResult"/>, in both the
/// same-Frame and foreign-Frame branches of <c>BacktestEngine.PrepareIndicators</c>, and a request that
/// omits it (or that names a series the result does not have) must fall back to the Main series exactly as
/// before this field existed (existing-behavior-preservation regression guard).
/// </summary>
public class BacktestEngineOutputNameTests
{
    private static readonly DateTime DailyStart = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); // Monday

    private static CandleData DailyBar(int dayOffset, decimal close)
        => new(DailyStart.AddDays(dayOffset), close, close, close, close, 1000);

    private static CandleData WeeklyBar(int weekOffset, decimal close)
        => new(DailyStart.AddDays(weekOffset * 7), close, close, close, close, 1000);

    /// <summary>Deterministic fake exposing two distinct named series: "Main" = each candle's own Close,
    /// "Signal" = Close + 100 (deliberately far apart so a test reading the wrong series is unmistakable).</summary>
    private sealed class MultiSeriesIndicator : ICoreIndicator
    {
        public string Name => "MultiSeries";
        public IReadOnlyList<decimal?> Values { get; private set; } = Array.Empty<decimal?>();
        public void Configure(CoreIndicatorParameterBase parameters) { }

        public IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles)
        {
            var mainValues = new List<decimal?>(candles.Count);
            var signalValues = new List<decimal?>(candles.Count);
            foreach (CoreCandleData candle in candles)
            {
                mainValues.Add(candle.Close);
                signalValues.Add(candle.Close + 100m);
            }
            Values = mainValues;
            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                [IndicatorResult.MainSeriesName] = mainValues,
                ["Signal"] = signalValues,
            });
        }

        public IIndicatorResult CalculateSeries(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
            => IndicatorResult.Success(series);

        public Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
            => Task.FromResult(Calculate(candles));

        public CoreIndicatorSettings GetDefaultSettings() => new();
    }

    private sealed class FakeIndicatorFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => new MultiSeriesIndicator();
        public bool IsRegistered(IndicatorType type) => true;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => new[] { IndicatorType.MACD };
    }

    /// <summary>Never trades; only records each bar's indicator reading so the test can assert on which series was read.</summary>
    private sealed class RecordingStrategy : IBacktestStrategy
    {
        private readonly IReadOnlyList<StrategyIndicatorRequest> _requests;
        public string Name => "Recording";
        public List<decimal?> Recorded { get; } = new();

        public RecordingStrategy(IReadOnlyList<StrategyIndicatorRequest> requests) => _requests = requests;

        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => _requests;

        public StrategyOrderRequest? Evaluate(StrategyContext context)
        {
            Recorded.Add(context.Indicators.ValueAt(_requests[0].Key, context.BarIndex));
            return null;
        }
    }

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    [Fact]
    public void SameFrameRequest_OutputNameOmitted_MatchesMainSeriesExactly()
    {
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(DailyBar(0, 10m), DailyBar(1, 20m));
        var input = new BacktestInput(dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0);

        var requests = new[] { new StrategyIndicatorRequest("macd", IndicatorType.MACD, null) }; // OutputName omitted -> "Main"
        var strategy = new RecordingStrategy(requests);

        new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(new decimal?[] { 10m, 20m }, strategy.Recorded);
    }

    [Fact]
    public void SameFrameRequest_OutputNameSignal_ExtractsSignalSeriesNotMain()
    {
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(DailyBar(0, 10m), DailyBar(1, 20m));
        var input = new BacktestInput(dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0);

        var requests = new[] { new StrategyIndicatorRequest("macd-signal", IndicatorType.MACD, null, null, "Signal") };
        var strategy = new RecordingStrategy(requests);

        new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(new decimal?[] { 110m, 120m }, strategy.Recorded);
    }

    [Fact]
    public void ForeignFrameRequest_OutputNameSignal_ExtractsSignalSeriesBeforeAlignment()
    {
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(
            DailyBar(0, 1m), DailyBar(1, 1m), DailyBar(6, 1m), // still inside week 1
            DailyBar(7, 1m));                                   // week 2 -> week 1 now closed

        // Two weekly bars needed: TimeframeAlignment only treats week 1 as "closed" once week 2's own
        // bar exists (it steps back one index from the period containing the current base bar).
        ImmutableArray<CandleData> weeklyBars = ImmutableArray.Create(WeeklyBar(0, 50m), WeeklyBar(1, 999m));

        var input = new BacktestInput(
            dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(30), 0, 0,
            additionalTimeframeBars: new Dictionary<TimeFrame, ImmutableArray<CandleData>> { [TimeFrame.W1] = weeklyBars });

        var requests = new[] { new StrategyIndicatorRequest("weekly-macd-signal", IndicatorType.MACD, null, TimeFrame.W1, "Signal") };
        var strategy = new RecordingStrategy(requests);

        new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        // Week 1's weekly bar has Close=50 -> Signal series = 150 (Close + 100), visible once week 1 closes.
        Assert.Equal(150m, strategy.Recorded[3]);
    }
}
