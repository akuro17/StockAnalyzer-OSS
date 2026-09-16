using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Task #6: the bar-loop orchestrator (Y:\0915 Backtesting\01_P1_SimulationEngine.md section 5.5, Steps
/// 1-9), amended per Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5 for the margin-account
/// Long/Short model. Consumes the task #5 fill-evaluation primitives
/// (<see cref="OrderFillEvaluator"/>, <see cref="MarginLiquidationCalculator"/>,
/// <see cref="ExitPrecedenceResolver"/>) and wires indicator batch-prep (Gate G2) through
/// <see cref="IIndicatorFactory"/>.
/// </summary>
public sealed class BacktestEngine : IBacktestEngine
{
    /// <summary>Sentinel BacktestFill.OrderId for a forced-liquidation fill, which has no originating BacktestOrder. OrderIds are sequential starting at 1, so 0 is never a real order.</summary>
    private const long NoOriginatingOrderId = 0L;

    private readonly IIndicatorFactory _indicatorFactory;
    private readonly ILogger<BacktestEngine> _logger;

    /// <summary>
    /// <paramref name="logger"/> defaults to null (null-object pattern via <see cref="NullLogger{T}"/>)
    /// per SA_ARCHITECTURE_RULES.md section 2 "Optional Dependency Safety (Instrumentation)" - required on
    /// every StockAnalyzer.Core service regardless of perceived simplicity, so observability is available
    /// without forcing every caller/test to supply one.
    /// </summary>
    public BacktestEngine(IIndicatorFactory indicatorFactory, ILogger<BacktestEngine>? logger = null)
    {
        _indicatorFactory = indicatorFactory ?? throw new ArgumentNullException(nameof(indicatorFactory));
        _logger = logger ?? NullLogger<BacktestEngine>.Instance;
    }

