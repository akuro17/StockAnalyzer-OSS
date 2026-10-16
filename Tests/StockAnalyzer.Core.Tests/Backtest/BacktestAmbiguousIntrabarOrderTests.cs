#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Order-dependent bars (API_SPECIFICATION "Order-dependent bars"): AB-1 (Stop-Loss and Take-Profit both reached) and AB-2 (pending exit order versus margin
/// liquidation) are recorded as <see cref="WarningCode.AmbiguousIntrabarOrder"/> warnings. Detection never changes an order, fill, trade or equity point, and
/// warnings are not part of the run fingerprint.
/// </summary>
public class BacktestAmbiguousIntrabarOrderTests
{
    private const decimal Entry = 100m;
    private const decimal FivePercent = 0.05m;

    private static CandleData Bar(decimal open, decimal high, decimal low, decimal close) => SyntheticBars.Bar(0, open, high, low, close);

    private static CandleData Flat(int day, decimal price) => SyntheticBars.Bar(day, price, price, price, price);

    private static BacktestConditionEntry CloseAbove10(TradeSide position) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 10m,
        Role = BacktestConditionRole.EntryOnly,
        Position = position,
    };

    private static ConditionBasedBacktestStrategy RiskStrategy(decimal? stopLoss, decimal? takeProfit, TradeSide position = TradeSide.Long)
        => new(new[] { CloseAbove10(position) }, new BacktestRiskManagementSettings { StopLossPercent = stopLoss, TakeProfitPercent = takeProfit });

    private static StrategyContext HeldContext(CandleData bar, TradeSide? side, decimal? entryPrice, PricePathConflictMode mode) => new()
    {
        BarIndex = 5,
        Bar = bar,
        PositionSide = side,
        EntryPrice = entryPrice,
        PricePathConflictMode = mode,
    };

    // ------------------------------------------------------------------ AB-1: the pure detector

    [Theory]
    // Long: stop 95, limit 105. Both reached, Open between them.
    [InlineData(100, 106, 94, 100, PricePathConflictMode.Conservative, false, SlTpWinner.StopLoss, false)]
    [InlineData(100, 106, 94, 103, PricePathConflictMode.CanonicalOHLC, false, SlTpWinner.StopLoss, false)]   // bullish: Low first
    [InlineData(100, 106, 94, 97, PricePathConflictMode.CanonicalOHLC, false, SlTpWinner.TakeProfit, true)]   // bearish: High first (Conservative would say Stop-Loss)
    [InlineData(100, 106, 94, 100, PricePathConflictMode.CanonicalOHLC, false, SlTpWinner.StopLoss, false)]   // doji: Stop-Loss
    // Open decided: gap through the stop / through the limit.
    [InlineData(94, 106, 93, 100, PricePathConflictMode.Conservative, true, SlTpWinner.StopLoss, false)]
    [InlineData(94, 106, 93, 100, PricePathConflictMode.CanonicalOHLC, true, SlTpWinner.StopLoss, false)]
    [InlineData(106, 108, 94, 100, PricePathConflictMode.Conservative, true, SlTpWinner.StopLoss, true)]  // CanonicalOHLC would let the gap through the limit win
    [InlineData(106, 108, 94, 100, PricePathConflictMode.CanonicalOHLC, true, SlTpWinner.TakeProfit, true)]
    // Bars on which a RejectIfAtLimitPrice lock can keep the Stop-Loss order pending (Low == stop == Close, or Open == Low == Close <= stop).
    [InlineData(100, 106, 95, 95, PricePathConflictMode.Conservative, false, SlTpWinner.StopLoss, true)]    // shape A: bearish, CanonicalOHLC would say Take-Profit
    [InlineData(94, 106, 94, 94, PricePathConflictMode.Conservative, true, SlTpWinner.StopLoss, false)]     // shape B: gap through the stop, both conventions say Stop-Loss
    public void Long_BarThatReachedBothThresholds_IsATie(
        int open, int high, int low, int close, PricePathConflictMode mode, bool openDecided, SlTpWinner outcome, bool differsUnderOtherMode)
    {
        ConditionBasedBacktestStrategy strategy = RiskStrategy(FivePercent, FivePercent);
        StrategyContext context = HeldContext(Bar(open, high, low, close), TradeSide.Long, Entry, mode);

        Assert.True(strategy.TryDetectRiskTie(in context, out RiskTie tie));
        Assert.Equal(new RiskTie(TradeSide.Long, outcome, openDecided, differsUnderOtherMode), tie);
        // The detector and the decision share one resolver: the recorded outcome is the decision the strategy actually takes.
        StrategyOrderRequest? decision = strategy.Evaluate(context);
        Assert.NotNull(decision);
        Assert.Equal(outcome == SlTpWinner.StopLoss ? OrderType.Stop : OrderType.Limit, decision.Value.OrderType);
    }

    [Theory]
    [InlineData(100, 104, 94)]   // only the stop is reached
    [InlineData(100, 106, 96)]   // only the limit is reached
    [InlineData(100, 104, 96)]   // neither
    public void Long_BarThatReachedOneOrNoThreshold_IsNotATie(int open, int high, int low)
    {
        ConditionBasedBacktestStrategy strategy = RiskStrategy(FivePercent, FivePercent);
        StrategyContext context = HeldContext(Bar(open, high, low, 100), TradeSide.Long, Entry, PricePathConflictMode.Conservative);

        Assert.False(strategy.TryDetectRiskTie(in context, out RiskTie tie));
        Assert.Equal(default, tie);
    }

    [Theory]
    // Short: stop 105, limit 95.
    [InlineData(100, 106, 94, 100, PricePathConflictMode.Conservative, false, SlTpWinner.StopLoss, false)]
    [InlineData(100, 106, 94, 103, PricePathConflictMode.CanonicalOHLC, false, SlTpWinner.TakeProfit, true)]  // bullish: Low (the limit) first
    [InlineData(100, 106, 94, 97, PricePathConflictMode.CanonicalOHLC, false, SlTpWinner.StopLoss, false)]    // bearish: High (the stop) first
    [InlineData(106, 108, 94, 100, PricePathConflictMode.CanonicalOHLC, true, SlTpWinner.StopLoss, false)]    // gap through the stop
    [InlineData(94, 106, 93, 100, PricePathConflictMode.CanonicalOHLC, true, SlTpWinner.TakeProfit, true)]    // gap through the limit
    public void Short_IsTheMirrorImage(int open, int high, int low, int close, PricePathConflictMode mode, bool openDecided, SlTpWinner outcome, bool differsUnderOtherMode)
    {
        ConditionBasedBacktestStrategy strategy = RiskStrategy(FivePercent, FivePercent, TradeSide.Short);
        StrategyContext context = HeldContext(Bar(open, high, low, close), TradeSide.Short, Entry, mode);

        Assert.True(strategy.TryDetectRiskTie(in context, out RiskTie tie));
        Assert.Equal(new RiskTie(TradeSide.Short, outcome, openDecided, differsUnderOtherMode), tie);
    }

    [Fact]
    public void Detector_NeedsBothThresholds_AHeldPosition_AndAnEntryPrice()
    {
        CandleData both = Bar(100, 106, 94, 100);

        StrategyContext held = HeldContext(both, TradeSide.Long, Entry, PricePathConflictMode.Conservative);
        Assert.False(RiskStrategy(FivePercent, null).TryDetectRiskTie(in held, out _));
        Assert.False(RiskStrategy(null, FivePercent).TryDetectRiskTie(in held, out _));
        Assert.False(RiskStrategy(null, null).TryDetectRiskTie(in held, out _));

        ConditionBasedBacktestStrategy strategy = RiskStrategy(FivePercent, FivePercent);
        StrategyContext flat = HeldContext(both, null, null, PricePathConflictMode.Conservative);
        StrategyContext noEntryPrice = HeldContext(both, TradeSide.Long, null, PricePathConflictMode.Conservative);
        Assert.False(strategy.TryDetectRiskTie(in flat, out _));
        Assert.False(strategy.TryDetectRiskTie(in noEntryPrice, out _));

        var withoutRisk = new ConditionBasedBacktestStrategy(new[] { CloseAbove10(TradeSide.Long) });
        Assert.False(withoutRisk.TryDetectRiskTie(in held, out _));
    }

    // ------------------------------------------------------------------ AB-1: through the engine

    private static BacktestInput Input(params CandleData[] bars) => VerificationHarness.MakeInput(bars.ToImmutableArray());

    private static BacktestConfiguration Config(PricePathConflictMode mode = PricePathConflictMode.Conservative) => new()
    {
        InitialCapital = 1_000_000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 100m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
        PricePathConflictMode = mode,
    };

    /// <summary>Bar 1's Close (20) triggers the entry, filled at bar 2's Open (100); bar 3 is the bar under test; bar 4 lets the exit order fill.</summary>
    private static BacktestInput RiskScenario(CandleData bar3) => Input(
        Flat(0, 1m),
        Flat(1, 20m),
        SyntheticBars.Bar(2, 100m, 102m, 99m, 100m),
        SyntheticBars.Bar(3, bar3.Open, bar3.High, bar3.Low, bar3.Close),
        SyntheticBars.Bar(4, 96m, 97m, 95m, 96m));

    private static string Day(BacktestInput input, int bar) => input.Bars[bar].Timestamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Engine_RecordsOneWarning_ForTheBarThatReachedBothThresholds()
    {
        BacktestInput input = RiskScenario(Bar(100m, 112m, 90m, 102m));
        // 5% stop (95) and 10% limit (110): bar 3 reaches both.
        BacktestResult result = VerificationHarness.Run(input, Config(), RiskStrategy(FivePercent, 0.10m));

        BacktestWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(WarningCode.AmbiguousIntrabarOrder, warning.Code);
        Assert.Equal(3, warning.BarIndex);
        Assert.Equal(input.Bars[3].Timestamp, warning.Timestamp);
        Assert.Equal(
            $"Intrabar order ambiguity at bar 3 ({Day(input, 3)}): Stop-Loss and Take-Profit were both reached; PricePathConflictMode=Conservative resolved to StopLoss.",
            warning.Message);
    }

    [Fact]
    public void Engine_MessageNamesTheModeTheOutcomeAndAnOpenDecidedOrder()
    {
        // Bearish bar under CanonicalOHLC: High first, so the Take-Profit wins.
        BacktestInput bearish = RiskScenario(Bar(100m, 112m, 90m, 98m));
        BacktestWarning first = Assert.Single(VerificationHarness.Run(bearish, Config(PricePathConflictMode.CanonicalOHLC), RiskStrategy(FivePercent, 0.10m)).Warnings);
        Assert.EndsWith("PricePathConflictMode=CanonicalOHLC resolved to TakeProfit.", first.Message, StringComparison.Ordinal);
        Assert.Equal(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, first.Code); // Conservative would have chosen Stop-Loss: the convention decided this bar

        // A gap through the stop at the Open decides the order.
        BacktestInput gap = RiskScenario(Bar(94m, 112m, 90m, 98m));
        BacktestWarning second = Assert.Single(VerificationHarness.Run(gap, Config(), RiskStrategy(FivePercent, 0.10m)).Warnings);
        Assert.EndsWith("resolved to StopLoss; Open decided the order.", second.Message, StringComparison.Ordinal);
        Assert.Equal(WarningCode.AmbiguousIntrabarOrder, second.Code); // CanonicalOHLC also resolves a gap through the stop to Stop-Loss: detected, not affecting
    }

    [Fact]
    public void Engine_RecordsNothing_WhenNoBarReachedBothThresholds()
    {
        // Bar 3 reaches only the limit.
        BacktestResult onlyLimit = VerificationHarness.Run(RiskScenario(Bar(100m, 112m, 99m, 102m)), Config(), RiskStrategy(FivePercent, 0.10m));
        Assert.Empty(onlyLimit.Warnings);

        // No risk management at all.
        BacktestResult none = VerificationHarness.Run(RiskScenario(Bar(100m, 112m, 90m, 102m)), Config(),
            new ConditionBasedBacktestStrategy(new[] { CloseAbove10(TradeSide.Long) }));
        Assert.Empty(none.Warnings);
    }

    [Fact]
    public void Engine_DetectionChangesNoHashedOutput_AndIsReproducible()
    {
        BacktestInput input = RiskScenario(Bar(100m, 112m, 90m, 102m));
        BacktestResult withDetector = VerificationHarness.Run(input, Config(), RiskStrategy(FivePercent, 0.10m));
        BacktestResult again = VerificationHarness.Run(input, Config(), RiskStrategy(FivePercent, 0.10m));
        BacktestResult withoutDetector = VerificationHarness.Run(input, Config(), new WithoutDetector(RiskStrategy(FivePercent, 0.10m)));

        Assert.Single(withDetector.Warnings);
        Assert.Empty(withoutDetector.Warnings);

        // Reproducibility: the same input, configuration and strategy give the same warnings, text and order.
        Assert.Equal(withDetector.Warnings.ToArray(), again.Warnings.ToArray());

        // Detection is observation only: every hashed collection and the legacy digest are identical with and without it.
        Assert.Equal(withoutDetector.ReproducibilityHash, withDetector.ReproducibilityHash);
        Assert.Equal(withoutDetector.Orders.ToArray(), withDetector.Orders.ToArray());
        Assert.Equal(withoutDetector.Fills.ToArray(), withDetector.Fills.ToArray());
        Assert.Equal(withoutDetector.Trades.ToArray(), withDetector.Trades.ToArray());
        Assert.Equal(withoutDetector.EquityPoints.ToArray(), withDetector.EquityPoints.ToArray());
        Assert.Equal(withoutDetector.Signals.ToArray(), withDetector.Signals.ToArray());
    }

    /// <summary>Hides the optional detector capability: the engine sees a plain <see cref="IBacktestStrategy"/>.</summary>
    private sealed class WithoutDetector : IBacktestStrategy
    {
        private readonly IBacktestStrategy _inner;

        public WithoutDetector(IBacktestStrategy inner) => _inner = inner;

        public string Name => _inner.Name;

        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => _inner.GetRequiredIndicators();

        public StrategyOrderRequest? Evaluate(StrategyContext context) => _inner.Evaluate(context);

        public StrategyOrderRequest? EvaluateExit(StrategyContext context) => _inner.EvaluateExit(context);
    }

    [Fact]
    public void Engine_BarThatHadAPendingExitOrderIsDetectedButNotAffecting()
    {
        // The strategy reports a tie that the other convention would decide differently on every held bar. Bar 1: no Exit order is pending, so the decision is
        // submitted and the convention decides the result (affecting). Bar 2: the Exit order submitted at bar 1 is still pending (it never fills), so the decision
        // is not submitted and the bar is only detected.
        var strategy = new AlwaysTiedStrategy();
        BacktestResult result = VerificationHarness.Run(Input(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m), Flat(3, 100m)), Config(), strategy);

        Assert.Collection(
            result.Warnings,
            first =>
            {
                Assert.Equal(1, first.BarIndex);
                Assert.Equal(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, first.Code);
            },
            second =>
            {
                Assert.Equal(2, second.BarIndex);
                Assert.Equal(WarningCode.AmbiguousIntrabarOrder, second.Code);
            },
            third =>
            {
                Assert.Equal(3, third.BarIndex);
                Assert.Equal(WarningCode.AmbiguousIntrabarOrder, third.Code);
            });
        Assert.Equal(new OrderDependentBarCounts(Detected: 3, Affecting: 1, RiskTies: 3, ExitVersusLiquidation: 0), OrderDependentBarCounts.Count(result.Warnings));
    }

    [Fact]
    public void Engine_TieBarWithAPendingExitOrderCausedByAVolumeDeferral_IsDetectedButNotAffecting()
    {
        // Reachability of the "no Exit order pending" half of the affecting rule with the strategy's OWN orders. Bar 3 is a convention-dependent tie (bearish: Conservative
        // says Stop-Loss, CanonicalOHLC says Take-Profit) with no Exit order pending, so it is affecting; the Stop-Loss order it submits is triggered on bar 4 but only 10
        // units may trade (100 * 0.1) against 100 held, and ClampAndCarryOverRemainder keeps the order pending. Bar 4 is the same kind of tie, but the decision cannot be
        // submitted (the slot is taken), so it is detected only. Without a deferral a pending order and a tie never share a bar.
        const decimal ratio = 0.1m;
        CandleData Candle(int day, decimal open, decimal high, decimal low, decimal close, long volume)
            => new(SyntheticBars.Start.AddDays(day), open, high, low, close, volume);
        BacktestInput input = Input(
            Candle(0, 1m, 1m, 1m, 1m, 10_000),
            Candle(1, 20m, 20m, 20m, 20m, 10_000),
            Candle(2, 100m, 102m, 99m, 100m, 10_000),
            Candle(3, 100m, 112m, 90m, 98m, 10_000),
            Candle(4, 100m, 112m, 90m, 98m, 100),
            Candle(5, 96m, 97m, 95m, 96m, 10_000));
        var config = new BacktestConfiguration
        {
            InitialCapital = 1_000_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 100m,
            InitialMarginRatio = 1m,
            MaintenanceMarginRatio = 0.5m,
            MaxVolumeParticipationRatio = ratio,
            VolumeConstraintMode = VolumeConstraintMode.ClampAndCarryOverRemainder,
        };

        BacktestResult result = VerificationHarness.Run(input, config, RiskStrategy(FivePercent, 0.10m));

        Assert.Single(result.Orders, o => o.Type == OrderType.Stop); // the bar-4 decision was not submitted: one Stop-Loss order only
        Assert.Equal(5, Assert.Single(result.Trades).ExitBar);       // it was carried over and filled when volume allowed
        Assert.Collection(
            result.Warnings,
            first =>
            {
                Assert.Equal(3, first.BarIndex);
                Assert.Equal(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, first.Code);
            },
            second =>
            {
                Assert.Equal(4, second.BarIndex);
                Assert.Equal(WarningCode.AmbiguousIntrabarOrder, second.Code);
            });
        Assert.Equal(new OrderDependentBarCounts(Detected: 2, Affecting: 1, RiskTies: 2, ExitVersusLiquidation: 0), OrderDependentBarCounts.Count(result.Warnings));
    }

    // The second route to a pending Exit order on a tie bar: a price-limit lock (non-default PriceLimitFillMode.RejectIfAtLimitPrice). Entry 100, Stop-Loss 95, Take-Profit 110.
    // Bar 3 is an ordinary convention-dependent tie that submits the Stop-Loss order; bar 4 reaches both thresholds again but closes at its low with the stop at or above that
    // low, so the sell fill is blocked and the order stays pending; bar 5 releases it.
    private static BacktestInput PriceLimitInput(decimal open4, decimal high4, decimal low4, decimal close4)
    {
        CandleData Candle(int day, decimal open, decimal high, decimal low, decimal close)
            => new(SyntheticBars.Start.AddDays(day), open, high, low, close, 10_000);
        return Input(
            Candle(0, 1m, 1m, 1m, 1m),
            Candle(1, 20m, 20m, 20m, 20m),
            Candle(2, 100m, 102m, 99m, 100m),
            Candle(3, 100m, 112m, 90m, 98m),
            Candle(4, open4, high4, low4, close4),
            Candle(5, 96m, 97m, 95m, 96m));
    }

    private static BacktestConfiguration PriceLimitConfig(PriceLimitFillMode mode) => new()
    {
        InitialCapital = 1_000_000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 100m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
        PriceLimitFillMode = mode,
    };

    [Theory]
    [InlineData(100, 112, 95, 95)] // shape A: Low == stop == Close (CanonicalOHLC would resolve it to Take-Profit)
    [InlineData(94, 112, 94, 94)]  // shape B: Open == Low == Close <= stop (both conventions say Stop-Loss)
    public void Engine_TieBarWithAPendingExitOrderCausedByAPriceLimitLock_IsDetectedButNotAffecting(int open, int high, int low, int close)
    {
        BacktestResult result = VerificationHarness.Run(
            PriceLimitInput(open, high, low, close), PriceLimitConfig(PriceLimitFillMode.RejectIfAtLimitPrice), RiskStrategy(FivePercent, 0.10m));

        Assert.Single(result.Orders, o => o.Type == OrderType.Stop); // the bar-4 decision was not submitted: one Stop-Loss order only
        Assert.Equal(5, Assert.Single(result.Trades).ExitBar);       // locked on bar 4, filled on bar 5
        Assert.Collection(
            result.Warnings,
            first =>
            {
                Assert.Equal(3, first.BarIndex);
                Assert.Equal(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, first.Code);
            },
            second =>
            {
                Assert.Equal(4, second.BarIndex);
                Assert.Equal(WarningCode.AmbiguousIntrabarOrder, second.Code);
            });
        Assert.Equal(new OrderDependentBarCounts(Detected: 2, Affecting: 1, RiskTies: 2, ExitVersusLiquidation: 0), OrderDependentBarCounts.Count(result.Warnings));
    }

    [Theory]
    [InlineData(100, 112, 95, 95)]
    [InlineData(94, 112, 94, 94)]
    public void Engine_SameBarsWithTheDefaultPriceLimitMode_FillTheExitAtOnce(int open, int high, int low, int close)
    {
        // Control: the lock, not the bar shape alone, keeps the order pending. With AlwaysFill the Stop-Loss fills on bar 4, nothing is held at the tie check of that
        // bar, and only bar 3 is recorded.
        BacktestResult result = VerificationHarness.Run(
            PriceLimitInput(open, high, low, close), PriceLimitConfig(PriceLimitFillMode.AlwaysFill), RiskStrategy(FivePercent, 0.10m));

        Assert.Equal(4, Assert.Single(result.Trades).ExitBar);
        BacktestWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(3, warning.BarIndex);
        Assert.Equal(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, warning.Code);
    }

    /// <summary>Enters at bar 0, submits an Exit Limit far above the market at bar 1 (never filled), and reports a convention-dependent tie on every held bar.</summary>
    private sealed class AlwaysTiedStrategy : IBacktestStrategy, IBacktestRiskTieDetector
    {
        public string Name => "AlwaysTied";

        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();

        public StrategyOrderRequest? Evaluate(StrategyContext context) => context.BarIndex switch
        {
            0 => VerificationHarness.Req(SignalType.LongEntry),
            1 => VerificationHarness.Req(SignalType.LongExit, OrderType.Limit, limitPrice: 1000m),
            _ => null,
        };

        public bool TryDetectRiskTie(in StrategyContext context, out RiskTie tie)
        {
            tie = new RiskTie(TradeSide.Long, SlTpWinner.StopLoss, OpenDecided: false, OutcomeDiffersUnderOtherMode: true);
            return context.PositionSide is not null;
        }
    }

    [Fact]
    public void OrderDependentBarCounts_CountsEachCodeOnceAndHonoursTheFirstEvaluatedBar()
    {
        DateTime t = SyntheticBars.Start;
        BacktestWarning[] warnings =
        {
            new(WarningCode.PotentialUnadjustedSplit, 0, t, OrderDependentBarCounts.RiskTieSubject), // a split warning is never counted, whatever its text
            new(WarningCode.AmbiguousIntrabarOrder, 1, t, OrderDependentBarCounts.RiskTieSubject),
            new(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, 2, t, OrderDependentBarCounts.ExitVersusLiquidationSubject),
            new(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, 5, t, OrderDependentBarCounts.RiskTieSubject),
        };

        Assert.Equal(new OrderDependentBarCounts(3, 2, RiskTies: 2, ExitVersusLiquidation: 1), OrderDependentBarCounts.Count(warnings));
        Assert.Equal(new OrderDependentBarCounts(2, 2, RiskTies: 1, ExitVersusLiquidation: 1), OrderDependentBarCounts.Count(warnings, fromBarIndex: 2));
        Assert.Equal(new OrderDependentBarCounts(0, 0), OrderDependentBarCounts.Count(Array.Empty<BacktestWarning>()));
    }

    // ------------------------------------------------------------------ AB-2: exit order versus margin liquidation

    // E0 = 1000, Q = 20, IM 0.3, MM 0.2: a Long at 100 has Cash 400 / HeldMargin 600, so its liquidation threshold is 62.5.
    private const decimal LiquidationPrice = 62.5m;
    private const decimal SellLimit = 110m;

    private static BacktestConfiguration MarginConfig(IntrabarPathMode mode = IntrabarPathMode.DeterministicCandleShape)
        => VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m, intrabarPathMode: mode);

    /// <summary>Long entered at bar 1's Open (100); a pending Exit order is submitted at bar 1's Close and is eligible from bar 2, the bar under test.</summary>
    private static (BacktestInput Input, ScriptedStrategy Strategy) MarginScenario(CandleData bar2, StrategyOrderRequest? exit)
    {
        var script = new Dictionary<int, StrategyOrderRequest> { [0] = VerificationHarness.Req(SignalType.LongEntry) };
        if (exit is { } request) script[1] = request;
        return (Input(Flat(0, 100m), Flat(1, 100m), SyntheticBars.Bar(2, bar2.Open, bar2.High, bar2.Low, bar2.Close)), new ScriptedStrategy(script));
    }

    private static StrategyOrderRequest SellLimitExit() => VerificationHarness.Req(SignalType.LongExit, OrderType.Limit, limitPrice: SellLimit);

    private static BacktestResult RunMargin(CandleData bar2, StrategyOrderRequest? exit, IntrabarPathMode mode = IntrabarPathMode.DeterministicCandleShape)
    {
        (BacktestInput input, ScriptedStrategy strategy) = MarginScenario(bar2, exit);
        return VerificationHarness.Run(input, MarginConfig(mode), strategy);
    }

    [Theory]
    // L 60 <= 62.5 < O 100 < 110 <= H 112: the bar reaches both the margin threshold and the exit limit.
    [InlineData(105, IntrabarPathMode.DeterministicCandleShape, "PendingOrder", false)] // bullish: O-H-L-C, the limit is reached first
    [InlineData(95, IntrabarPathMode.DeterministicCandleShape, "MarginLiquidation", true)] // bearish: O-L-H-C, the liquidation is reached first
    [InlineData(100, IntrabarPathMode.DeterministicCandleShape, "MarginLiquidation", true)] // doji, held Long: Low first
    [InlineData(95, IntrabarPathMode.CanonicalOHLC, "PendingOrder", false)] // fixed O-H-L-C
    public void ExitOrderAndLiquidationBothReached_IsRecordedWithTheOutcomeTheEngineActuallyUsed(
        int close, IntrabarPathMode mode, string expectedWinner, bool liquidated)
    {
        CandleData bar = Bar(100m, 112m, 60m, close);
        (BacktestInput input, ScriptedStrategy strategy) = MarginScenario(bar, SellLimitExit());
        BacktestResult result = VerificationHarness.Run(input, MarginConfig(mode), strategy);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(liquidated, trade.IsForcedLiquidation);
        Assert.Equal(liquidated ? LiquidationPrice : SellLimit, trade.ExitPrice);

        BacktestWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, warning.Code); // the vertex order decided the winner by construction
        Assert.Equal(2, warning.BarIndex);
        Assert.Equal(
            $"Intrabar order ambiguity at bar 2 ({Day(input, 2)}): the pending exit order and the margin liquidation are order-dependent; IntrabarPathMode={mode} resolved to {expectedWinner}.",
            warning.Message);
    }

    [Fact]
    public void ExitVersusLiquidation_DetectionChangesNoHashedOutput()
    {
        CandleData bar = Bar(100m, 112m, 60m, 95m);
        (BacktestInput input, ScriptedStrategy strategy) = MarginScenario(bar, SellLimitExit());
        BacktestResult recorded = VerificationHarness.Run(input, MarginConfig(), strategy);

        // The same run on a configuration where nothing is order-dependent has no warning; the legacy digest depends only on the hashed collections.
        Assert.Single(recorded.Warnings);
        BacktestResult again = VerificationHarness.Run(input, MarginConfig(), strategy);
        Assert.Equal(recorded.Warnings.ToArray(), again.Warnings.ToArray());
        Assert.Equal(recorded.ReproducibilityHash, again.ReproducibilityHash);
    }

    [Theory]
    [InlineData(112, 70, 105)]  // only the limit is reached (Low 70 stays above the threshold)
    [InlineData(105, 60, 95)]   // only the liquidation is reached (High 105 stays below the limit)
    [InlineData(112, 50, 60)]   // the Open (55) is already through the threshold: decided at position 0 under every order
    public void OneSidedOrOpenDecidedBars_AreNotOrderDependent(int high, int low, int close)
    {
        decimal open = low == 50 ? 55m : 100m;
        BacktestResult result = RunMargin(Bar(open, high, low, close), SellLimitExit());

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NoEligibleExitOrder_IsNotOrderDependent()
    {
        CandleData bar = Bar(100m, 112m, 60m, 95m);

        Assert.Empty(RunMargin(bar, exit: null).Warnings);
        // A MarketOnClose exit never competes with the liquidation on the path (it is evaluated at Close in its own step).
        Assert.Empty(RunMargin(bar, VerificationHarness.Req(SignalType.LongExit, OrderType.MarketOnClose)).Warnings);
    }

    [Fact]
    public void FailedBar_LeavesNoRecordOfItsAmbiguity()
    {
        // Bar 2 reaches both the liquidation threshold (Low 50) and the Sell Limit at 6e28 (bullish: the limit first), so the bar is order-dependent and is recorded
        // at Step 2; the exit then fills at 6e28 and 20 * (6e28 - 100) overflows the realized PnL. The diagnostic engine rolls the whole bar back, record included.
        const decimal huge = 6e28m;
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = VerificationHarness.Req(SignalType.LongEntry),
            [1] = VerificationHarness.Req(SignalType.LongExit, OrderType.Limit, limitPrice: huge),
        });
        BacktestInput input = Input(Flat(0, 100m), Flat(1, 100m), SyntheticBars.Bar(2, 5e28m, huge, 50m, 5.5e28m));

        BacktestDiagnosedRun run = VerificationHarness.CreateEngine().RunWithDiagnostics(input, MarginConfig(), strategy);

        Assert.Equal(RunStatus.Failed, run.Result.Status);
        BacktestRunDiagnostic diagnostic = Assert.IsType<BacktestRunDiagnostic>(run.Diagnostic);
        // The failure is in the exit's accounting, which runs after the ambiguity was recorded (so the record existed and had to be rolled back).
        Assert.Equal(BacktestArithmeticOperation.ExitAccounting, diagnostic.Operation);
        Assert.Equal(2, diagnostic.BarIndex);
        Assert.Empty(run.Result.Warnings);
    }

    // ------------------------------------------------------------------ the explicit-order functions

    [Theory]
    [InlineData(IntrabarPathMode.CanonicalOHLC)]
    [InlineData(IntrabarPathMode.DeterministicCandleShape)]
    public void OnOrderFunctions_AgreeWithTheModeDerivedOnes_OverGeneratedBars(IntrabarPathMode mode)
    {
        var random = new Random(20261016);
        TradeSide?[] sides = { null, TradeSide.Long, TradeSide.Short };
        var orders = new List<BacktestOrder>();
        foreach (OrderSide side in new[] { OrderSide.Buy, OrderSide.Sell })
        {
            orders.Add(Order(OrderType.Limit, side, limit: 100m, stop: null, activated: false));
            orders.Add(Order(OrderType.Stop, side, limit: null, stop: 100m, activated: false));
            orders.Add(Order(OrderType.StopLimit, side, limit: 100m, stop: 103m, activated: false));
            orders.Add(Order(OrderType.StopLimit, side, limit: 100m, stop: 97m, activated: true));
        }

        for (int n = 0; n < 200; n++)
        {
            decimal open = 90m + random.Next(0, 2000) / 100m;
            decimal close = random.Next(0, 5) == 0 ? open : 90m + random.Next(0, 2000) / 100m;
            decimal high = Math.Max(open, close) + random.Next(0, 800) / 100m;
            decimal low = Math.Min(open, close) - random.Next(0, 800) / 100m;
            decimal threshold = 85m + random.Next(0, 3000) / 100m;
            CandleData bar = Bar(open, high, low, close);

            foreach (TradeSide? side in sides)
            {
                bool lowFirst = FillPathScanner.VisitsLowFirst(open, close, mode, side);
                foreach (TouchDirection direction in new[] { TouchDirection.Upward, TouchDirection.Downward })
                {
                    Assert.Equal(
                        FillPathScanner.TryFindTouchPosition(open, high, low, close, threshold, direction, mode, side),
                        FillPathScanner.TryFindTouchPositionOnOrder(open, high, low, close, threshold, direction, lowFirst));
                }

                if (side is { } held)
                {
                    Assert.Equal(
                        MarginLiquidationCalculator.TryFindBreachPosition(held, threshold, bar, mode),
                        MarginLiquidationCalculator.TryFindBreachPositionOnOrder(held, threshold, bar, lowFirst));
                }

                foreach (BacktestOrder order in orders)
                {
                    ResidualFill expected = OrderFillEvaluator.EvaluateFrom(order, bar, FillPathScanner.OpenPoint(open), 0m, 0m, 0m, null, mode, side);
                    ResidualFill actual = OrderFillEvaluator.EvaluateFromOnOrder(order, bar, FillPathScanner.OpenPoint(open), 0m, 0m, 0m, lowFirst);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Theory]
    [InlineData(IntrabarPathMode.CanonicalOHLC, 105, 100, null, false)]
    [InlineData(IntrabarPathMode.CanonicalOHLC, 95, 100, TradeSide.Short, false)]
    [InlineData(IntrabarPathMode.DeterministicCandleShape, 105, 100, null, false)]      // bullish: High first
    [InlineData(IntrabarPathMode.DeterministicCandleShape, 95, 100, null, true)]        // bearish: Low first
    [InlineData(IntrabarPathMode.DeterministicCandleShape, 100, 100, TradeSide.Long, true)]   // doji, Long: Low first
    [InlineData(IntrabarPathMode.DeterministicCandleShape, 100, 100, null, true)]       // doji, flat: Low first
    [InlineData(IntrabarPathMode.DeterministicCandleShape, 100, 100, TradeSide.Short, false)] // doji, Short: High first
    public void VisitsLowFirst_IsTheDocumentedCandleShapeRule(IntrabarPathMode mode, int close, int open, TradeSide? held, bool expected)
        => Assert.Equal(expected, FillPathScanner.VisitsLowFirst(open, close, mode, held));

    private static BacktestOrder Order(OrderType type, OrderSide side, decimal? limit, decimal? stop, bool activated) => new(
        OrderId: 1, Side: side, Type: type, Quantity: 10m, LimitPrice: limit, StopPrice: stop,
        Status: OrderStatus.Submitted, TimeInForce: new TimeInForce(true, -1), SubmittedBar: 0, EarliestFillBar: 0,
        StopActivated: activated, ExpiredReason: null, RejectedReason: null);
}
