using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
public sealed class BacktestEngine : IBacktestEngine, IBacktestDiagnosticEngine
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

    /// <summary>
    /// <paramref name="cancellationToken"/> defaults to <see cref="CancellationToken.None"/> (P3 Hardening
    /// Task 5, safe extension - see <see cref="IBacktestEngine.Run"/>): the default here (not just on the
    /// interface) is required because several existing tests call this concrete <see cref="BacktestEngine"/>
    /// type directly (bypassing the interface) with the original 3-argument call, and C# does not inherit
    /// an interface method's default parameter value onto a class's own declaration.
    /// </summary>
    public BacktestResult Run(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy,
        CancellationToken cancellationToken = default)
        => RunCore(input, configuration, strategy, cancellationToken, captureArithmeticFailures: false).Result;

    /// <summary>
    /// Owner decision G3 (A1 (Run diagnostics)): the same simulation as <see cref="Run"/> - one shared implementation - where an
    /// OverflowException raised by the engine's own arithmetic becomes a <see cref="RunStatus.Failed"/> result (the committed prefix of bars) plus a diagnostic.
    /// </summary>
    public BacktestDiagnosedRun RunWithDiagnostics(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy,
        CancellationToken cancellationToken = default)
        => RunCore(input, configuration, strategy, cancellationToken, captureArithmeticFailures: true);

    /// <param name="captureArithmeticFailures">
    /// False (legacy <see cref="Run"/>): the exception filters below never match, so every exception propagates untouched. True: only an
    /// OverflowException raised outside a strategy call is converted; validation, strategy, cancellation and unknown exceptions still propagate.
    /// </param>
    private BacktestDiagnosedRun RunCore(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy,
        CancellationToken cancellationToken, bool captureArithmeticFailures)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (strategy is null) throw new ArgumentNullException(nameof(strategy));
        configuration.Validate();
        if (configuration.ExecutionModel == ExecutionModel.StrictEvidence)
        {
            throw new NotSupportedException(
                "StrictEvidence cannot run through the Legacy OHLC engine. Use IStrictBacktestEngine with explicit evidence input.");
        }
        if (configuration.ExecutionModel == ExecutionModel.YFinanceApproximate &&
            (input.Frame != TimeFrame.D1 || input.AdditionalTimeframeBars is { Count: > 0 }))
        {
            throw new NotSupportedException("YFinanceApproximate requires only D1 input without foreign-timeframe bars.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        int n = input.Bars.Length;
        if (n == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new BacktestDiagnosedRun(BacktestResult.CreateEmpty(configuration), null);
        }

        // A strategy call, kept outside the arithmetic scope below: an OverflowException from it is a strategy failure, never an engine diagnostic.
        IReadOnlyList<StrategyIndicatorRequest> requests = strategy.GetRequiredIndicators();
        IndicatorSeriesSet indicatorSeries;
        try
        {
            indicatorSeries = PrepareIndicators(input, requests, cancellationToken);
        }
        catch (OverflowException ex) when (captureArithmeticFailures)
        {
            // A batch indicator calculation has no bar: the diagnostic carries none and the committed prefix is empty.
            var diagnostic = new BacktestRunDiagnostic(BacktestDiagnosticCode.ArithmeticOverflow, BacktestRunPhase.Preparation, null, BacktestArithmeticOperation.Unknown, ex.Message);
            return new BacktestDiagnosedRun(BuildResult(input, configuration, strategy, new RunState { Orders = new List<BacktestOrder>(), Fills = new List<BacktestFill>(), Trades = new List<BacktestTrade>(), EquityPoints = new List<EquityPoint>(), Signals = new List<BacktestSignal>() }, RunStatus.Failed), diagnostic);
        }
        // The series cover the whole bar array, so each bar's context gets its own view bound to that bar (never a shared mutable cursor:
        // a strategy that keeps an old context must not gain access to later bars). With nothing declared there is nothing to bound.
        IndicatorSeriesSet[]? barBoundedIndicators = indicatorSeries.IsEmpty ? null : indicatorSeries.CreateBarBoundedViews(n);

        var s = new RunState
        {
            Cash = configuration.InitialCapital,
            Orders = new List<BacktestOrder>(n),
            Fills = new List<BacktestFill>(n),
            Trades = new List<BacktestTrade>(Math.Max(1, n / 2)),
            EquityPoints = new List<EquityPoint>(n + 1),
            Signals = new List<BacktestSignal>(n),
        };

        RunStatus runStatus = RunStatus.Completed;
        // Optional capability (owner decision G4): resolved once; a strategy without it runs exactly as before.
        var environment = new BarEnvironment(input, configuration, strategy, strategy as IBacktestOrderCancellationStrategy, indicatorSeries, barBoundedIndicators);

        // T5 (owner decision G3): each bar works on a value snapshot so an engine arithmetic failure rolls back ONLY that uncommitted bar.
        BacktestRunDiagnostic? runDiagnostic = null;
        BarSnapshot barStart = default;
        int currentBar = -1;
        try
        {
            for (int i = 0; i < n; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentBar = i;
                barStart = BarSnapshot.Capture(s);

                if (ProcessBar(s, in environment, i) is { } stopStatus)
                {
                    runStatus = stopStatus;
                    break;
                }
            }
        }
        catch (OverflowException ex) when (captureArithmeticFailures && !s.InStrategyCall)
        {
            barStart.RestoreTo(s);
            runStatus = RunStatus.Failed;
            runDiagnostic = new BacktestRunDiagnostic(BacktestDiagnosticCode.ArithmeticOverflow, BacktestRunPhase.Bar, currentBar, s.Operation, ex.Message);
        }

        // Task #8 fix: when the loop runs through every bar without an early Insolvent/failure break, a
        // still-pending order that never filled must be expired with reason EndOfData - the open position
        // itself (if any) is left untouched, exactly mirroring Step 9's "residual orders expire, positions
        // stay marked at their last market value" rule for the Insolvency case. This was a gap in task #6:
        // ExpiredReason.EndOfData existed on the enum but nothing ever assigned it. Both dual-slot pending
        // orders (section 2.3) are handled independently here.
        if (runStatus == RunStatus.Completed)
        {
            EndPendingOrder(s, ref s.PendingEntryOrderIndex, OrderStatus.Expired, ExpiredReason.EndOfData);
            EndPendingOrder(s, ref s.PendingExitOrderIndex, OrderStatus.Expired, ExpiredReason.EndOfData);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new BacktestDiagnosedRun(BuildResult(input, configuration, strategy, s, runStatus), runDiagnostic);
    }

    /// <summary>
    /// Steps 1-9 of one bar (the body of the bar loop). Returns <see cref="RunStatus.Insolvent"/> when the run must stop after this bar (a forced liquidation or the
    /// end-of-bar check left the account with no equity), otherwise null. Any exception propagates to <see cref="RunCore"/>, which owns the rollback of the bar.
    /// </summary>
    private static RunStatus? ProcessBar(RunState s, in BarEnvironment env, int i)
    {
        BacktestInput input = env.Input;
        BacktestConfiguration configuration = env.Configuration;
        IBacktestStrategy strategy = env.Strategy;
        IBacktestOrderCancellationStrategy? cancellationStrategy = env.CancellationStrategy;
        IndicatorSeriesSet indicatorSeries = env.Indicators;
        IndicatorSeriesSet[]? barBoundedIndicators = env.BarBoundedIndicators;
        List<EquityPoint> equityPoints = s.EquityPoints;
        List<BacktestSignal> signals = s.Signals;

        CandleData bar = input.Bars[i];
        s.Operation = BacktestArithmeticOperation.Unknown;

        // Step 1: cancellations queued from the previous bar's Close are applied first (a cancellation before expiry wins), then the GTD expiry check ("i > ExpiryBar" is strictly greater - the expiry bar itself may still fill).
        // Dual pending-order slots (Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md
        // section 2.3): each slot expires independently, exactly mirroring the old single-slot check.
        ApplyQueuedCancellations(s);
        ExpireIfGtdElapsed(s, i, ref s.PendingEntryOrderIndex);
        ExpireIfGtdElapsed(s, i, ref s.PendingExitOrderIndex);

        // Step 2 (+ margin-call precedence, section 1.5.6): evaluate the pending order(s) and/or a
        // maintenance-margin breach on the same O->H->L->C path. Section 2.4's Exit-before-Entry rule
        // is enforced inside EvaluateStep2 itself: the pending Exit slot (if any) is always resolved
        // before the pending Entry slot is ever considered.
        bool insolventFromLiquidation = EvaluateStep2(s, i, bar, configuration);
        if (insolventFromLiquidation)
        {
            // Flat immediately after a forced liquidation: HeldMargin=0, UnrealizedPnL=0, so Equity == Cash.
            equityPoints.Add(new EquityPoint(i, bar.Timestamp, s.Cash, s.Cash, 0m, 0m));
            return RunStatus.Insolvent;
        }

        // Step 4: MarketOnClose evaluation at Close, unconditional, never competes with margin
        // liquidation on the O->H->L path (see MarginLiquidationCalculator remarks). Exit is still
        // evaluated before Entry (section 2.4) so a same-bar MOC reversal pair sees the freed Cash.
        if (s.PendingExitOrderIndex != -1 && s.Orders[s.PendingExitOrderIndex].Type == OrderType.MarketOnClose && s.Orders[s.PendingExitOrderIndex].EarliestFillBar <= i)
        {
            s.Operation = BacktestArithmeticOperation.OrderFillPricing;
            OrderFillOutcome mocExitOutcome = OrderFillEvaluator.EvaluateMarketOnClose(s.Orders[s.PendingExitOrderIndex], bar, configuration.SlippageRatio, configuration.CommissionFlat, configuration.CommissionPerUnit);
            CommitOrRejectFill(s, i, bar, mocExitOutcome, configuration, isEntry: false);
        }
        // An Entry (MOC included) is only ever admitted while Flat - the same rule EvaluateStep2 applies to every other Entry
        // type - so a MOC Entry waiting behind a still-open position must not overwrite it (it stays pending until Flat).
        if (s.Q == 0m && s.PendingEntryOrderIndex != -1 && s.Orders[s.PendingEntryOrderIndex].Type == OrderType.MarketOnClose && s.Orders[s.PendingEntryOrderIndex].EarliestFillBar <= i)
        {
            s.Operation = BacktestArithmeticOperation.OrderFillPricing;
            OrderFillOutcome mocEntryOutcome = OrderFillEvaluator.EvaluateMarketOnClose(s.Orders[s.PendingEntryOrderIndex], bar, configuration.SlippageRatio, configuration.CommissionFlat, configuration.CommissionPerUnit);
            CommitOrRejectFill(s, i, bar, mocEntryOutcome, configuration, isEntry: true);

            // A MOC Entry opens AT Close, so its only exposure is its Close state (G2-1): a breach there cannot be executed on this bar
            // (no executable price is observable after Close), so the position waits as LiquidationPending for the next bar's Open.
            if (s.Q != 0m && s.EntryBar == i)
            {
                s.LiquidationPending = TryFindNewPositionBreach(s, configuration, bar, FillPathScanner.ClosePoint(bar.Close), out _, out _);
            }
        }

        // Step 5: mark to market at Close (section 1.5.5).
        s.Operation = BacktestArithmeticOperation.MarkToMarket;
        decimal unrealizedPnL = s.Q * (bar.Close - s.EntryPrice);
        decimal equity = s.Cash + s.HeldMargin + unrealizedPnL;
        decimal marketValue = s.Q * bar.Close;

        // Step 6: indicators were already precomputed in full before the loop (Gate G2) - nothing to do per-bar; StrategyContext below exposes them via index.

        // Step 7: strategy evaluation - at most once per bar per method, only while trading is active
        // and solvent. Section 2.3's amended per-bar rule: Evaluate() may return any one signal (Entry
        // or Exit, exactly as before); EvaluateExit() (safe extension, default no-op) may independently
        // return a SECOND, Exit-type-only signal for the same bar - the accompanying close of a
        // same-bar reversal's opposite side. At most one Entry-type and independently at most one
        // Exit-type order is ever submitted this bar (never two of the same type).
        if (i >= input.TradingStartIndex && equity > 0m)
        {
            TradeSide? positionSide = s.Q > 0m ? TradeSide.Long : s.Q < 0m ? TradeSide.Short : null;
            var context = new StrategyContext
            {
                BarIndex = i,
                Bar = bar,
                PositionSide = positionSide,
                Snapshot = new EquityPoint(i, bar.Timestamp, equity, s.Cash, marketValue, s.HeldMargin),
                PendingOrderId = s.PendingExitOrderIndex != -1 ? s.Orders[s.PendingExitOrderIndex].OrderId
                    : s.PendingEntryOrderIndex != -1 ? s.Orders[s.PendingEntryOrderIndex].OrderId
                    : null,
                PendingEntryOrderId = s.PendingEntryOrderIndex != -1 ? s.Orders[s.PendingEntryOrderIndex].OrderId : null,
                PendingExitOrderId = s.PendingExitOrderIndex != -1 ? s.Orders[s.PendingExitOrderIndex].OrderId : null,
                Indicators = barBoundedIndicators is null ? indicatorSeries : barBoundedIndicators[i],
                EntryPrice = positionSide is null ? null : s.EntryPrice,
            };

            // G4: the intent is only QUEUED here (applied at Step 1 of the next bar), so the slot stays occupied for this Close's own submissions below.
            if (cancellationStrategy is not null)
            {
                s.InStrategyCall = true;
                OrderCancellationIntent cancellationIntent = cancellationStrategy.EvaluateCancellations(context);
                s.InStrategyCall = false;
                QueueCancellations(s, cancellationIntent);
            }

            s.InStrategyCall = true;
            StrategyOrderRequest? request = strategy.Evaluate(context);
            s.InStrategyCall = false;
            if (request is { } req)
            {
                signals.Add(new BacktestSignal(req.SignalType, i, bar.Timestamp, req.Reason ?? string.Empty));
                SubmitOrderFromSignal(s, i, bar, req, configuration, equity);
            }

            s.InStrategyCall = true;
            StrategyOrderRequest? accompanyingExit = strategy.EvaluateExit(context);
            s.InStrategyCall = false;
            if (accompanyingExit is { SignalType: SignalType.LongExit or SignalType.ShortExit } exitReq)
            {
                signals.Add(new BacktestSignal(exitReq.SignalType, i, bar.Timestamp, exitReq.Reason ?? string.Empty));
                SubmitOrderFromSignal(s, i, bar, exitReq, configuration, equity);
            }
            // A still-pending order of the SAME type is never overwritten (section 2.3's amended rule)
            // - SubmitOrderFromSignal itself no-ops when that signal's own slot is already occupied; the
            // signal is still logged above either way.
        }

        // Step 8: record the post-Step-5/6 Close snapshot.
        equityPoints.Add(new EquityPoint(i, bar.Timestamp, equity, s.Cash, marketValue, s.HeldMargin));

        // Step 9: end-of-bar insolvency check. Open positions stay marked at their last market value - they are not force-liquidated here (that is Step 2's job on a LATER bar).
        if (equity <= 0m)
        {
            EndPendingOrder(s, ref s.PendingEntryOrderIndex, OrderStatus.Expired, ExpiredReason.Insolvency);
            EndPendingOrder(s, ref s.PendingExitOrderIndex, OrderStatus.Expired, ExpiredReason.Insolvency);
            return RunStatus.Insolvent;
        }

        return null;
    }

    /// <summary>The per-run, read-only inputs of the bar steps (everything that is not the mutable <see cref="RunState"/>), passed by reference so a bar allocates nothing.</summary>
    private readonly struct BarEnvironment
    {
        public readonly BacktestInput Input;
        public readonly BacktestConfiguration Configuration;
        public readonly IBacktestStrategy Strategy;
        public readonly IBacktestOrderCancellationStrategy? CancellationStrategy;
        public readonly IndicatorSeriesSet Indicators;
        public readonly IndicatorSeriesSet[]? BarBoundedIndicators;

        public BarEnvironment(
            BacktestInput input,
            BacktestConfiguration configuration,
            IBacktestStrategy strategy,
            IBacktestOrderCancellationStrategy? cancellationStrategy,
            IndicatorSeriesSet indicators,
            IndicatorSeriesSet[]? barBoundedIndicators)
        {
            Input = input;
            Configuration = configuration;
            Strategy = strategy;
            CancellationStrategy = cancellationStrategy;
            Indicators = indicators;
            BarBoundedIndicators = barBoundedIndicators;
        }
    }

    private static BacktestResult BuildResult(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy,
        RunState s, RunStatus runStatus)
    {
        byte[] hash = ComputeReproducibilityHash(input, configuration, strategy.Name, s.Orders, s.Fills, s.Trades, s.EquityPoints, s.Signals);

        return new BacktestResult(
            s.Orders.ToImmutableArray(),
            s.Fills.ToImmutableArray(),
            s.Trades.ToImmutableArray(),
            s.EquityPoints.ToImmutableArray(),
            s.Signals.ToImmutableArray(),
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

        /// <summary>
        /// Dual pending-order slots (Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md
        /// section 2.3, amending the old single <c>PendingOrderIndex</c>/<c>PendingOrderIsEntry</c> pair):
        /// at most one Entry-type order and independently at most one Exit-type order may be pending at
        /// once (0-2 total, never two of the same type) - enabling a same-bar reversal pair while every
        /// pre-existing single-order scenario simply never populates the second slot, preserving identical
        /// behavior.
        /// </summary>
        public int PendingEntryOrderIndex = -1;
        public int PendingExitOrderIndex = -1;
        public TradeSide PendingEntrySide;

        /// <summary>
        /// A position opened by a MarketOnClose Entry that is already at/through its maintenance-margin threshold at that Close (owner decision G2-1).
        /// The daily-OHLC model observes no executable price after Close, so the forced liquidation is carried to the next bar's Open. Only ever true
        /// while a position is open; cleared by <see cref="ClosePosition"/>.
        /// </summary>
        public bool LiquidationPending;

        /// <summary>
        /// Cancellation queued at the previous Close for the order that was pending in that slot at that moment (owner decision G4); applied and
        /// cleared at Step 1. Resolved when queued, so an order submitted later at the same Close can never be matched by a guessed id.
        /// </summary>
        public bool QueuedEntryCancellation;
        public bool QueuedExitCancellation;

        /// <summary>True only while a strategy method is running: an OverflowException thrown there is the strategy's failure, never an engine arithmetic diagnostic (owner decision G3).</summary>
        public bool InStrategyCall;

        /// <summary>The engine step currently doing decimal arithmetic, reported by the diagnostic when an overflow occurs.</summary>
        public BacktestArithmeticOperation Operation;
        public long NextOrderId = 1;
        public long NextFillId = 1;
        public long NextTradeId = 1;
        public List<BacktestOrder> Orders = null!;
        public List<BacktestFill> Fills = null!;
        public List<BacktestTrade> Trades = null!;
        public List<EquityPoint> EquityPoints = null!;
        public List<BacktestSignal> Signals = null!;
    }

    /// <summary>
    /// Value snapshot, taken at the start of every bar, of everything a bar can change (owner decision G3): account scalars, ID counters, both pending slots
    /// (the only order records a bar ever rewrites in place), queued cancellations and the output lengths. <see cref="RestoreTo"/> discards exactly the
    /// uncommitted bar so the result exposes the fully committed prefix only. Strategy-internal state cannot be rolled back and is never re-evaluated.
    /// </summary>
    private readonly struct BarSnapshot
    {
        private readonly decimal _cash, _heldMargin, _q, _entryPrice, _entryFeeAccum;
        private readonly int _entryBar, _pendingEntryIndex, _pendingExitIndex, _orderCount, _fillCount, _tradeCount, _equityCount, _signalCount;
        private readonly DateTime _entryTime;
        private readonly TradeSide _pendingEntrySide;
        private readonly long _nextOrderId, _nextFillId, _nextTradeId;
        private readonly bool _liquidationPending, _queuedEntryCancellation, _queuedExitCancellation;
        private readonly BacktestOrder _pendingEntryOrder, _pendingExitOrder;

        private BarSnapshot(RunState s)
        {
            _cash = s.Cash;
            _heldMargin = s.HeldMargin;
            _q = s.Q;
            _entryPrice = s.EntryPrice;
            _entryFeeAccum = s.EntryFeeAccum;
            _entryBar = s.EntryBar;
            _entryTime = s.EntryTime;
            _pendingEntryIndex = s.PendingEntryOrderIndex;
            _pendingExitIndex = s.PendingExitOrderIndex;
            _pendingEntrySide = s.PendingEntrySide;
            _nextOrderId = s.NextOrderId;
            _nextFillId = s.NextFillId;
            _nextTradeId = s.NextTradeId;
            _liquidationPending = s.LiquidationPending;
            _queuedEntryCancellation = s.QueuedEntryCancellation;
            _queuedExitCancellation = s.QueuedExitCancellation;
            _orderCount = s.Orders.Count;
            _fillCount = s.Fills.Count;
            _tradeCount = s.Trades.Count;
            _equityCount = s.EquityPoints.Count;
            _signalCount = s.Signals.Count;
            _pendingEntryOrder = s.PendingEntryOrderIndex != -1 ? s.Orders[s.PendingEntryOrderIndex] : default;
            _pendingExitOrder = s.PendingExitOrderIndex != -1 ? s.Orders[s.PendingExitOrderIndex] : default;
        }

        public static BarSnapshot Capture(RunState s) => new(s);

        public void RestoreTo(RunState s)
        {
            s.Orders.RemoveRange(_orderCount, s.Orders.Count - _orderCount);
            s.Fills.RemoveRange(_fillCount, s.Fills.Count - _fillCount);
            s.Trades.RemoveRange(_tradeCount, s.Trades.Count - _tradeCount);
            s.EquityPoints.RemoveRange(_equityCount, s.EquityPoints.Count - _equityCount);
            s.Signals.RemoveRange(_signalCount, s.Signals.Count - _signalCount);
            if (_pendingEntryIndex != -1) s.Orders[_pendingEntryIndex] = _pendingEntryOrder;
            if (_pendingExitIndex != -1) s.Orders[_pendingExitIndex] = _pendingExitOrder;

            s.Cash = _cash;
            s.HeldMargin = _heldMargin;
            s.Q = _q;
            s.EntryPrice = _entryPrice;
            s.EntryFeeAccum = _entryFeeAccum;
            s.EntryBar = _entryBar;
            s.EntryTime = _entryTime;
            s.PendingEntryOrderIndex = _pendingEntryIndex;
            s.PendingExitOrderIndex = _pendingExitIndex;
            s.PendingEntrySide = _pendingEntrySide;
            s.NextOrderId = _nextOrderId;
            s.NextFillId = _nextFillId;
            s.NextTradeId = _nextTradeId;
            s.LiquidationPending = _liquidationPending;
            s.QueuedEntryCancellation = _queuedEntryCancellation;
            s.QueuedExitCancellation = _queuedExitCancellation;
        }
    }

    /// <summary>
    /// Queues (does not apply) a strategy's cancellation intent at Close (owner decision G4). Only the two pending slots are matched, never the order
    /// history, so an unknown, duplicate or already-terminal id is naturally a no-op. The slots stay occupied for this Close's own submissions.
    /// </summary>
    private static void QueueCancellations(RunState s, OrderCancellationIntent intent)
    {
        s.QueuedEntryCancellation = s.PendingEntryOrderIndex != -1 && intent.Names(s.Orders[s.PendingEntryOrderIndex].OrderId);
        s.QueuedExitCancellation = s.PendingExitOrderIndex != -1 && intent.Names(s.Orders[s.PendingExitOrderIndex].OrderId);
    }

    /// <summary>Step 1's application of the cancellations queued at the previous Close: the queued order moves to Cancelled before expiry and fills; the queue is always cleared.</summary>
    private static void ApplyQueuedCancellations(RunState s)
    {
        if (s.QueuedEntryCancellation)
        {
            s.QueuedEntryCancellation = false;
            EndPendingOrder(s, ref s.PendingEntryOrderIndex, OrderStatus.Cancelled);
        }
        if (s.QueuedExitCancellation)
        {
            s.QueuedExitCancellation = false;
            EndPendingOrder(s, ref s.PendingExitOrderIndex, OrderStatus.Cancelled);
        }
    }

    /// <summary>The single way a pending slot ends: the order takes its terminal status (and expiry reason, for Expired) and the slot is freed. A no-op for an empty slot.</summary>
    private static void EndPendingOrder(RunState s, ref int pendingIndex, OrderStatus status, ExpiredReason? expiredReason = null)
    {
        if (pendingIndex == -1) return;
        s.Orders[pendingIndex] = s.Orders[pendingIndex] with { Status = status, ExpiredReason = expiredReason };
        pendingIndex = -1;
    }

    /// <summary>Step 1's GTD expiry check (section 2.3), applied independently to one pending-order slot.</summary>
    private static void ExpireIfGtdElapsed(RunState s, int i, ref int pendingIndex)
    {
        if (pendingIndex == -1) return;
        TimeInForce timeInForce = s.Orders[pendingIndex].TimeInForce;
        if (!timeInForce.IsGoodTilCancelled && i > timeInForce.ExpiryBar)
        {
            EndPendingOrder(s, ref pendingIndex, OrderStatus.Expired, ExpiredReason.GTDExpired);
        }
    }

    /// <summary>
    /// Step 2 + section 1.5.6's margin-call precedence, amended per plan section 2.4 to sequence the
    /// pending EXIT slot strictly before the pending ENTRY slot: margin liquidation only ever competes
    /// with the pending Exit (a forced liquidation is itself a way of closing the currently-held
    /// position); the pending Entry slot is only ever considered once the account is Flat, whether it
    /// already was or just became so from this same call's Exit processing - guaranteeing Entry's funds
    /// check (<see cref="CommitEntryFill"/>) reads Cash AFTER any same-bar Exit released HeldMargin back
    /// into it. Time never reverses (A1 (lifetime and reversal chronology)): a reversal Entry behind a
    /// mid-bar Exit fills the same bar only as a marketable Limit at the Exit's reference price (see
    /// <see cref="EvaluatePendingEntryIfEligible"/>), a position that opens on this bar is exposed to margin liquidation from its
    /// own fill point onward, and a liquidation carried over from a MarketOnClose Entry executes first, at the Open. Returns true only when a forced liquidation this bar leaves
    /// the account insolvent (post-liquidation Equity &lt;= 0), signalling the caller to stop the run immediately per
    /// section 1.5.6's user-confirmed run-continuation rule.
    /// </summary>
    private static bool EvaluateStep2(RunState s, int i, CandleData bar, BacktestConfiguration config)
    {
        if (s.LiquidationPending)
        {
            // G2-1: the liquidation carried over from the previous Close executes first, at this bar's Open (RawFillPrice = Open, never the
            // threshold or any earlier price) with the usual forced-liquidation cost and commission; it supersedes both pending slots.
            TradeSide pendingSide = s.Q > 0m ? TradeSide.Long : TradeSide.Short;
            return ExecuteForcedLiquidation(s, i, bar, config, pendingSide, bar.Open);
        }

        PathPoint pathStart = FillPathScanner.OpenPoint(bar.Open);

        if (s.Q != 0m)
        {
            s.Operation = BacktestArithmeticOperation.LiquidationPricing;
            TradeSide openSide = s.Q > 0m ? TradeSide.Long : TradeSide.Short;
            decimal liquidationPrice = MarginLiquidationCalculator.ComputeLiquidationPrice(openSide, Math.Abs(s.Q), s.EntryPrice, s.Cash, s.HeldMargin, config.MaintenanceMarginRatio);
            decimal? marginPosition = MarginLiquidationCalculator.TryFindBreachPosition(openSide, liquidationPrice, bar);
            // Owner decision (gap rule): when the Open is already at/through the threshold the position trades at the Open, not at the threshold.
            decimal rawLiquidationPrice = marginPosition == FillPathScanner.OpenPoint(bar.Open).Position ? bar.Open : liquidationPrice;
            bool exitEligible = s.PendingExitOrderIndex != -1
                && s.Orders[s.PendingExitOrderIndex].Type != OrderType.MarketOnClose
                && s.Orders[s.PendingExitOrderIndex].EarliestFillBar <= i;
            // Rank the pending Exit by where it would actually FILL (a StopLimit's Stop trigger alone closes nothing), not where it activates.
            s.Operation = BacktestArithmeticOperation.OrderFillPricing;
            PathPoint? exitFillPoint = exitEligible
                ? OrderFillEvaluator.EvaluateFrom(s.Orders[s.PendingExitOrderIndex], bar, pathStart, 0m, 0m, 0m).FillPoint
                : null;

            ExitWinner winner = ExitPrecedenceResolver.Resolve(exitFillPoint?.Position, marginPosition);
            if (winner == ExitWinner.MarginLiquidation)
            {
                // A forced liquidation supersedes both slots: it closes the position outright, so any pending
                // Entry (only ever reachable here as the "open the opposite side" half of a reversal) can no
                // longer proceed against a position that no longer exists - see ExecuteForcedLiquidation.
                return ExecuteForcedLiquidation(s, i, bar, config, openSide, rawLiquidationPrice);
            }

            // winner is PendingOrder or None. If exitEligible, still evaluate/persist it (StopActivated must
            // be persisted even on a no-touch bar); if the "winning" order does not actually resolve into a
            // fill this bar (e.g. a StopLimit that only activates), a same-bar margin breach that exists
            // independently still applies as a fallback - see EvaluatePendingExitIfEligible's remarks.
            if (exitEligible && EvaluatePendingExitIfEligible(s, i, bar, config, marginPosition, openSide, rawLiquidationPrice))
            {
                return true;
            }

            // The Entry slot follows the Exit on the same path: it can only act at or after the point where the Exit actually filled.
            if (exitFillPoint is { } exitPoint) pathStart = exitPoint;
        }

        // Section 2.4: only after the Exit slot has been fully resolved against this bar's O->H->L->C path
        // do we consider the Entry slot - and only once the account is actually Flat (whether it already
        // was, or just became so above), so a same-bar reversal's Entry funds-check reads the freed Cash.
        return s.Q == 0m && EvaluatePendingEntryIfEligible(s, i, bar, config, pathStart);
    }

    /// <summary>
    /// Evaluates the pending EXIT slot against the bar (skipping it entirely if that slot is not
    /// eligible this bar), persists any StopActivated transition, and commits a fill if one
    /// occurred. USER-CONFIRMED (2026-09-16): if the pending order "won" precedence over the margin
    /// threshold but does not itself result in an actual fill this bar (e.g. a StopLimit that only
    /// activates, touching before the margin threshold but never reaching its Limit leg), and a margin
    /// breach independently exists later in the same bar, the margin liquidation still fires as a fallback
    /// - because an order that only activates without filling never actually closes the position, so
    /// nothing should shield the account from a real maintenance-margin breach.
    /// </summary>
    private static bool EvaluatePendingExitIfEligible(RunState s, int i, CandleData bar, BacktestConfiguration config, decimal? marginPosition, TradeSide openSide, decimal rawLiquidationPrice)
    {
        int idx = s.PendingExitOrderIndex;
        bool eligible = idx != -1 && s.Orders[idx].Type != OrderType.MarketOnClose && s.Orders[idx].EarliestFillBar <= i;
        if (!eligible) return false;

        s.Operation = BacktestArithmeticOperation.OrderFillPricing;
        OrderFillOutcome outcome = OrderFillEvaluator.Evaluate(s.Orders[idx], bar, config.SlippageRatio, config.CommissionFlat, config.CommissionPerUnit);
        if (outcome.StopActivated != s.Orders[idx].StopActivated)
        {
            s.Orders[idx] = s.Orders[idx] with { StopActivated = outcome.StopActivated };
        }

        if (outcome.Filled)
        {
            CommitExitFill(s, i, bar, outcome);
            return false;
        }

        if (marginPosition is not null)
        {
            return ExecuteForcedLiquidation(s, i, bar, config, openSide, rawLiquidationPrice);
        }

        return false;
    }

    /// <summary>
    /// Evaluates the pending non-MOC ENTRY slot, persists any StopActivated transition and commits the fill. When the account was Flat (or the Exit
    /// filled at Open) <paramref name="pathStart"/> is the bar's Open and the whole bar is scanned. When the Exit filled LATER in the bar
    /// (<paramref name="pathStart"/> after Open) the Entry is treated as a marketable-limit approximation (owner decision G2-2): only a Limit that is
    /// already marketable at the Exit's reference price (its raw fill price: Sell Limit &lt;= Ref, Buy Limit &gt;= Ref) fills the same bar, at that
    /// reference through the usual slippage/commission model and <see cref="FillPricing.ClampToLimit"/>; every other Entry - a Limit that is not
    /// marketable, Stop, StopLimit, Market - stays pending for a later bar and never reuses this bar's High/Low, which may precede the Exit.
    /// A position that opens is then assessed for margin liquidation from its own fill point onward. Returns true only when that
    /// liquidation leaves the account insolvent.
    /// </summary>
    private static bool EvaluatePendingEntryIfEligible(RunState s, int i, CandleData bar, BacktestConfiguration config, PathPoint pathStart)
    {
        int idx = s.PendingEntryOrderIndex;
        bool eligible = idx != -1 && s.Orders[idx].Type != OrderType.MarketOnClose && s.Orders[idx].EarliestFillBar <= i;
        if (!eligible) return false;

        bool afterIntrabarExit = pathStart.Position > FillPathScanner.OpenPoint(bar.Open).Position;
        if (afterIntrabarExit && s.Orders[idx].Type != OrderType.Limit) return false;

        s.Operation = BacktestArithmeticOperation.OrderFillPricing;
        ResidualFill residual = OrderFillEvaluator.EvaluateFrom(s.Orders[idx], bar, pathStart, config.SlippageRatio, config.CommissionFlat, config.CommissionPerUnit);
        if (afterIntrabarExit && residual.FillPoint != pathStart) return false; // not marketable at the Exit's reference price
        OrderFillOutcome outcome = residual.Outcome;
        if (outcome.StopActivated != s.Orders[idx].StopActivated)
        {
            s.Orders[idx] = s.Orders[idx] with { StopActivated = outcome.StopActivated };
        }

        if (!outcome.Filled || residual.FillPoint is not { } entryPoint) return false;

        CommitEntryFill(s, i, bar, outcome, config);
        return s.Q != 0m && AssessNewPositionLiquidation(s, i, bar, config, entryPoint);
    }

    /// <summary>
    /// A position that just opened at <paramref name="entryPoint"/> is live from that point through Close, so its own maintenance-margin
    /// threshold (post-entry Cash/HeldMargin/EntryPrice) is scanned on that residual path only - never on extrema that precede the fill.
    /// A breach force-closes it on the same bar (at the threshold, or at the market price of the entry point when the threshold already holds there). Returns true when the forced close leaves the account insolvent.
    /// A MarketOnClose Entry is not assessed here: its Close-state breach is deferred to the next Open (<see cref="RunState.LiquidationPending"/>).
    /// </summary>
    private static bool AssessNewPositionLiquidation(RunState s, int i, CandleData bar, BacktestConfiguration config, PathPoint entryPoint)
        => TryFindNewPositionBreach(s, config, bar, entryPoint, out TradeSide side, out decimal rawFillPrice)
            && ExecuteForcedLiquidation(s, i, bar, config, side, rawFillPrice);

    /// <summary>
    /// Whether the open position's own maintenance-margin threshold is at/through the price somewhere on the residual path from <paramref name="entryPoint"/>;
    /// <paramref name="rawFillPrice"/> is the price it trades at: the threshold, or the market price at <paramref name="entryPoint"/> when the threshold already holds there.
    /// </summary>
    private static bool TryFindNewPositionBreach(RunState s, BacktestConfiguration config, CandleData bar, PathPoint entryPoint, out TradeSide side, out decimal rawFillPrice)
    {
        s.Operation = BacktestArithmeticOperation.LiquidationPricing;
        side = s.Q > 0m ? TradeSide.Long : TradeSide.Short;
        decimal liquidationPrice = MarginLiquidationCalculator.ComputeLiquidationPrice(side, Math.Abs(s.Q), s.EntryPrice, s.Cash, s.HeldMargin, config.MaintenanceMarginRatio);
        TouchDirection breachDirection = side == TradeSide.Long ? TouchDirection.Downward : TouchDirection.Upward;
        PathPoint? breach = FillPathScanner.TryFindTouchFrom(bar.Open, bar.High, bar.Low, bar.Close, liquidationPrice, breachDirection, entryPoint);
        rawFillPrice = breach?.Price ?? liquidationPrice;
        return breach is not null;
    }

    /// <summary>
    /// Section 1.5.6's execution-on-breach + section 1.5.6's user-confirmed run-continuation rule: closes
    /// the position at the penalized forced-liquidation price, then reports whether the account is now
    /// insolvent (Equity &lt;= 0 while Flat, i.e. Cash &lt;= 0) so the caller stops the run, or whether it
    /// should continue as a partial loss-cut (Equity &gt; 0).
    /// </summary>
    private static bool ExecuteForcedLiquidation(RunState s, int i, CandleData bar, BacktestConfiguration config, TradeSide openSide, decimal rawFillPrice)
    {
        s.Operation = BacktestArithmeticOperation.LiquidationPricing;
        (decimal fillPrice, decimal commission) = MarginLiquidationCalculator.ComputeForcedLiquidationFill(
            openSide, rawFillPrice, Math.Abs(s.Q), config.LiquidationPenaltyRatio, config.CommissionFlat, config.CommissionPerUnit);
        OrderSide closingSide = openSide == TradeSide.Long ? OrderSide.Sell : OrderSide.Buy;
        decimal quantity = Math.Abs(s.Q);
        decimal slippageAmount = FillPricing.ComputeSlippageAmount(fillPrice, rawFillPrice, quantity);

        ClosePosition(s, i, bar, openSide, NoOriginatingOrderId, closingSide, fillPrice, quantity, commission, slippageAmount, isForcedLiquidation: true);

        // G6 (owner decision B1): the outcome decides how the superseded pending orders end - Expired(Insolvency) when the liquidation leaves
        // the account insolvent (the run stops), Cancelled when the account survives the loss-cut.
        bool insolvent = s.Cash <= 0m;
        (OrderStatus status, ExpiredReason? reason) = insolvent ? (OrderStatus.Expired, (ExpiredReason?)ExpiredReason.Insolvency) : (OrderStatus.Cancelled, null);
        EndPendingOrder(s, ref s.PendingExitOrderIndex, status, reason);
        EndPendingOrder(s, ref s.PendingEntryOrderIndex, status, reason);

        return insolvent;
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
        s.Operation = BacktestArithmeticOperation.ExitAccounting;
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
        s.LiquidationPending = false;
    }

    private static void CommitOrRejectFill(RunState s, int i, CandleData bar, OrderFillOutcome outcome, BacktestConfiguration config, bool isEntry)
    {
        if (isEntry)
        {
            CommitEntryFill(s, i, bar, outcome, config);
        }
        else
        {
            CommitExitFill(s, i, bar, outcome);
        }
    }

    /// <summary>Section 1.5.3 entry + funds check. Rejected orders leave account state untouched (Flat stays Flat) - including a same-bar reversal's just-freed Cash from CommitExitFill, since Step 2 always resolves the Exit slot first (section 2.4).</summary>
    private static void CommitEntryFill(RunState s, int i, CandleData bar, OrderFillOutcome outcome, BacktestConfiguration config)
    {
        s.Operation = BacktestArithmeticOperation.EntryAccounting;
        BacktestOrder order = s.Orders[s.PendingEntryOrderIndex];
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
            s.Orders[s.PendingEntryOrderIndex] = order with { Status = OrderStatus.Filled, StopActivated = outcome.StopActivated };
        }
        else
        {
            s.Orders[s.PendingEntryOrderIndex] = order with { Status = OrderStatus.Rejected, RejectedReason = RejectedReason.InsufficientFunds };
        }

        s.PendingEntryOrderIndex = -1;
    }

    /// <summary>Section 1.5.4 exit - never funds-rejected; Cash may legitimately go negative here (feeds Step 9).</summary>
    private static void CommitExitFill(RunState s, int i, CandleData bar, OrderFillOutcome outcome)
    {
        BacktestOrder order = s.Orders[s.PendingExitOrderIndex];
        TradeSide side = s.Q > 0m ? TradeSide.Long : TradeSide.Short;
        decimal quantity = Math.Abs(s.Q);

        ClosePosition(s, i, bar, side, order.OrderId, order.Side, outcome.FillPrice, quantity, outcome.Commission, outcome.SlippageAmount, isForcedLiquidation: false);

        s.Orders[s.PendingExitOrderIndex] = order with { Status = OrderStatus.Filled, StopActivated = outcome.StopActivated };
        s.PendingExitOrderIndex = -1;
    }

    /// <summary>
    /// Step 7's order submission: resolves a SignalType into a sized order. Defensive PositionConflict/
    /// InvalidQuantity rejections are recorded into Orders history (never silently dropped) even though
    /// the engine's own structural checks (Entry only allowed while flat or reversing into the opposite
    /// side, Exit only while holding the matching side) mean a conforming strategy should never trigger
    /// them. Section 2.3's amended per-bar rule: each SignalType targets its own independent pending slot
    /// (Entry-type vs Exit-type) - a slot already occupied silently no-ops the new signal (mirrors the old
    /// single-slot "still-pending order is never overwritten" rule, now enforced per type).
    /// </summary>
    private static void SubmitOrderFromSignal(RunState s, int i, CandleData bar, StrategyOrderRequest req, BacktestConfiguration config, decimal equity)
    {
        bool isEntry;
        OrderSide side;
        TradeSide entrySide = default;
        bool conflict;

        switch (req.SignalType)
        {
            // Entry conflict is relaxed from the old "Q != 0m" (Task 3 correction, plan section 2.4/3.2):
            // conflict now means "already holding THIS SAME direction" rather than "holding any position at
            // all" - every pre-existing caller only ever emits an Entry-type signal while Q==0m anyway (both
            // conditions agree there), so this is behaviorally identical for all of them; it additionally
            // allows the NEW same-bar reversal case (an Entry into the OPPOSITE of the currently-held side)
            // to be submitted at all, which the old "Q != 0m" rule would have always rejected.
            case SignalType.LongEntry:
                side = OrderSide.Buy; isEntry = true; entrySide = TradeSide.Long; conflict = s.Q > 0m; break;
            case SignalType.ShortEntry:
                side = OrderSide.Sell; isEntry = true; entrySide = TradeSide.Short; conflict = s.Q < 0m; break;
            case SignalType.LongExit:
                side = OrderSide.Sell; isEntry = false; conflict = s.Q <= 0m; break;
            case SignalType.ShortExit:
                side = OrderSide.Buy; isEntry = false; conflict = s.Q >= 0m; break;
            case SignalType.None:
                return; // No order to submit.
            default:
                throw new ArgumentOutOfRangeException(nameof(req), req.SignalType, "Unknown SignalType.");
        }

        ref int pendingIndex = ref (isEntry ? ref s.PendingEntryOrderIndex : ref s.PendingExitOrderIndex);
        if (pendingIndex != -1)
        {
            // This signal's own slot is already occupied - silently no-op (no Order row added), exactly
            // mirroring the old single-slot "a still-pending order is never overwritten" rule, now
            // enforced independently per type (section 2.3).
            return;
        }

        if (conflict)
        {
            s.Orders.Add(new BacktestOrder(s.NextOrderId++, side, req.OrderType, 0m, req.LimitPrice, req.StopPrice, OrderStatus.Rejected, req.TimeInForce, i, i + 1, false, null, RejectedReason.PositionConflict));
            return;
        }

        s.Operation = BacktestArithmeticOperation.PositionSizing;
        decimal quantity = isEntry ? ComputeEntryQuantity(config, equity, bar.Close) : Math.Abs(s.Q);
        if (isEntry && quantity < 1m)
        {
            s.Orders.Add(new BacktestOrder(s.NextOrderId++, side, req.OrderType, 0m, req.LimitPrice, req.StopPrice, OrderStatus.Rejected, req.TimeInForce, i, i + 1, false, null, RejectedReason.InvalidQuantity));
            return;
        }

        // Only a request about to become a pending order is validated: a discarded (slot occupied) or already-rejected
        // (PositionConflict/InvalidQuantity) signal keeps its existing outcome.
        BacktestOrderRequestValidator.Validate(req, i);

        var order = new BacktestOrder(s.NextOrderId++, side, req.OrderType, quantity, req.LimitPrice, req.StopPrice, OrderStatus.Submitted, req.TimeInForce, i, i + 1, false, null, null);
        s.Orders.Add(order);
        pendingIndex = s.Orders.Count - 1;
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

    /// <summary>Gate G2: create + calculate each requested indicator once over the full bar array before the loop starts.
    /// A request whose <see cref="StrategyIndicatorRequest.Frame"/> is null or equals <paramref name="input"/>'s own
    /// Frame is computed exactly as before (same-Frame path, unchanged). A request naming a different Frame is
    /// computed against that Frame's own bars (from <see cref="BacktestInput.AdditionalTimeframeBars"/>) and then
    /// causally aligned onto <paramref name="input"/>'s own bar indices via <see cref="TimeframeAlignment.Align"/> —
    /// see Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 3.2.4.</summary>
    private IndicatorSeriesSet PrepareIndicators(BacktestInput input, IReadOnlyList<StrategyIndicatorRequest> requests, CancellationToken cancellationToken)
    {
        var series = new Dictionary<string, ImmutableArray<decimal?>>(requests.Count);
        if (requests.Count == 0) return new IndicatorSeriesSet(series);

        var coreCandles = new CoreCandleData[input.Bars.Length];
        for (int i = 0; i < coreCandles.Length; i++)
        {
            CandleData bar = input.Bars[i];
            coreCandles[i] = new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        }

        var foreignCoreCandlesByFrame = new Dictionary<TimeFrame, CoreCandleData[]>();

        foreach (StrategyIndicatorRequest request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Causality guard (Y:\Temp\sa_implementation_plan_BacktestIndicatorCausalityGuard.md): refuse non-causal / synchronously
            // uncomputable outputs before anything is calculated - the whole-array precompute below would otherwise leak future bars.
            BacktestIndicatorViolationReason violation = BacktestIndicatorEligibility.Check(request.Type, request.OutputName);
            if (violation != BacktestIndicatorViolationReason.None)
            {
                throw new InvalidOperationException(
                    $"StrategyIndicatorRequest '{request.Key}': {BacktestIndicatorEligibility.Describe(request.Type, request.OutputName, violation)}");
            }

            ICoreIndicator indicator = _indicatorFactory.Create(request.Type, request.Parameters)
                ?? throw new InvalidOperationException($"Indicator type '{request.Type}' is not registered in IIndicatorFactory.");

            // SAで改善 (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): mirrors
            // AnalysisPipelineService.SyncPriceSource's existing mechanism for applying a non-Close price
            // source onto a created indicator instance. Without this, a "Price" catalog condition (one row
            // per PriceType - "Open"/"High"/"Low"/...) always evaluated against the instance's own default
            // (Close) regardless of which row was actually selected.
            if (request.PriceSource is { } priceSource && indicator is CoreIndicatorBase baseIndicator)
            {
                baseIndicator.PriceSource = priceSource;
            }

            if (request.Frame is null || request.Frame == input.Frame)
            {
                IIndicatorResult result = indicator.Calculate(coreCandles);
                EnsureCalculationSucceeded(request, result);
                series[request.Key] = ExtractRequestedSeries(result, request.OutputName, request.StrictOutputName).ToImmutableArray();
                continue;
            }

            TimeFrame foreignFrame = request.Frame.Value;
            if (input.AdditionalTimeframeBars is null ||
                !input.AdditionalTimeframeBars.TryGetValue(foreignFrame, out ImmutableArray<CandleData> foreignBars))
            {
                throw new InvalidOperationException(
                    $"StrategyIndicatorRequest '{request.Key}' requires Frame '{foreignFrame}', but " +
                    $"{nameof(BacktestInput.AdditionalTimeframeBars)} does not supply bars for it.");
            }

            if (!foreignCoreCandlesByFrame.TryGetValue(foreignFrame, out CoreCandleData[]? foreignCoreCandles))
            {
                foreignCoreCandles = new CoreCandleData[foreignBars.Length];
                for (int i = 0; i < foreignCoreCandles.Length; i++)
                {
                    CandleData bar = foreignBars[i];
                    foreignCoreCandles[i] = new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
                }
                foreignCoreCandlesByFrame[foreignFrame] = foreignCoreCandles;
            }

            IIndicatorResult foreignResult = indicator.Calculate(foreignCoreCandles);
            EnsureCalculationSucceeded(request, foreignResult);
            // A series may extend past the last candle (Ichimoku's Senkou spans hold count + Displacement values). That tail is a projection the
            // backtest cannot know, and Align needs exactly one value per foreign bar, so only the per-bar part is kept.
            ImmutableArray<decimal?> foreignValues = ExtractRequestedSeries(foreignResult, request.OutputName, request.StrictOutputName).Take(foreignBars.Length).ToImmutableArray();
            series[request.Key] = TimeframeAlignment.Align(input.Bars, foreignBars, foreignValues);
        }

        return new IndicatorSeriesSet(series);
    }

    /// <summary>An unsuccessful indicator result used to be consumed as an empty series, silently turning every condition on it into "false"; fail loudly instead.</summary>
    private static void EnsureCalculationSucceeded(StrategyIndicatorRequest request, IIndicatorResult result)
    {
        if (!result.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"StrategyIndicatorRequest '{request.Key}': indicator '{request.Type}' calculation failed: {result.ErrorMessage}");
        }
    }

    /// <summary>Task 6a safe-extension (Y:\Temp\sa_implementation_plan_BacktestOutputNameSupport.md):
    /// mirrors the exact HasSeries/GetSeries-guarded pattern <c>ScreenerValueExtractor</c> already uses,
    /// so a request naming a non-Main series of a multi-series indicator (e.g. MACD's "Signal") is
    /// honored, while a request whose OutputName is missing/not found or the default "Main" behaves
    /// byte-for-byte identically to before this field existed.</summary>
    private static IReadOnlyList<decimal?> ExtractRequestedSeries(IIndicatorResult result, string outputName, bool strictOutputName)
    {
        if (!strictOutputName)
        {
            return (!string.IsNullOrWhiteSpace(outputName) && result.HasSeries(outputName))
                ? result.GetSeries(outputName)
                : result.MainValues;
        }

        string normalized = string.IsNullOrWhiteSpace(outputName) ? IndicatorResult.MainSeriesName : outputName;
        if (string.Equals(normalized, IndicatorResult.MainSeriesName, StringComparison.Ordinal))
        {
            return result.MainValues;
        }

        string? exactName = result.SeriesNamesList.FirstOrDefault(name => string.Equals(name, normalized, StringComparison.Ordinal));
        if (exactName is null)
        {
            throw new InvalidOperationException($"Indicator result does not contain the requested series '{normalized}'.");
        }

        return result.GetSeries(exactName);
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