    public BacktestResult Run(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (strategy is null) throw new ArgumentNullException(nameof(strategy));
        configuration.Validate();

        int n = input.Bars.Length;
        if (n == 0)
        {
            return BacktestResult.CreateEmpty(configuration);
        }

        IndicatorSeriesSet indicatorSeries = PrepareIndicators(input, strategy);

        var s = new RunState
        {
            Cash = configuration.InitialCapital,
            Orders = new List<BacktestOrder>(n),
            Fills = new List<BacktestFill>(n),
            Trades = new List<BacktestTrade>(Math.Max(1, n / 2)),
        };
        var equityPoints = new List<EquityPoint>(n + 1);
        var signals = new List<BacktestSignal>(n);

        RunStatus runStatus = RunStatus.Completed;

        for (int i = 0; i < n; i++)
        {
            CandleData bar = input.Bars[i];

            // Step 1: cancellations queued from the previous bar's Close (GTD expiry check; "i > ExpiryBar" is strictly greater - the expiry bar itself may still fill).
            if (s.PendingOrderIndex != -1)
            {
                BacktestOrder pending = s.Orders[s.PendingOrderIndex];
                if (!pending.TimeInForce.IsGoodTilCancelled && i > pending.TimeInForce.ExpiryBar)
                {
                    s.Orders[s.PendingOrderIndex] = pending with { Status = OrderStatus.Expired, ExpiredReason = ExpiredReason.GTDExpired };
                    s.PendingOrderIndex = -1;
                    s.PendingOrderIsEntry = false;
                }
            }

            // Step 2 (+ margin-call precedence, section 1.5.6): evaluate the pending order and/or a maintenance-margin breach on the same O->H->L->C path.
            bool insolventFromLiquidation = EvaluateStep2(s, i, bar, configuration);
            if (insolventFromLiquidation)
            {
                // Flat immediately after a forced liquidation: HeldMargin=0, UnrealizedPnL=0, so Equity == Cash.
                equityPoints.Add(new EquityPoint(i, bar.Timestamp, s.Cash, s.Cash, 0m, 0m));
                runStatus = RunStatus.Insolvent;
                break;
            }

            // Step 4: MarketOnClose evaluation at Close, unconditional, never competes with margin liquidation on the O->H->L path (see MarginLiquidationCalculator remarks).
            if (s.PendingOrderIndex != -1 && s.Orders[s.PendingOrderIndex].Type == OrderType.MarketOnClose && s.Orders[s.PendingOrderIndex].EarliestFillBar <= i)
            {
                OrderFillOutcome mocOutcome = OrderFillEvaluator.EvaluateMarketOnClose(s.Orders[s.PendingOrderIndex], bar, configuration.SlippageRatio, configuration.CommissionFlat, configuration.CommissionPerUnit);
                CommitOrRejectFill(s, i, bar, mocOutcome, configuration);
            }

            // Step 5: mark to market at Close (section 1.5.5).
            decimal unrealizedPnL = s.Q * (bar.Close - s.EntryPrice);
            decimal equity = s.Cash + s.HeldMargin + unrealizedPnL;
            decimal marketValue = s.Q * bar.Close;

            // Step 6: indicators were already precomputed in full before the loop (Gate G2) - nothing to do per-bar; StrategyContext below exposes them via index.

            // Step 7: strategy evaluation - at most once per bar, only while trading is active and solvent.
            if (i >= input.TradingStartIndex && equity > 0m)
            {
                TradeSide? positionSide = s.Q > 0m ? TradeSide.Long : s.Q < 0m ? TradeSide.Short : null;
                var context = new StrategyContext
                {
                    BarIndex = i,
                    Bar = bar,
                    PositionSide = positionSide,
                    Snapshot = new EquityPoint(i, bar.Timestamp, equity, s.Cash, marketValue, s.HeldMargin),
                    PendingOrderId = s.PendingOrderIndex == -1 ? null : s.Orders[s.PendingOrderIndex].OrderId,
                    Indicators = indicatorSeries,
                };

                StrategyOrderRequest? request = strategy.Evaluate(context);
                if (request is { } req)
                {
                    signals.Add(new BacktestSignal(req.SignalType, i, bar.Timestamp, req.Reason ?? string.Empty));
                    if (s.PendingOrderIndex == -1)
                    {
                        SubmitOrderFromSignal(s, i, bar, req, configuration, equity);
                    }
                    // A still-pending order is never overwritten (v1 max-1-pending-order rule) - the signal is still logged above, but no order is submitted for it.
                }
            }

            // Step 8: record the post-Step-5/6 Close snapshot.
            equityPoints.Add(new EquityPoint(i, bar.Timestamp, equity, s.Cash, marketValue, s.HeldMargin));

            // Step 9: end-of-bar insolvency check. Open positions stay marked at their last market value - they are not force-liquidated here (that is Step 2's job on a LATER bar).
            if (equity <= 0m)
            {
                if (s.PendingOrderIndex != -1)
                {
                    s.Orders[s.PendingOrderIndex] = s.Orders[s.PendingOrderIndex] with { Status = OrderStatus.Expired, ExpiredReason = ExpiredReason.Insolvency };
                    s.PendingOrderIndex = -1;
                }
                runStatus = RunStatus.Insolvent;
                break;
            }
        }

        // Task #8 fix: when the loop runs through every bar without an early Insolvent/failure break, a
        // still-pending order that never filled must be expired with reason EndOfData - the open position
        // itself (if any) is left untouched, exactly mirroring Step 9's "residual orders expire, positions
        // stay marked at their last market value" rule for the Insolvency case. This was a gap in task #6:
        // ExpiredReason.EndOfData existed on the enum but nothing ever assigned it.
        if (runStatus == RunStatus.Completed && s.PendingOrderIndex != -1)
        {
            s.Orders[s.PendingOrderIndex] = s.Orders[s.PendingOrderIndex] with { Status = OrderStatus.Expired, ExpiredReason = ExpiredReason.EndOfData };
            s.PendingOrderIndex = -1;
        }

        byte[] hash = ComputeReproducibilityHash(input, configuration, strategy.Name, s.Orders, s.Fills, s.Trades, equityPoints, signals);

        return new BacktestResult(
            s.Orders.ToImmutableArray(),
            s.Fills.ToImmutableArray(),
            s.Trades.ToImmutableArray(),
            equityPoints.ToImmutableArray(),
            signals.ToImmutableArray(),
            configuration,
            runStatus,
            strategy.Name,
            hash,
            isInsufficientData: false);
    }

    /// <summary>Mutable per-run account/bookkeeping state. Kept as a single object (not a long ref-parameter list) so the Step helpers below stay readable.</summary>
    private sealed class RunState
    {
        public decimal Cash;
        public decimal HeldMargin;
        public decimal Q;
        public decimal EntryPrice;
        public decimal EntryFeeAccum;
        public int EntryBar = -1;
        public DateTime EntryTime;
        public int PendingOrderIndex = -1;
        public bool PendingOrderIsEntry;
        public TradeSide PendingEntrySide;
        public long NextOrderId = 1;
        public long NextFillId = 1;
        public long NextTradeId = 1;
        public List<BacktestOrder> Orders = null!;
        public List<BacktestFill> Fills = null!;
        public List<BacktestTrade> Trades = null!;
    }

