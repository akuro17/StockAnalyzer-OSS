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
/// Integration-level proof (through the real, public <see cref="BacktestEngine.Run"/> entry point,
/// not by reaching into the private PrepareIndicators method) for Task 2b of
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md: same-Frame requests must produce
/// byte-for-byte identical output to the pre-existing behavior (regression guard), a request naming a
/// Frame missing from <see cref="BacktestInput.AdditionalTimeframeBars"/> must fail loudly with a named
/// exception rather than silently returning nulls, and a genuine cross-timeframe request must expose
/// the same causal-forward-fill timing proven in isolation by <see cref="TimeframeAlignmentTests"/>.
/// </summary>
public class BacktestEngineCrossTimeframeTests
{
    private static readonly DateTime DailyStart = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); // Monday

    private static CandleData DailyBar(int dayOffset, decimal close)
        => new(DailyStart.AddDays(dayOffset), close, close, close, close, 1000);

    private static CandleData WeeklyBar(int weekOffset, decimal close)
        => new(DailyStart.AddDays(weekOffset * 7), close, close, close, close, 1000);

    /// <summary>Deterministic fake: "computes" an indicator by returning each candle's own Close price
    /// unchanged. Makes the resulting indicator series trivially comparable against the input bars in
    /// assertions, without depending on any specific real indicator's math.</summary>
    private sealed class CloseValueIndicator : ICoreIndicator
    {
        public string Name => "CloseValue";
        public IReadOnlyList<decimal?> Values { get; private set; } = Array.Empty<decimal?>();
        public void Configure(CoreIndicatorParameterBase parameters) { }

        public IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles)
        {
            var values = new List<decimal?>(candles.Count);
            foreach (CoreCandleData candle in candles) values.Add(candle.Close);
            Values = values;
            return IndicatorResult.Success(values);
        }

        public IIndicatorResult CalculateSeries(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
            => IndicatorResult.Success(series);

        public Task<IIndicatorResult> CalculateAsync(IReadOnlyList<CoreCandleData> candles, IExecutionContext context)
            => Task.FromResult(Calculate(candles));

        public CoreIndicatorSettings GetDefaultSettings() => new();
    }

    private sealed class FakeIndicatorFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => new CloseValueIndicator();
        public bool IsRegistered(IndicatorType type) => true;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => new[] { IndicatorType.SMA };
    }

    /// <summary>Never trades; only records each bar's indicator reading so the test can assert on timing.</summary>
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
    public void SameFrameRequest_NullFrame_MatchesUnalignedCloseValuesExactly()
    {
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(
            DailyBar(0, 10m), DailyBar(1, 20m), DailyBar(2, 30m));
        var input = new BacktestInput(dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0);

        var requests = new[] { new StrategyIndicatorRequest("close", IndicatorType.SMA, null) }; // Frame omitted -> null
        var strategy = new RecordingStrategy(requests);

        new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        // The regression guard: a null-Frame request is untouched by any alignment logic and must equal
        // the bar's own Close exactly, bar-for-bar, exactly as PrepareIndicators behaved before Task 2b.
        Assert.Equal(new decimal?[] { 10m, 20m, 30m }, strategy.Recorded);
    }

    [Fact]
    public void SameFrameRequest_ExplicitFrameEqualToRunFrame_MatchesUnalignedCloseValuesExactly()
    {
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(
            DailyBar(0, 10m), DailyBar(1, 20m), DailyBar(2, 30m));
        var input = new BacktestInput(dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0);

        var requests = new[] { new StrategyIndicatorRequest("close", IndicatorType.SMA, null, TimeFrame.D1) };
        var strategy = new RecordingStrategy(requests);

        new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(new decimal?[] { 10m, 20m, 30m }, strategy.Recorded);
    }

    [Fact]
    public void ForeignFrameRequest_MissingFromAdditionalTimeframeBars_ThrowsNamingTheFrame()
    {
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(DailyBar(0, 10m));
        var input = new BacktestInput(dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0); // AdditionalTimeframeBars omitted -> null

        var requests = new[] { new StrategyIndicatorRequest("weekly-close", IndicatorType.SMA, null, TimeFrame.W1) };
        var strategy = new RecordingStrategy(requests);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy));

        Assert.Contains("W1", ex.Message);
        Assert.Contains("weekly-close", ex.Message);
    }

    [Fact]
    public void ForeignFrameRequest_CausalForwardFill_DelaysVisibilityByOneClosedPeriod()
    {
        // Daily bars: week 1 = Mon(0)..Sun(6), week 2 starts at day 7.
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(
            DailyBar(0, 1m), DailyBar(1, 1m), DailyBar(6, 1m), // still inside week 1
            DailyBar(7, 1m), DailyBar(8, 1m));                  // week 2 has started -> week 1 now closed

        ImmutableArray<CandleData> weeklyBars = ImmutableArray.Create(WeeklyBar(0, 111m), WeeklyBar(1, 222m));

        var input = new BacktestInput(
            dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(30), 0, 0,
            additionalTimeframeBars: new Dictionary<TimeFrame, ImmutableArray<CandleData>> { [TimeFrame.W1] = weeklyBars });

        var requests = new[] { new StrategyIndicatorRequest("weekly-close", IndicatorType.SMA, null, TimeFrame.W1) };
        var strategy = new RecordingStrategy(requests);

        new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        // Week 1 is still forming for its own 3 daily bars -> null (never week 1's own 111m leaking early).
        Assert.Null(strategy.Recorded[0]);
        Assert.Null(strategy.Recorded[1]);
        Assert.Null(strategy.Recorded[2]);
        // The first daily bar of week 2 is exactly when week 1's value (111m) becomes visible.
        Assert.Equal(111m, strategy.Recorded[3]);
        Assert.Equal(111m, strategy.Recorded[4]);
    }
}
