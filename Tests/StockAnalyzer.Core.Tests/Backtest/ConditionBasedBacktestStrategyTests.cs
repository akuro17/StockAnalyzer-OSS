using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Task 3 proof (plan section 4.3 of Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md):
/// <see cref="ConditionBasedBacktestStrategy"/> correctly sequences entry/exit signals per
/// <see cref="BacktestConditionRole"/>, dedupes indicator requests by content, and produces the
/// expected delayed-visibility signal timing for a cross-timeframe (Task 2b) condition — all through
/// the real, public <see cref="BacktestEngine.Run"/> entry point, not by reaching into private engine
/// internals.
/// </summary>
public class ConditionBasedBacktestStrategyTests
{
    private static readonly DateTime DailyStart = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); // Monday

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close)
        => new(DailyStart.AddDays(dayOffset), open, high, low, close, 1000);

    private static CandleData FlatBar(int dayOffset, decimal close) => Bar(dayOffset, close, close, close, close);

    private static CandleData WeeklyBar(int weekOffset, decimal close)
        => new(DailyStart.AddDays(weekOffset * 7), close, close, close, close, 1000);

    /// <summary>Deterministic fake: "computes" an indicator by returning each candle's own Close price
    /// unchanged, so the resulting series is trivially comparable against crafted bars in assertions.</summary>
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

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static BacktestConditionSide CloseSide(TimeFrame? frame = null, string outputName = IndicatorResult.MainSeriesName, PriceType? priceSource = null) => new()
    {
        IndicatorType = IndicatorType.SMA,
        Parameters = null,
        Frame = frame,
        OutputName = outputName,
        PriceSource = priceSource,
    };

    [Fact]
    public void Constructor_NullEntries_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConditionBasedBacktestStrategy(null!));
    }

    [Fact]
    public void Constructor_RiskRatioBoundaries_RejectMinusOneAndOne_ButAcceptZero()
    {
        var condition = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
        };

        _ = new ConditionBasedBacktestStrategy(
            new[] { condition },
            new BacktestRiskManagementSettings { StopLossPercent = 0m, TakeProfitPercent = 0m });

        Assert.Throws<ArgumentOutOfRangeException>(() => new ConditionBasedBacktestStrategy(
            new[] { condition },
            new BacktestRiskManagementSettings { StopLossPercent = -1m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConditionBasedBacktestStrategy(
            new[] { condition },
            new BacktestRiskManagementSettings { TakeProfitPercent = 1m }));
    }

    [Fact]
    public void EntryOnlyAndExitOnlyRoles_FireOnlyInTheirOwnChain()
    {
        // close: bar0=1 (no entry, entry cond is Close>10), bar1=20 (entry cond true -> LongEntry decided
        // at bar1, fills at bar2's Open=110), bar2=20 (exit cond Close<5 false -> still holding),
        // bar3=2 (exit cond true -> LongExit decided at bar3, fills at bar4's Open=90), bar4=2 (flat again,
        // entry cond false).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 110m, 115m, 15m, 20m),  // fill bar for the entry: Open=110 is the expected EntryPrice
            FlatBar(3, 2m),
            Bar(4, 90m, 95m, 1m, 2m));      // fill bar for the exit: Open=90 is the expected ExitPrice
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0);

        var entryOnly = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.EntryOnly,
        };
        var exitOnly = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 5m,
            Role = BacktestConditionRole.ExitOnly,
        };
        var strategy = new ConditionBasedBacktestStrategy(new[] { entryOnly, exitOnly });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(110m, trade.EntryPrice); // bar2's Open
        Assert.Equal(90m, trade.ExitPrice);   // bar4's Open
    }

    [Fact]
    public void BothRole_SingleConditionDrivesEntryThenImmediateExit()
    {
        // A Role.Both condition participates in both chains: once it goes true it both enters (while
        // flat) and, on the very next opportunity while holding, exits again (same boolean) - this is
        // the exact dual-chain wiring plan section 4.3 describes, not a bug.
        // close: bar0=1 (false), bar1=20 (true -> flat -> LongEntry decided at bar1, fills bar2 Open=110),
        // bar2=20 (true, now holding -> exit chain also true -> LongExit decided at bar2, fills bar3 Open=90),
        // bar3=1 (flat again, condition false -> no new entry).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 110m, 115m, 15m, 20m), // fill bar for the entry: Open=110 is the expected EntryPrice
            Bar(3, 90m, 95m, 1m, 1m));     // fill bar for the exit: Open=90 is the expected ExitPrice
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0);

        var bothRole = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.Both,
        };
        var strategy = new ConditionBasedBacktestStrategy(new[] { bothRole });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(110m, trade.EntryPrice); // bar2's Open
        Assert.Equal(90m, trade.ExitPrice);   // bar3's Open
    }

    [Fact]
    public void ReversalRole_TwoOppositeConditions_ProduceARealSameBarReversal_ThroughTheRealEngine()
    {
        // End-to-end proof (Task 3, Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md)
        // that ConditionBasedBacktestStrategy's Reversal-role chains and BacktestEngine's dual pending-order
        // slots work together through the real, public BacktestEngine.Run entry point - not a hand-scripted
        // strategy double. Two Reversal entries, opposite Position: "Close>10"/Position=Long opens/holds
        // Long, "Close<5"/Position=Short opens/holds Short.
        // close: bar0=7 (both conditions false, flat - deliberately between 5 and 10), bar1=20 (Long cond
        // true -> flat -> LongEntry decided bar1, fills bar2 Open=110), bar2=20 (Long cond still true,
        // Short cond false, holding Long -> same side as its own Position -> no reversal, no exit - stays
        // Long, exactly section 2.2's "behaves like EntryOnly" case), bar3=2 (Long cond false, Short cond
        // true, holding Long -> Short cond is the OPPOSITE side -> reversal decided at bar3:
        // Evaluate->ShortEntry, EvaluateExit->LongExit, both fill at bar4 Open=90 with Exit processed
        // first per section 2.4).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 7m),
            FlatBar(1, 20m),
            Bar(2, 110m, 115m, 15m, 20m), // fill bar for the entry: Open=110 is the expected EntryPrice
            FlatBar(3, 2m),
            Bar(4, 90m, 95m, 1m, 2m));     // fill bar for the reversal pair: Open=90 for both legs
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(10), 0, 0);

        var reversalLong = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.Reversal,
            Position = TradeSide.Long,
        };
        var reversalShort = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 5m,
            Role = BacktestConditionRole.Reversal,
            Position = TradeSide.Short,
        };
        var strategy = new ConditionBasedBacktestStrategy(new[] { reversalLong, reversalShort });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade closedLongTrade = Assert.Single(result.Trades); // the Long round-trip the reversal closed
        Assert.Equal(TradeSide.Long, closedLongTrade.Side);
        Assert.Equal(110m, closedLongTrade.EntryPrice); // bar2's Open
        Assert.Equal(90m, closedLongTrade.ExitPrice);   // bar4's Open - Exit fills before the new Entry
        Assert.Equal(3, result.Fills.Length); // LongEntry@bar2, then (Exit-before-Entry) LongExit@bar4, ShortEntry@bar4
    }

    // ===== Task 8b: entry-price-relative Stop-Loss/Take-Profit (Y:\Temp\sa_implementation_plan_
    // BacktestComparisonSignals.md sections 3.4/4.5) =====

    /// <summary>Single EntryOnly/Long condition shared by every risk-management test below - opens a Long
    /// position at whatever the fill bar's Open is, with no exit-chain/reversal contribution of its own,
    /// so the ONLY way the position can ever close is via the risk-management breach check itself.</summary>
    private static BacktestConditionEntry LongEntryAbove10() => new()
    {
        Left = CloseSide(),
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 10m,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Long,
    };

    private static BacktestConditionEntry ShortEntryAbove10() => new()
    {
        Left = CloseSide(),
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 10m,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Short,
    };

    [Fact]
    public void StopLoss_TriggersOnTheBarWhoseLowBreachesTheThreshold_NotCloseBased_NotOffByOne()
    {
        // Entry fills bar2 @ Open=100 -> Stop-Loss threshold = 100*(1-0.05) = 95. Bar2 itself stays safe
        // (Low=99 > 95). Bar3's Low=90 breaches (<=95) while its Close=98 does NOT (98>95) - a Close-based
        // check would wrongly let this bar "survive" un-stopped, proving detection is Low-based. Bar4 is
        // the fill bar for the Stop order; Open=96 is on the safe side of the threshold (no gap), so the
        // fill is the literal threshold price, 95.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 100m, 102m, 99m, 100m),
            Bar(3, 100m, 101m, 90m, 98m),
            Bar(4, 96m, 97m, 95m, 96m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, DailyStart, DailyStart.AddDays(10), 0, 0);

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { LongEntryAbove10() },
            new BacktestRiskManagementSettings { StopLossPercent = 0.05m });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Long, trade.Side);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(95m, trade.ExitPrice);
        Assert.Contains(result.Orders, o => o.Type == OrderType.Stop && o.StopPrice == 95m);
    }

    [Fact]
    public void TakeProfit_TriggersOnTheBarWhoseHighBreachesTheThreshold_NotCloseBased()
    {
        // Mirror of the Stop-Loss test above: threshold = 100*(1+0.10) = 110, detected via High (bar3's
        // High=112 breaches while its Close=105 does not), fill bar's Open=108 is on the safe side (no
        // gap for a Take-Profit close), so the fill is the literal threshold price, 110.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 100m, 102m, 99m, 100m),
            Bar(3, 101m, 112m, 100m, 105m),
            Bar(4, 108m, 111m, 107m, 110m)); // High=111 must reach the 110 threshold for the Limit order to fill
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, DailyStart, DailyStart.AddDays(10), 0, 0);

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { LongEntryAbove10() },
            new BacktestRiskManagementSettings { TakeProfitPercent = 0.10m });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Contains(result.Orders, o => o.Type == OrderType.Limit && o.LimitPrice == 110m);
    }

    [Fact]
    public void SameBarDoubleBreach_StopLossAlwaysWinsOverTakeProfit()
    {
        // Section 3.4.3's RESOLVED tie-break: bar3's range (Low=90, High=112) breaches BOTH the
        // Stop-Loss threshold (95) and the Take-Profit threshold (110) at once. The exit must resolve as
        // the Stop-Loss's price (95, via the bar4 fill), never the Take-Profit's (110).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 100m, 102m, 99m, 100m),
            Bar(3, 100m, 112m, 90m, 102m),
            Bar(4, 96m, 97m, 95m, 96m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, DailyStart, DailyStart.AddDays(10), 0, 0);

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { LongEntryAbove10() },
            new BacktestRiskManagementSettings { StopLossPercent = 0.05m, TakeProfitPercent = 0.10m });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(95m, trade.ExitPrice);
        Assert.DoesNotContain(result.Orders, o => o.Type == OrderType.Limit);
    }

    [Fact]
    public void StopLoss_FillPriceIsGapCorrected_WhenTheFillBarOpensThroughTheThreshold()
    {
        // Task 8a's finding (already-proven engine behavior, section 3.4.2): once the Stop-Loss order is
        // decided at bar3 (threshold=95), bar4 gaps down and opens at 90 - already past the threshold
        // before the bar even opens. The fill must be the WORSE of {95, 90} = 90 (the actual gapped-down
        // open), never the naive literal threshold of 95.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 100m, 102m, 99m, 100m),
            Bar(3, 100m, 101m, 90m, 98m),
            Bar(4, 90m, 92m, 88m, 90m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, DailyStart, DailyStart.AddDays(10), 0, 0);

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { LongEntryAbove10() },
            new BacktestRiskManagementSettings { StopLossPercent = 0.05m });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(90m, trade.ExitPrice); // gap-corrected, not the literal 95 threshold
    }

    [Fact]
    public void StopLoss_ShortPosition_TriggersOnHighBreach_WithGapCorrectedFill()
    {
        // Mirror of the Long Stop-Loss case for a Short position (section 3.4.1): threshold =
        // 100*(1+0.05) = 105, breached via High (bar3's High=112 vs. Close=102, proving High-based not
        // Close-based), and bar4 gaps UP through the threshold (Open=110) - worse-of for a Buy-to-cover is
        // the HIGHER of {105, 110} = 110.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 100m, 101m, 98m, 100m),
            Bar(3, 100m, 112m, 99m, 102m),
            Bar(4, 110m, 111m, 108m, 110m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, DailyStart, DailyStart.AddDays(10), 0, 0);

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { ShortEntryAbove10() },
            new BacktestRiskManagementSettings { StopLossPercent = 0.05m });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(110m, trade.ExitPrice);
    }

    [Fact]
    public void RiskManagement_DisabledOrBothPercentsNull_IsByteForByteIdenticalToNoRiskManagementAtAll()
    {
        // Regression guard (Task 8b's own required proof): a strategy built WITHOUT the optional
        // riskManagement constructor argument (defaults to null) must behave identically to one built
        // WITH an explicit settings object whose two percents are both left null (IsEnabled == false).
        // Bar3's wide range (Low=90, High=112) would breach both thresholds used elsewhere in this test
        // class if risk management were active - here it must be completely inert, leaving the Long
        // position open through the end of the run (no exit chain/reversal exists either).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 1m),
            FlatBar(1, 20m),
            Bar(2, 100m, 102m, 99m, 100m),
            Bar(3, 100m, 112m, 90m, 102m),
            Bar(4, 96m, 97m, 95m, 96m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, DailyStart, DailyStart.AddDays(10), 0, 0);

        var strategyWithoutParam = new ConditionBasedBacktestStrategy(new[] { LongEntryAbove10() });
        var strategyWithAllNullSettings = new ConditionBasedBacktestStrategy(new[] { LongEntryAbove10() }, new BacktestRiskManagementSettings());

        BacktestResult resultA = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategyWithoutParam);
        BacktestResult resultB = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategyWithAllNullSettings);

        Assert.Empty(resultA.Trades); // never closed - proves the wide bar3 breach had zero effect
        Assert.Empty(resultB.Trades);
        Assert.Equal(resultA.Fills.Length, resultB.Fills.Length);
        Assert.Equal(resultA.Fills[0].Price, resultB.Fills[0].Price);
        Assert.Equal(resultA.EquityPoints[^1].Equity, resultB.EquityPoints[^1].Equity);
    }

    [Fact]
    public void CrossTimeframeCondition_EntersOnlyAfterTheHigherTimeframePeriodCloses()
    {
        // Same causal forward-fill timing proven in isolation by TimeframeAlignmentTests/
        // BacktestEngineCrossTimeframeTests (Task 2b), now driving a real signal end-to-end: week 1's
        // value (111) only becomes visible on the first daily bar of week 2 (day index 3), never during
        // week 1 itself, so the strategy must not enter before then.
        ImmutableArray<CandleData> dailyBars = ImmutableArray.Create(
            FlatBar(0, 1m), FlatBar(1, 1m), FlatBar(6, 1m),                 // still inside week 1
            FlatBar(7, 1m),                                                  // week 2 started -> week 1's 111 now visible
            Bar(8, 50m, 55m, 45m, 52m));                                     // fill bar, distinctive Open=50

        ImmutableArray<CandleData> weeklyBars = ImmutableArray.Create(WeeklyBar(0, 111m), WeeklyBar(1, 222m));

        var input = new BacktestInput(
            dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            DailyStart, DailyStart.AddDays(30), 0, 0,
            additionalTimeframeBars: new Dictionary<TimeFrame, ImmutableArray<CandleData>> { [TimeFrame.W1] = weeklyBars });

        var entryOnly = new BacktestConditionEntry
        {
            Left = CloseSide(TimeFrame.W1),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 100m,
            Role = BacktestConditionRole.EntryOnly,
        };
        var strategy = new ConditionBasedBacktestStrategy(new[] { entryOnly });

        BacktestResult result = new BacktestEngine(new FakeIndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(50m, fill.Price); // bar index 4's (day 8) Open - the bar right after the visible entry signal
    }

    [Fact]
    public void GetRequiredIndicators_DedupesByContent_NotByEntryOrObjectIdentity()
    {
        // Two distinct BacktestConditionSide instances with identical (IndicatorType, Parameters, Frame)
        // content must collapse into a single StrategyIndicatorRequest (plan section 4.3's "dedupe by
        // content, not by entry" rule).
        var entryA = new BacktestConditionEntry { Left = CloseSide(), RightNumericValue = 1m };
        var entryB = new BacktestConditionEntry { Left = CloseSide(), RightNumericValue = 2m };
        var strategy = new ConditionBasedBacktestStrategy(new[] { entryA, entryB });

        Assert.Single(strategy.GetRequiredIndicators());
    }

    [Fact]
    public void NumericTarget_StaleRightIsPreservedButNeverRequestedOrValidatedAsActive()
    {
        var entry = new BacktestConditionEntry
        {
            Left = CloseSide(),
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Right = new BacktestConditionSide
            {
                IndicatorType = IndicatorType.ZigZag,
                Frame = TimeFrame.W1,
                Offset = -1,
            },
        };

        var strategy = new ConditionBasedBacktestStrategy(new[] { entry });

        StrategyIndicatorRequest request = Assert.Single(strategy.GetRequiredIndicators());
        Assert.Equal(IndicatorType.SMA, request.Type);
        Assert.True(request.StrictOutputName);
        Assert.NotNull(Assert.Single(strategy.Entries).Right);
    }

    [Fact]
    public void Constructor_ClonesParameterOwnership()
    {
        var parameters = new CoreSmaParameter { Period = 5 };
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Parameters = parameters },
            RightNumericValue = 10m,
        };
        var strategy = new ConditionBasedBacktestStrategy(new[] { entry });

        parameters.Period = 99;

        Assert.Equal(5, Assert.IsType<CoreSmaParameter>(Assert.Single(strategy.GetRequiredIndicators()).Parameters).Period);
        Assert.Equal(5, Assert.IsType<CoreSmaParameter>(Assert.Single(strategy.Entries).Left.Parameters).Period);
    }

    [Fact]
    public void GetRequiredIndicators_DifferentFrame_ProducesSeparateRequests()
    {
        var sameFrame = new BacktestConditionEntry { Left = CloseSide(), RightNumericValue = 1m };
        var foreignFrame = new BacktestConditionEntry { Left = CloseSide(TimeFrame.W1), RightNumericValue = 1m };
        var strategy = new ConditionBasedBacktestStrategy(new[] { sameFrame, foreignFrame });

        Assert.Equal(2, strategy.GetRequiredIndicators().Count);
    }

    [Fact]
    public void GetRequiredIndicators_DifferentOutputName_ProducesSeparateRequests()
    {
        // Task 6a (Y:\Temp\sa_implementation_plan_BacktestOutputNameSupport.md): two sides sharing
        // (IndicatorType, Parameters, Frame) but differing only by OutputName must NOT collapse into one
        // request - this is the corrected dedupe key restoring plan section 4.3's original spec.
        var mainSeries = new BacktestConditionEntry { Left = CloseSide(outputName: "Main"), RightNumericValue = 1m };
        var signalSeries = new BacktestConditionEntry { Left = CloseSide(outputName: "Signal"), RightNumericValue = 1m };
        var strategy = new ConditionBasedBacktestStrategy(new[] { mainSeries, signalSeries });

        IReadOnlyList<StrategyIndicatorRequest> requests = strategy.GetRequiredIndicators();

        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, r => r.OutputName == "Main");
        Assert.Contains(requests, r => r.OutputName == "Signal");
    }

    [Fact]
    public void GetRequiredIndicators_SameOutputName_StillDedupes()
    {
        // Regression guard: the OutputName addition to the dedupe key must not break the existing
        // content-dedupe behavior when OutputName is identical (including the default "Main" on both).
        var entryA = new BacktestConditionEntry { Left = CloseSide(), RightNumericValue = 1m };
        var entryB = new BacktestConditionEntry { Left = CloseSide(), RightNumericValue = 2m };
        var strategy = new ConditionBasedBacktestStrategy(new[] { entryA, entryB });

        IReadOnlyList<StrategyIndicatorRequest> requests = strategy.GetRequiredIndicators();

        Assert.Single(requests);
        Assert.Equal(IndicatorResult.MainSeriesName, requests[0].OutputName);
    }

    [Fact]
    public void GetRequiredIndicators_PrimaryDisplayAlias_IsCanonicalizedToMain()
    {
        var entry = new BacktestConditionEntry
        {
            Left = CloseSide(outputName: "SMA"),
            RightNumericValue = 1m,
        };

        var strategy = new ConditionBasedBacktestStrategy(new[] { entry });

        StrategyIndicatorRequest request = Assert.Single(strategy.GetRequiredIndicators());
        Assert.Equal(IndicatorResult.MainSeriesName, request.OutputName);
        Assert.Equal(IndicatorResult.MainSeriesName, Assert.Single(strategy.Entries).Left.OutputName);
    }

    // ===== SAで改善 bug fix (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): a "Price"
    // catalog condition (one row per PriceType - Open/High/Low/...) previously had no way to carry which
    // PriceType was selected all the way to the engine, so every Price condition silently evaluated
    // against Close regardless of the row picked ("High > Low" was actually always "Close > Close"). =====

    [Fact]
    public void GetRequiredIndicators_PriceSourceDiffers_ProducesSeparateRequests()
    {
        var highSide = new BacktestConditionEntry { Left = CloseSide(priceSource: PriceType.High), RightNumericValue = 1m };
        var lowSide = new BacktestConditionEntry { Left = CloseSide(priceSource: PriceType.Low), RightNumericValue = 1m };
        var strategy = new ConditionBasedBacktestStrategy(new[] { highSide, lowSide });

        IReadOnlyList<StrategyIndicatorRequest> requests = strategy.GetRequiredIndicators();

        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, r => r.PriceSource == PriceType.High);
        Assert.Contains(requests, r => r.PriceSource == PriceType.Low);
    }

    [Fact]
    public void PriceHighGreaterThanPriceLow_EntersImmediately_ThroughTheRealEngineAndRealIndicatorFactory()
    {
        // Uses the REAL IndicatorFactory (not this file's Close-only FakeIndicatorFactory) so
        // CorePriceIndicator actually computes distinct High/Low series - proving PriceSource is genuinely
        // applied to the created indicator instance, not just plumbed through data structures. High > Low
        // is true for every non-doji bar, so a correct implementation must enter on the very first bar.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 110m, 90m, 105m),
            Bar(1, 105m, 106m, 104m, 105m));
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, DailyStart, DailyStart.AddDays(10), 0, 0);

        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.High },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            Right = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Low },
            Role = BacktestConditionRole.EntryOnly,
            Position = TradeSide.Long,
        };
        var strategy = new ConditionBasedBacktestStrategy(new[] { entry });

        BacktestResult result = new BacktestEngine(new IndicatorFactory()).Run(input, MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(105m, fill.Price); // bar index 1's Open - the bar right after bar0's true signal
    }

    /// <summary>Builds a strategy for a single true-valued condition and a matching StrategyContext, direct
    /// unit-testing <see cref="ConditionBasedBacktestStrategy.Evaluate"/>/<c>EvaluateExit</c> without going
    /// through <see cref="BacktestEngine"/> (Task 2 of
    /// Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md; the engine does not call the new
    /// <c>EvaluateExit</c> member yet — that wiring is Task 3's scope).</summary>
    private static (ConditionBasedBacktestStrategy Strategy, StrategyContext Context) MakeTrueConditionContext(
        BacktestConditionEntry entry, TradeSide? positionSide)
    {
        var strategy = new ConditionBasedBacktestStrategy(new[] { entry });
        StrategyIndicatorRequest request = Assert.Single(strategy.GetRequiredIndicators());
        var indicators = new IndicatorSeriesSet(new Dictionary<string, ImmutableArray<decimal?>>
        {
            [request.Key] = ImmutableArray.Create<decimal?>(20m), // Close=20 > RightNumericValue=10 -> true
        });
        var context = new StrategyContext
        {
            BarIndex = 0,
            Bar = FlatBar(0, 20m),
            PositionSide = positionSide,
            Snapshot = default,
            Indicators = indicators,
        };
        return (strategy, context);
    }

    [Theory]
    [InlineData(BacktestConditionRole.EntryOnly, TradeSide.Long, SignalType.LongEntry)]
    [InlineData(BacktestConditionRole.EntryOnly, TradeSide.Short, SignalType.ShortEntry)]
    [InlineData(BacktestConditionRole.Both, TradeSide.Long, SignalType.LongEntry)]
    [InlineData(BacktestConditionRole.Both, TradeSide.Short, SignalType.ShortEntry)]
    [InlineData(BacktestConditionRole.Reversal, TradeSide.Long, SignalType.LongEntry)]
    [InlineData(BacktestConditionRole.Reversal, TradeSide.Short, SignalType.ShortEntry)]
    public void Evaluate_WhileFlat_ResolvesSignalTypeFromRoleAndPosition(
        BacktestConditionRole role, TradeSide position, SignalType expected)
    {
        // Plan section 2.2's table: entry-side resolution while flat is Position-aware for EntryOnly, Both,
        // and Reversal alike (v1's hardcoded-LongEntry behavior is retired).
        var entry = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = role,
            Position = position,
        };
        (ConditionBasedBacktestStrategy strategy, StrategyContext context) = MakeTrueConditionContext(entry, positionSide: null);

        StrategyOrderRequest? result = strategy.Evaluate(context);

        Assert.True(result.HasValue);
        Assert.Equal(expected, result!.Value.SignalType);
    }

    [Fact]
    public void Evaluate_ExitOnly_StaysSideAgnostic_UnaffectedByPosition()
    {
        // Plan section 2.2.1: ExitOnly never reads Position; it stays keyed only to PositionSide, exactly
        // as before this feature existed.
        var exitOnly = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.ExitOnly,
            Position = TradeSide.Short, // must be ignored entirely for ExitOnly
        };

        (ConditionBasedBacktestStrategy longStrategy, StrategyContext longContext) = MakeTrueConditionContext(exitOnly, TradeSide.Long);
        Assert.Equal(SignalType.LongExit, longStrategy.Evaluate(longContext)!.Value.SignalType);

        (ConditionBasedBacktestStrategy shortStrategy, StrategyContext shortContext) = MakeTrueConditionContext(exitOnly, TradeSide.Short);
        Assert.Equal(SignalType.ShortExit, shortStrategy.Evaluate(shortContext)!.Value.SignalType);
    }

    [Theory]
    [InlineData(TradeSide.Long, SignalType.LongExit)]
    [InlineData(TradeSide.Short, SignalType.ShortExit)]
    public void Evaluate_BothRoleAnySide_AlwaysUsesUnchangedGenericExitChain_NeverReverses(
        TradeSide heldSide, SignalType expectedExit)
    {
        // Corrected design (2026-09-19): Both never carries reversal semantics, regardless of Position or
        // which side is held - it keeps its pre-existing, unconditional, side-agnostic generic-exit-chain
        // behavior byte-for-byte (this is exactly what BothRole_SingleConditionDrivesEntryThenImmediateExit
        // relies on end-to-end through the real engine). Position=Short is deliberately set here (the
        // opposite of one of the held sides tested) to prove Both's exit path never branches on it.
        var bothShort = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.Both,
            Position = TradeSide.Short,
        };
        (ConditionBasedBacktestStrategy strategy, StrategyContext context) = MakeTrueConditionContext(bothShort, heldSide);

        StrategyOrderRequest? entryTypeResult = strategy.Evaluate(context);
        StrategyOrderRequest? exitTypeResult = strategy.EvaluateExit(context);

        Assert.Equal(expectedExit, entryTypeResult!.Value.SignalType); // generic exit chain, unchanged
        Assert.Null(exitTypeResult); // Both never reverses - EvaluateExit reports nothing extra
    }

    [Fact]
    public void Evaluate_ReversalRoleSameSideAsHeldPosition_BehavesLikeEntryOnly_NoExitContribution()
    {
        // Plan section 2.2 (Reversal row): while holding the SAME side as its own Position, a Reversal
        // entry contributes nothing at all - it never joins the generic exit chain (unlike Both/ExitOnly).
        var reversalLong = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.Reversal,
            Position = TradeSide.Long,
        };
        (ConditionBasedBacktestStrategy strategy, StrategyContext context) = MakeTrueConditionContext(reversalLong, TradeSide.Long);

        Assert.Null(strategy.Evaluate(context));
        Assert.Null(strategy.EvaluateExit(context));
    }

    [Fact]
    public void EvaluateAndEvaluateExit_ReversalRoleOppositeSideOfHeldPosition_ProducesReversalPair()
    {
        // Plan sections 2.2/3.3: a Reversal/Position=Long entry, while holding Short (the OPPOSITE side),
        // requests opening Long via Evaluate AND closing the held Short via the new EvaluateExit - the
        // exact "Both button, Long selected -> Entry=Long, Exit=Short" pair the user originally requested.
        var reversalLong = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.Reversal,
            Position = TradeSide.Long,
        };
        (ConditionBasedBacktestStrategy strategy, StrategyContext context) = MakeTrueConditionContext(reversalLong, TradeSide.Short);

        StrategyOrderRequest? entryTypeResult = strategy.Evaluate(context);
        StrategyOrderRequest? exitTypeResult = strategy.EvaluateExit(context);

        Assert.Equal(SignalType.LongEntry, entryTypeResult!.Value.SignalType);
        Assert.Equal(SignalType.ShortExit, exitTypeResult!.Value.SignalType);
    }

    [Fact]
    public void EvaluateAndEvaluateExit_ReversalRoleOppositeSideShortPosition_ProducesReversalPair()
    {
        // Symmetric case: Reversal/Position=Short, while holding Long, requests opening Short + closing Long.
        var reversalShort = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.Reversal,
            Position = TradeSide.Short,
        };
        (ConditionBasedBacktestStrategy strategy, StrategyContext context) = MakeTrueConditionContext(reversalShort, TradeSide.Long);

        StrategyOrderRequest? entryTypeResult = strategy.Evaluate(context);
        StrategyOrderRequest? exitTypeResult = strategy.EvaluateExit(context);

        Assert.Equal(SignalType.ShortEntry, entryTypeResult!.Value.SignalType);
        Assert.Equal(SignalType.LongExit, exitTypeResult!.Value.SignalType);
    }

    [Fact]
    public void EvaluateExit_NoReversalRoleEntries_AlwaysReturnsNull()
    {
        // Default-interface-method regression guard: with only EntryOnly/ExitOnly/Both entries (no
        // Reversal at all), EvaluateExit must stay inert (mirrors NoOpBacktestStrategy's inherited default
        // of null).
        var exitOnly = new BacktestConditionEntry
        {
            Left = CloseSide(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.ExitOnly,
        };
        (ConditionBasedBacktestStrategy strategy, StrategyContext context) = MakeTrueConditionContext(exitOnly, TradeSide.Long);

        Assert.Null(strategy.EvaluateExit(context));
    }
}