    /// <summary>
    /// Step 2 + section 1.5.6's margin-call precedence. Returns true only when a forced liquidation this
    /// bar leaves the account insolvent (post-liquidation Equity &lt;= 0), signalling the caller to stop
    /// the run immediately per section 1.5.6's user-confirmed run-continuation rule.
    /// </summary>
    private static bool EvaluateStep2(RunState s, int i, CandleData bar, BacktestConfiguration config)
    {
        bool pendingEligible = s.PendingOrderIndex != -1
            && s.Orders[s.PendingOrderIndex].Type != OrderType.MarketOnClose
            && s.Orders[s.PendingOrderIndex].EarliestFillBar <= i;

        if (s.Q == 0m)
        {
            return pendingEligible && EvaluatePendingOrderTouch(s, i, bar, config, marginPosition: null, openSide: null, liquidationPrice: 0m);
        }

        TradeSide openSide = s.Q > 0m ? TradeSide.Long : TradeSide.Short;
        decimal liquidationPrice = MarginLiquidationCalculator.ComputeLiquidationPrice(openSide, Math.Abs(s.Q), s.EntryPrice, s.Cash, s.HeldMargin, config.MaintenanceMarginRatio);
        decimal? marginPosition = MarginLiquidationCalculator.TryFindBreachPosition(openSide, liquidationPrice, bar);
        decimal? pendingPosition = pendingEligible ? OrderFillEvaluator.TryFindCandidatePosition(s.Orders[s.PendingOrderIndex], bar) : null;

        ExitWinner winner = ExitPrecedenceResolver.Resolve(pendingPosition, marginPosition);
        if (winner == ExitWinner.MarginLiquidation)
        {
            return ExecuteForcedLiquidation(s, i, bar, config, openSide, liquidationPrice);
        }

        // winner is PendingOrder or None. If pendingEligible, still evaluate/persist it (StopActivated
        // must be persisted even on a no-touch bar); if the "winning" order does not actually resolve
        // into a fill this bar (e.g. a StopLimit that only activates), a same-bar margin breach that
        // exists independently still applies as a fallback - see EvaluatePendingOrderTouch's remarks.
        return pendingEligible && EvaluatePendingOrderTouch(s, i, bar, config, marginPosition, openSide, liquidationPrice);
    }

    /// <summary>
    /// Evaluates the current pending order against the bar, persists any StopActivated transition, and
    /// commits/rejects a fill if one occurred. USER-CONFIRMED (2026-09-16): if the pending order "won"
    /// precedence over the margin threshold but does not itself result in an actual fill this bar (e.g.
    /// a StopLimit that only activates, touching before the margin threshold but never reaching its
    /// Limit leg), and a margin breach independently exists later in the same bar, the margin
    /// liquidation still fires as a fallback - because an order that only activates without filling
    /// never actually closes the position, so nothing should shield the account from a real
    /// maintenance-margin breach. Pass
    /// <paramref name="openSide"/> null to disable this fallback (used for the Flat / no-position path).
    /// </summary>
    private static bool EvaluatePendingOrderTouch(RunState s, int i, CandleData bar, BacktestConfiguration config, decimal? marginPosition, TradeSide? openSide, decimal liquidationPrice)
    {
        OrderFillOutcome outcome = OrderFillEvaluator.Evaluate(s.Orders[s.PendingOrderIndex], bar, config.SlippageRatio, config.CommissionFlat, config.CommissionPerUnit);
        if (outcome.StopActivated != s.Orders[s.PendingOrderIndex].StopActivated)
        {
            s.Orders[s.PendingOrderIndex] = s.Orders[s.PendingOrderIndex] with { StopActivated = outcome.StopActivated };
        }

        if (outcome.Filled)
        {
            CommitOrRejectFill(s, i, bar, outcome, config);
            return false;
        }

        if (openSide is not null && marginPosition is not null)
        {
            return ExecuteForcedLiquidation(s, i, bar, config, openSide.Value, liquidationPrice);
        }

        return false;
    }

