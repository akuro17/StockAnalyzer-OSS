#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using StockAnalyzer.Core.Tests.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Opt-in measurement (MCP Part 2 external review, T8): what one backtest run with ATR dynamic exits allocates on the calling thread, before and after the per-bar
/// HashSet / key strings of <c>ConditionBasedBacktestStrategy</c> were replaced by arrays built once. 5,000 synthetic daily bars, an entry that is true from bar 30 on (after the ATR warm-up), an ATR stop,
/// an ATR target and an ATR trailing stop, so the strategy holds a position on most bars. Procedure: one warm-up run (discarded), then five measured runs; the report holds the
/// MEDIAN and the minimum of <see cref="GC.GetAllocatedBytesForCurrentThread"/> (the run is synchronous on the test thread). Runs only when <see cref="MeasureVariable"/> is set; run it
/// alone (<c>--filter</c>). No threshold is asserted: the test reports and checks that the run still trades.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class DynamicExitAllocationMeasurementTests
{
    private const string MeasureVariable = "SA_DYNAMIC_EXIT_MEASURE";
    private const int Bars = 5000;
    private const int WarmUpRuns = 1;
    private const int MeasuredRuns = 5;
    private const int AtrPeriod = 14;
    private const int FlatBars = 30; // below the entry level, so the first entry comes after the ATR warm-up
    private const decimal FlatPrice = 50m;
    private const decimal EntryLevel = 60m;
    private const decimal BasePrice = 100m;
    private const double SlowWaveAmplitude = 20.0;
    private const double SlowWaveBars = 40.0;      // bars per radian of the slow wave
    private const decimal DriftPerBar = 0.01m;
    private const double FastWaveAmplitude = 2.0;
    private const double FastWaveStep = 1.7;       // radians per bar of the fast wave
    private const int PriceDecimals = 2;
    private const decimal StopMultiple = 3m;
    private const decimal TargetMultiple = 4m;
    private const decimal TrailingMultiple = 5m;

    private readonly ITestOutputHelper _output;

    public DynamicExitAllocationMeasurementTests(ITestOutputHelper output) => _output = output;

    [EnvironmentFact(MeasureVariable)]
    public void T8_AllocationsOfARunWithAtrDynamicExits()
    {
        var table = new (decimal Open, decimal High, decimal Low, decimal Close)[Bars];
        decimal previous = FlatPrice;
        for (int i = 0; i < Bars; i++)
        {
            decimal close = i < FlatBars ? FlatPrice : Math.Round(BasePrice + (decimal)(SlowWaveAmplitude * Math.Sin(i / SlowWaveBars)) + (i * DriftPerBar) + (decimal)(FastWaveAmplitude * Math.Sin(i * FastWaveStep)), PriceDecimals);
            decimal high = Math.Max(previous, close) + 1m;
            decimal low = Math.Min(previous, close) - 1m;
            table[i] = (previous, high, low, close);
            previous = close;
        }

        var bars = SyntheticBars.FromTable(table);
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = EntryLevel,
            Role = BacktestConditionRole.EntryOnly,
            Position = TradeSide.Long,
        };
        var risk = new BacktestRiskManagementSettings
        {
            DynamicExits = new DynamicExitConfiguration
            {
                AtrStop = new AtrExitParameters { Period = AtrPeriod, Multiple = StopMultiple },
                AtrTarget = new AtrExitParameters { Period = AtrPeriod, Multiple = TargetMultiple },
                TrailingStop = new TrailingExitParameters { Mode = TrailingExitMode.AtrMultiple, Period = AtrPeriod, Multiple = TrailingMultiple },
            },
        };
        BacktestInput input = VerificationHarness.MakeInput(bars);
        BacktestConfiguration config = VerificationHarness.MakeConfig();

        var bytes = new List<long>();
        int trades = 0;
        int warnings = 0;
        for (int run = 0; run < WarmUpRuns + MeasuredRuns; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();

            BacktestResult result = VerificationHarness.Run(input, config, new ConditionBasedBacktestStrategy(new[] { entry }, risk));

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            trades = result.Trades.Length;
            warnings = result.Warnings.Length;
            if (run >= WarmUpRuns)
            {
                bytes.Add(allocated);
            }
        }

        long[] ordered = bytes.OrderBy(value => value).ToArray();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"T8 {Bars} bars, ATR({AtrPeriod}) stop/target/trailing, {WarmUpRuns} warm-up + {MeasuredRuns} runs: trades {trades}, warnings {warnings}, allocated bytes (current thread) median {ordered[ordered.Length / 2]:N0}, min {ordered[0]:N0}"));
        Assert.True(trades > 1, "the scenario must trade, otherwise the dynamic exits are never exercised");
    }
}
