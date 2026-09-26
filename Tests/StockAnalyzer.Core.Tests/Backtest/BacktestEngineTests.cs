using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Task #6 engine-level sanity tests for BacktestEngine.Run's Steps 1-9. This is not the full named
/// acceptance suite from Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 10 (that is task #8) - it
/// exists to prove the orchestration itself (fills, funds checks, margin-call precedence, GTD expiry,
/// insolvency) is wired correctly before the final acceptance pass.
/// </summary>
public class BacktestEngineTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close, long volume = 1000)
        => new(Bar0.AddDays(dayOffset), open, high, low, close, volume);

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars, int tradingStartIndex = 0)
        => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, tradingStartIndex);

    private static BacktestConfiguration MakeConfig(
        decimal initialCapital = 1000m, decimal commissionFlat = 0m, decimal commissionPerUnit = 0m,
        decimal slippageRatio = 0m, PositionSizingModel sizingModel = PositionSizingModel.FixedQuantity,
        decimal sizingParameter = 1m, decimal initialMarginRatio = 1m, decimal maintenanceMarginRatio = 0.5m,
        decimal liquidationPenaltyRatio = 0m, ExecutionModel executionModel = ExecutionModel.Legacy)
        => new()
        {
            ExecutionModel = executionModel,
            InitialCapital = initialCapital,
            CommissionFlat = commissionFlat,
            CommissionPerUnit = commissionPerUnit,
            SlippageRatio = slippageRatio,
            SizingModel = sizingModel,
            SizingParameter = sizingParameter,
            InitialMarginRatio = initialMarginRatio,
            MaintenanceMarginRatio = maintenanceMarginRatio,
            LiquidationPenaltyRatio = liquidationPenaltyRatio,
        };

    private static StrategyOrderRequest LongEntryMarket(string reason = "enter") => new(SignalType.LongEntry, OrderType.Market, null, null, Gtc, reason);
    private static StrategyOrderRequest LongExitMarket(string reason = "exit") => new(SignalType.LongExit, OrderType.Market, null, null, Gtc, reason);
    private static StrategyOrderRequest LongEntryMoc(string reason = "enter-moc") => new(SignalType.LongEntry, OrderType.MarketOnClose, null, null, Gtc, reason);

    /// <summary>Deterministic strategy that fires pre-scripted requests on specific bar indices, so engine-level tests do not depend on any indicator computation.</summary>
    private sealed class ScriptedStrategy : IBacktestStrategy
    {
        private readonly Dictionary<int, StrategyOrderRequest> _script;
        private readonly IReadOnlyList<StrategyIndicatorRequest> _requiredIndicators;
        public string Name => "Scripted";
        public ScriptedStrategy(Dictionary<int, StrategyOrderRequest> script, IReadOnlyList<StrategyIndicatorRequest>? requiredIndicators = null)
        {
            _script = script;
            _requiredIndicators = requiredIndicators ?? Array.Empty<StrategyIndicatorRequest>();
        }
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => _requiredIndicators;
        public StrategyOrderRequest? Evaluate(StrategyContext context)
            => _script.TryGetValue(context.BarIndex, out StrategyOrderRequest request) ? request : null;
    }

    private sealed class StubIndicatorFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => null;
        public bool IsRegistered(IndicatorType type) => false;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => Array.Empty<IndicatorType>();
    }

    private static BacktestEngine CreateEngine() => new(new StubIndicatorFactory());

    [Fact]
    public void EmptyBars_ReturnsEmptyWithConfig()
    {
        BacktestConfiguration config = MakeConfig();
        BacktestInput input = MakeInput(ImmutableArray<CandleData>.Empty);

        BacktestResult result = CreateEngine().Run(input, config, new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>()));

        Assert.True(result.IsInsufficientData);
        Assert.Same(config, result.Configuration);
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public void StrictEvidenceConfiguration_IsRejectedByLegacyEngine()
    {
        BacktestConfiguration config = MakeConfig(executionModel: ExecutionModel.StrictEvidence);
        BacktestInput input = MakeInput(ImmutableArray<CandleData>.Empty);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            CreateEngine().Run(input, config, new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>())));

        Assert.Contains("IStrictBacktestEngine", error.Message);
    }

    [Fact]
    public void SingleBar_NoTrade_EquityEqualsInitialCapital()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Bar(0, 100m, 105m, 95m, 102m));
        BacktestInput input = MakeInput(bars);

        BacktestResult result = CreateEngine().Run(input, config, new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>()));

        Assert.False(result.IsInsufficientData);
        Assert.Empty(result.Trades);
        Assert.Single(result.EquityPoints);
        Assert.Equal(1000m, result.EquityPoints[0].Equity);
    }

    [Fact]
    public void GoldenLong_MarketEntryThenExit_NetPnLMatchesSignConvention()
    {
        // bar0 Close=100 -> LongEntry(Market) signal -> fills at bar1 Open=101.
        // bar1 Close=108 -> LongExit(Market) signal -> fills at bar2 Open=110.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 101m, 112m, 101m, 108m),
            Bar(2, 110m, 115m, 109m, 111m));
        BacktestInput input = MakeInput(bars);
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 1m, initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = LongEntryMarket(),
            [1] = LongExitMarket(),
        });

        BacktestResult result = CreateEngine().Run(input, config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(101m, trade.EntryPrice);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(9m, trade.ClosedGross); // 1 * (110 - 101)
        Assert.False(trade.IsForcedLiquidation);
        Assert.Equal(1009m, result.EquityPoints[^1].Equity); // 1000 + 9, no fees/slippage
    }

    [Fact]
    public void MocEntry_FillsAtCloseNotOpen()
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 105m, 112m, 104m, 108m));
        BacktestInput input = MakeInput(bars);
        BacktestConfiguration config = MakeConfig(initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntryMoc() });

        BacktestResult result = CreateEngine().Run(input, config, strategy);

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(108m, fill.Price); // Close, not Open (105)
    }

    [Fact]
    public void MarginLiquidation_PartialLossCut_RunContinues_TradeFlaggedForced()
    {
        // Q=20 @ EntryPrice=100: notional=2000, InitialMarginRequired=600, Cash after=400, HeldMargin=600.
        // P_liq = (2000 - 400 - 600) / (20*0.8) = 1000/16 = 62.5.
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.30m, maintenanceMarginRatio: 0.20m, liquidationPenaltyRatio: 0m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),   // signal bar
            Bar(1, 100m, 100m, 100m, 100m),   // entry fills at Open=100
            Bar(2, 90m, 91m, 60m, 65m));      // Low=60 breaches P_liq=62.5
        BacktestInput input = MakeInput(bars);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntryMarket() });

        BacktestResult result = CreateEngine().Run(input, config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status); // Equity>0 post-liquidation -> run continues
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(62.5m, trade.ExitPrice);
    }

    [Fact]
    public void MarginLiquidation_EquityZeroOrBelow_StopsWithInsolvent()
    {
        // Q=100 @ EntryPrice=100, InitialMarginRatio=0.10: notional=10000, InitialMarginRequired=1000,
        // Cash after=0, HeldMargin=1000. P_liq = (10000 - 0 - 1000)/(100*0.95) = 9000/95 = 94.7368...
        // bar2 Open=90 already below P_liq -> breaches immediately at Open; the 50% liquidation penalty
        // plus the crash drives realized loss well past the remaining account value.
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 100m, initialMarginRatio: 0.10m, maintenanceMarginRatio: 0.05m, liquidationPenaltyRatio: 0.5m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m),
            Bar(2, 90m, 91m, 10m, 15m));
        BacktestInput input = MakeInput(bars);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntryMarket() });

        BacktestResult result = CreateEngine().Run(input, config, strategy);

        Assert.Equal(RunStatus.Insolvent, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.True(result.EquityPoints[^1].Equity <= 0m);
    }

    [Fact]
    public void GtdOrder_ExpiresStrictlyAfterExpiryBar_NeverFills()
    {
        BacktestConfiguration config = MakeConfig(initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 200m, 200m, 200m, 200m),
            Bar(2, 200m, 200m, 200m, 200m),
            Bar(3, 200m, 200m, 200m, 200m));
        BacktestInput input = MakeInput(bars);
        var gtd = new TimeInForce(false, 2); // expires strictly after bar index 2
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = new StrategyOrderRequest(SignalType.LongEntry, OrderType.Limit, 50m, null, gtd, "gtd-limit"),
        });

        BacktestResult result = CreateEngine().Run(input, config, strategy);

        Assert.Empty(result.Fills);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ExpiredReason.GTDExpired, order.ExpiredReason);
    }

    [Fact]
    public void InsufficientFunds_EntryRejected_NoPartialFill()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 50m, sizingParameter: 10m, initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m));
        BacktestInput input = MakeInput(bars);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntryMarket() });

        BacktestResult result = CreateEngine().Run(input, config, strategy);

        Assert.Empty(result.Fills);
        Assert.Empty(result.Trades);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(RejectedReason.InsufficientFunds, order.RejectedReason);
    }

    [Fact]
    public void ConflictingSignal_WhileAlreadyLong_RejectedPositionConflict()
    {
        // A LongEntry signal fired again on the same bar the first entry fills (Q is already Long by
        // Step7 of that bar) hits the defensive PositionConflict guard in SubmitOrderFromSignal - a
        // conforming strategy should never do this, but the engine must reject it explicitly rather than
        // silently drop or double-apply it.
        BacktestConfiguration config = MakeConfig(initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 105m, 95m, 102m));
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = LongEntryMarket(),
            [1] = LongEntryMarket("duplicate-entry"),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(2, result.Orders.Length);
        BacktestOrder secondOrder = result.Orders[1];
        Assert.Equal(OrderStatus.Rejected, secondOrder.Status);
        Assert.Equal(RejectedReason.PositionConflict, secondOrder.RejectedReason);
        Assert.Single(result.Fills); // only the first entry ever filled
    }

    [Fact]
    public void PercentOfEquity_RoundsDownToZero_RejectedInvalidQuantity()
    {
        // PercentOfEquity sizing with a tiny SizingParameter floors to Q=0, hitting the defensive
        // InvalidQuantity guard in SubmitOrderFromSignal before any fill is even attempted.
        BacktestConfiguration config = MakeConfig(initialCapital: 100m, sizingModel: PositionSizingModel.PercentOfEquity, sizingParameter: 0.001m, initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 105m, 95m, 102m));
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntryMarket() });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Empty(result.Fills);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(RejectedReason.InvalidQuantity, order.RejectedReason);
    }

    [Fact]
    public void ReproducibilityHash_SameInputsSameHash()
    {
        BacktestConfiguration config = MakeConfig(initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 101m, 112m, 101m, 108m),
            Bar(2, 110m, 115m, 109m, 111m));
        BacktestInput input = MakeInput(bars);
        var strategy1 = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntryMarket(), [1] = LongExitMarket() });
        var strategy2 = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntryMarket(), [1] = LongExitMarket() });

        BacktestResult result1 = CreateEngine().Run(input, config, strategy1);
        BacktestResult result2 = CreateEngine().Run(input, config, strategy2);

        Assert.Equal(result1.ReproducibilityHash, result2.ReproducibilityHash);
    }

    /// <summary>P3 Hardening Task 5: a pre-cancelled token must abort the bar loop via
    /// <c>ThrowIfCancellationRequested()</c> rather than run to completion - proves the Run(...) overload
    /// with the new CancellationToken parameter actually observes it.</summary>
    [Fact]
    public void Run_PreCancelledToken_ThrowsOperationCanceledException_BeforeBarLoop()
    {
        BacktestConfiguration config = MakeConfig();
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 105m, 95m, 102m),
            Bar(1, 102m, 108m, 100m, 106m));
        BacktestInput input = MakeInput(bars);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateEngine().Run(input, config, new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>()), cts.Token));
    }

    [Fact]
    public void Run_PreCancelledToken_WithEmptyInput_ThrowsOperationCanceledException()
    {
        BacktestInput input = MakeInput(ImmutableArray<CandleData>.Empty);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException error = Assert.Throws<OperationCanceledException>(
            () => CreateEngine().Run(input, MakeConfig(), new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>()), cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
    }

    /// <summary>P3 Hardening Task 5: the Gate G2 indicator-preparation loop must also observe cancellation,
    /// independent of the bar loop's own check - a strategy that requests at least one indicator exercises
    /// PrepareIndicators' per-request ThrowIfCancellationRequested() before any bar is ever processed.</summary>
    [Fact]
    public void Run_PreCancelledToken_ThrowsDuringIndicatorPreparation()
    {
        BacktestConfiguration config = MakeConfig();
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Bar(0, 100m, 105m, 95m, 102m));
        BacktestInput input = MakeInput(bars);
        var strategy = new ScriptedStrategy(
            new Dictionary<int, StrategyOrderRequest>(),
            requiredIndicators: new[] { new StrategyIndicatorRequest("sma", IndicatorType.SMA, null) });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => CreateEngine().Run(input, config, strategy, cts.Token));
    }

    /// <summary>Default-parameter regression guard (P3 Hardening Task 5): every pre-existing 3-argument
    /// call in this file (and BacktestAcceptanceTests.cs) targets this concrete BacktestEngine type
    /// directly, so the optional CancellationToken must be declared on the class itself, not only on
    /// IBacktestEngine - this is a compile-time proof, and a passing run confirms unrelated (non-cancelled)
    /// behavior is unchanged.</summary>
    [Fact]
    public void Run_WithoutCancellationTokenArgument_StillCompilesAndCompletes()
    {
        BacktestConfiguration config = MakeConfig();
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Bar(0, 100m, 105m, 95m, 102m));
        BacktestInput input = MakeInput(bars);

        BacktestResult result = CreateEngine().Run(input, config, new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>()));

        Assert.Equal(RunStatus.Completed, result.Status);
    }
}