    /// <summary>
    /// Section 1.5.6's execution-on-breach + section 1.5.6's user-confirmed run-continuation rule: closes
    /// the position at the penalized forced-liquidation price, then reports whether the account is now
    /// insolvent (Equity &lt;= 0 while Flat, i.e. Cash &lt;= 0) so the caller stops the run, or whether it
    /// should continue as a partial loss-cut (Equity &gt; 0).
    /// </summary>
    private static bool ExecuteForcedLiquidation(RunState s, int i, CandleData bar, BacktestConfiguration config, TradeSide openSide, decimal liquidationPrice)
    {
        if (s.PendingOrderIndex != -1)
        {
            s.Orders[s.PendingOrderIndex] = s.Orders[s.PendingOrderIndex] with { Status = OrderStatus.Cancelled };
            s.PendingOrderIndex = -1;
            s.PendingOrderIsEntry = false;
        }

        (decimal fillPrice, decimal commission) = MarginLiquidationCalculator.ComputeForcedLiquidationFill(
            openSide, liquidationPrice, Math.Abs(s.Q), config.LiquidationPenaltyRatio, config.CommissionFlat, config.CommissionPerUnit);
        OrderSide closingSide = openSide == TradeSide.Long ? OrderSide.Sell : OrderSide.Buy;
        decimal quantity = Math.Abs(s.Q);
        decimal slippageAmount = FillPricing.ComputeSlippageAmount(fillPrice, liquidationPrice, quantity);

        ClosePosition(s, i, bar, openSide, NoOriginatingOrderId, closingSide, fillPrice, quantity, commission, slippageAmount, isForcedLiquidation: true);

        return s.Cash <= 0m;
    }

    /// <summary>
    /// Shared position-closing accounting for a full-quantity close: realizes PnL, releases HeldMargin
    /// back into Cash, records the Fill/Trade entries, and resets RunState to Flat. Used by both a
    /// normal exit fill (<see cref="CommitExitFill"/>) and a forced margin liquidation
    /// (<see cref="ExecuteForcedLiquidation"/>), since both perform the exact same account-state
    /// transition (sections 1.5.4 and 1.5.6) - kept as a single method rather than duplicated inline.
    /// </summary>
    private static void ClosePosition(RunState s, int i, CandleData bar, TradeSide side, long originatingOrderId, OrderSide fillSide, decimal fillPrice, decimal quantity, decimal commission, decimal slippageAmount, bool isForcedLiquidation)
    {
        decimal realizedPnL = s.Q * (fillPrice - s.EntryPrice);
        decimal closedNet = realizedPnL - s.EntryFeeAccum - commission;

        s.Cash = s.Cash + s.HeldMargin + realizedPnL - commission;

        s.Fills.Add(new BacktestFill(s.NextFillId++, originatingOrderId, i, bar.Timestamp, fillSide, fillPrice, quantity, commission, slippageAmount));
        s.Trades.Add(new BacktestTrade(s.NextTradeId++, side, s.EntryBar, s.EntryTime, s.EntryPrice, i, bar.Timestamp, fillPrice, quantity, realizedPnL, closedNet, s.EntryFeeAccum, commission, i - s.EntryBar, IsForcedLiquidation: isForcedLiquidation));

        s.HeldMargin = 0m;
        s.Q = 0m;
        s.EntryPrice = 0m;
        s.EntryFeeAccum = 0m;
        s.EntryBar = -1;
    }

    private static void CommitOrRejectFill(RunState s, int i, CandleData bar, OrderFillOutcome outcome, BacktestConfiguration config)
    {
        if (s.PendingOrderIsEntry)
        {
            CommitEntryFill(s, i, bar, outcome, config);
        }
        else
        {
            CommitExitFill(s, i, bar, outcome);
        }
    }

    /// <summary>Section 1.5.3 entry + funds check. Rejected orders leave account state untouched (Flat stays Flat).</summary>
    private static void CommitEntryFill(RunState s, int i, CandleData bar, OrderFillOutcome outcome, BacktestConfiguration config)
    {
        BacktestOrder order = s.Orders[s.PendingOrderIndex];
        decimal quantity = order.Quantity;
        decimal notional = quantity * outcome.FillPrice;
        decimal initialMarginRequired = notional * config.InitialMarginRatio;

        if (initialMarginRequired + outcome.Commission <= s.Cash)
        {
            s.Cash -= initialMarginRequired + outcome.Commission;
            s.HeldMargin = initialMarginRequired;
            s.Q = s.PendingEntrySide == TradeSide.Long ? quantity : -quantity;
            s.EntryPrice = outcome.FillPrice;
            s.EntryFeeAccum = outcome.Commission;
            s.EntryBar = i;
            s.EntryTime = bar.Timestamp;

            s.Fills.Add(new BacktestFill(s.NextFillId++, order.OrderId, i, bar.Timestamp, order.Side, outcome.FillPrice, quantity, outcome.Commission, outcome.SlippageAmount));
            s.Orders[s.PendingOrderIndex] = order with { Status = OrderStatus.Filled, StopActivated = outcome.StopActivated };
        }
        else
        {
            s.Orders[s.PendingOrderIndex] = order with { Status = OrderStatus.Rejected, RejectedReason = RejectedReason.InsufficientFunds };
        }

        s.PendingOrderIndex = -1;
        s.PendingOrderIsEntry = false;
    }

    /// <summary>Section 1.5.4 exit - never funds-rejected; Cash may legitimately go negative here (feeds Step 9).</summary>
    private static void CommitExitFill(RunState s, int i, CandleData bar, OrderFillOutcome outcome)
    {
        BacktestOrder order = s.Orders[s.PendingOrderIndex];
        TradeSide side = s.Q > 0m ? TradeSide.Long : TradeSide.Short;
        decimal quantity = Math.Abs(s.Q);

        ClosePosition(s, i, bar, side, order.OrderId, order.Side, outcome.FillPrice, quantity, outcome.Commission, outcome.SlippageAmount, isForcedLiquidation: false);

        s.Orders[s.PendingOrderIndex] = order with { Status = OrderStatus.Filled, StopActivated = outcome.StopActivated };
        s.PendingOrderIndex = -1;
        s.PendingOrderIsEntry = false;
    }

    /// <summary>
    /// Step 7's order submission: resolves a SignalType into a sized order. Defensive PositionConflict/
    /// InvalidQuantity rejections are recorded into Orders history (never silently dropped) even though
    /// the engine's own structural checks (Entry only allowed while Flat, Exit only while holding the
    /// matching side) mean a conforming strategy should never trigger them.
    /// </summary>
    private static void SubmitOrderFromSignal(RunState s, int i, CandleData bar, StrategyOrderRequest req, BacktestConfiguration config, decimal equity)
    {
        bool isEntry;
        OrderSide side;
        TradeSide entrySide = default;
        bool conflict;

        switch (req.SignalType)
        {
            case SignalType.LongEntry:
                side = OrderSide.Buy; isEntry = true; entrySide = TradeSide.Long; conflict = s.Q != 0m; break;
            case SignalType.ShortEntry:
                side = OrderSide.Sell; isEntry = true; entrySide = TradeSide.Short; conflict = s.Q != 0m; break;
            case SignalType.LongExit:
                side = OrderSide.Sell; isEntry = false; conflict = s.Q <= 0m; break;
            case SignalType.ShortExit:
                side = OrderSide.Buy; isEntry = false; conflict = s.Q >= 0m; break;
            case SignalType.None:
                return; // No order to submit.
            default:
                throw new ArgumentOutOfRangeException(nameof(req), req.SignalType, "Unknown SignalType.");
        }

        if (conflict)
        {
            s.Orders.Add(new BacktestOrder(s.NextOrderId++, side, req.OrderType, 0m, req.LimitPrice, req.StopPrice, OrderStatus.Rejected, req.TimeInForce, i, i + 1, false, null, RejectedReason.PositionConflict));
            return;
        }

        decimal quantity = isEntry ? ComputeEntryQuantity(config, equity, bar.Close) : Math.Abs(s.Q);
        if (isEntry && quantity < 1m)
        {
            s.Orders.Add(new BacktestOrder(s.NextOrderId++, side, req.OrderType, 0m, req.LimitPrice, req.StopPrice, OrderStatus.Rejected, req.TimeInForce, i, i + 1, false, null, RejectedReason.InvalidQuantity));
            return;
        }

        var order = new BacktestOrder(s.NextOrderId++, side, req.OrderType, quantity, req.LimitPrice, req.StopPrice, OrderStatus.Submitted, req.TimeInForce, i, i + 1, false, null, null);
        s.Orders.Add(order);
        s.PendingOrderIndex = s.Orders.Count - 1;
        s.PendingOrderIsEntry = isEntry;
        if (isEntry) s.PendingEntrySide = entrySide;
    }

    /// <summary>Section 1.5.7 sizing, frozen at signal time using the signal bar's Close as the reference price (the actual next-bar fill price is not yet known).</summary>
    private static decimal ComputeEntryQuantity(BacktestConfiguration config, decimal equity, decimal estimatePrice)
    {
        if (config.SizingModel == PositionSizingModel.FixedQuantity)
        {
            return config.SizingParameter;
        }

        decimal budget = equity * config.SizingParameter;
        if (budget <= config.CommissionFlat) return 0m;
        decimal denominator = (estimatePrice * config.InitialMarginRatio) + config.CommissionPerUnit;
        return Math.Floor((budget - config.CommissionFlat) / denominator);
    }

    /// <summary>Gate G2: create + calculate each requested indicator once over the full bar array before the loop starts.</summary>
    private IndicatorSeriesSet PrepareIndicators(BacktestInput input, IBacktestStrategy strategy)
    {
        IReadOnlyList<StrategyIndicatorRequest> requests = strategy.GetRequiredIndicators();
        var series = new Dictionary<string, ImmutableArray<decimal?>>(requests.Count);
        if (requests.Count == 0) return new IndicatorSeriesSet(series);

        var coreCandles = new CoreCandleData[input.Bars.Length];
        for (int i = 0; i < coreCandles.Length; i++)
        {
            CandleData bar = input.Bars[i];
            coreCandles[i] = new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        }

        foreach (StrategyIndicatorRequest request in requests)
        {
            ICoreIndicator indicator = _indicatorFactory.Create(request.Type, request.Parameters)
                ?? throw new InvalidOperationException($"Indicator type '{request.Type}' is not registered in IIndicatorFactory.");
            IIndicatorResult result = indicator.Calculate(coreCandles);
            series[request.Key] = result.MainValues.ToImmutableArray();
        }

        return new IndicatorSeriesSet(series);
    }

    /// <summary>SHA-256 over a deterministic byte serialization of every input/config/output field that affects the run's outcome, per BacktestResult's own "same input+config+strategy must hash identically" contract.</summary>
    private static byte[] ComputeReproducibilityHash(
        BacktestInput input, BacktestConfiguration config, string strategyName,
        List<BacktestOrder> orders, List<BacktestFill> fills, List<BacktestTrade> trades,
        List<EquityPoint> equityPoints, List<BacktestSignal> signals)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(input.Symbol);
            writer.Write((int)input.Frame);
            writer.Write(input.DataVersion);
            writer.Write(input.Bars.Length);
            writer.Write(strategyName);
            WriteDecimal(writer, config.InitialCapital);
            WriteDecimal(writer, config.CommissionFlat);
            WriteDecimal(writer, config.CommissionPerUnit);
            WriteDecimal(writer, config.SlippageRatio);
            WriteDecimal(writer, config.InitialMarginRatio);
            WriteDecimal(writer, config.MaintenanceMarginRatio);
            WriteDecimal(writer, config.LiquidationPenaltyRatio);
            writer.Write((int)config.SizingModel);
            WriteDecimal(writer, config.SizingParameter);

            foreach (BacktestOrder o in orders)
            {
                writer.Write(o.OrderId);
                writer.Write((int)o.Side);
                writer.Write((int)o.Type);
                WriteDecimal(writer, o.Quantity);
                WriteNullableDecimal(writer, o.LimitPrice);
                WriteNullableDecimal(writer, o.StopPrice);
                writer.Write((int)o.Status);
                writer.Write(o.SubmittedBar);
                writer.Write(o.EarliestFillBar);
            }
            foreach (BacktestFill f in fills)
            {
                writer.Write(f.FillId);
                writer.Write(f.OrderId);
                writer.Write(f.BarIndex);
                WriteDecimal(writer, f.Price);
                WriteDecimal(writer, f.Quantity);
                WriteDecimal(writer, f.Commission);
            }
            foreach (BacktestTrade t in trades)
            {
                writer.Write(t.TradeId);
                writer.Write((int)t.Side);
                writer.Write(t.EntryBar);
                writer.Write(t.ExitBar);
                WriteDecimal(writer, t.EntryPrice);
                WriteDecimal(writer, t.ExitPrice);
                WriteDecimal(writer, t.ClosedNet);
                writer.Write(t.IsForcedLiquidation);
            }
            foreach (EquityPoint e in equityPoints)
            {
                writer.Write(e.BarIndex);
                WriteDecimal(writer, e.Equity);
                WriteDecimal(writer, e.Cash);
                WriteDecimal(writer, e.HeldMargin);
            }
            foreach (BacktestSignal sig in signals)
            {
                writer.Write((int)sig.Type);
                writer.Write(sig.BarIndex);
            }
        }

        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteDecimal(BinaryWriter writer, decimal value)
    {
        foreach (int part in decimal.GetBits(value)) writer.Write(part);
    }

    private static void WriteNullableDecimal(BinaryWriter writer, decimal? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) WriteDecimal(writer, value.Value);
    }
}
