using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

public sealed partial class BacktestEngine
{
    /// <summary>Create a T15 incremental session over exactly one immutable 192-row D1 support block.</summary>
    public IRlTradingSession CreateRlSession(BacktestInput input, BacktestConfiguration configuration)
        => CreateRlSessionCore(input, configuration, continuous: false);

    public IRlContinuousTradingSession CreateRlContinuousSession(BacktestInput input, BacktestConfiguration configuration)
        => CreateRlSessionCore(input, configuration, continuous: true);

    private RlTradingSession CreateRlSessionCore(BacktestInput input, BacktestConfiguration configuration, bool continuous)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (input.Frame != TimeFrame.D1 || input.AdditionalTimeframeBars is { Count: > 0 } ||
            input.Bars.Length != RlContract.SupportLength || input.HistoryStartIndex != 0 ||
            input.TradingStartIndex != RlContract.WindowLength - 1)
            throw new ArgumentException("RL requires exactly 192 D1 bars, history index 0 and completed decision anchor 63, without foreign timeframes.", nameof(input));
        if (configuration.ExecutionModel != ExecutionModel.YFinanceApproximate ||
            configuration.SizingModel != PositionSizingModel.FixedQuantity || configuration.SizingParameter != RlContract.FixedQuantity ||
            configuration.InitialMarginRatio != RlContract.InitialMarginRatio || configuration.MaintenanceMarginRatio != BacktestDefaults.MaintenanceMarginRatio)
            throw new ArgumentException("T15 requires explicit YFinanceApproximate, fixed one-unit sizing and adopted long-only margin ratios.", nameof(configuration));
        // The input owns immutable bars; the configuration consists entirely of init-only value fields.
        return new RlTradingSession(input, RlContract.CreateConfiguration(configuration), _logger, continuous);
    }

    private sealed class RlTradingSession : IRlContinuousTradingSession
    {
        private readonly object _gate = new();
        private readonly BacktestInput _input;
        private readonly BacktestConfiguration _configuration;
        private readonly ILogger<BacktestEngine> _logger;
        private readonly IBacktestStrategy _strategy = new RlIdleStrategy();
        private readonly BarEnvironment _environment;
        private RunState? _state;
        private RlSessionSnapshot? _snapshot;
        private bool _disposed;
        private bool _failed;
        private RunStatus _status;
        private readonly bool _continuous;
        private decimal _retainedStopDistance;
        private decimal? _retainedEntrySizeRatio;
        private bool _deferredExit;

        public RlTradingSession(BacktestInput input, BacktestConfiguration configuration, ILogger<BacktestEngine> logger, bool continuous)
        {
            _continuous = continuous;
            _input = input;
            _configuration = configuration;
            _logger = logger;
            _environment = new BarEnvironment(input, configuration, _strategy, null,
                new IndicatorSeriesSet(new Dictionary<string, ImmutableArray<decimal?>>()), null, stopOnForcedLiquidation: true);
        }

        public RlSessionSnapshot Reset(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                var state = new RunState
                {
                    Cash = _configuration.InitialCapital,
                    Orders = new List<BacktestOrder>(RlContract.EpisodeHorizon),
                    Fills = new List<BacktestFill>(RlContract.EpisodeHorizon),
                    Trades = new List<BacktestTrade>(RlContract.EpisodeHorizon),
                    EquityPoints = new List<EquityPoint>(RlContract.EpisodeHorizon + 1),
                    Signals = new List<BacktestSignal>(RlContract.EpisodeHorizon),
                };
                int anchor = _input.TradingStartIndex;
                // Reset is a newly initialized flat account at this completed Close, not a replay of warmup bars.
                state.EquityPoints.Add(new EquityPoint(anchor, _input.Bars[anchor].Timestamp,
                    _configuration.InitialCapital, _configuration.InitialCapital, 0m, 0m));
                _retainedStopDistance = 0m;
                _retainedEntrySizeRatio = null;
                _deferredExit = false;
                RlSessionSnapshot snapshot = CreateSnapshot(state, anchor, null, 0, _configuration.InitialCapital, 0m,
                    RlTerminalReason.None, RunStatus.Completed);
                cancellationToken.ThrowIfCancellationRequested();
                _state = state;
                _snapshot = snapshot;
                _failed = false;
                _status = RunStatus.Completed;
                return snapshot;
            }
        }

        public RlStepResult Step(RlAction action, CancellationToken cancellationToken = default)
        {
            if (_continuous) throw new InvalidOperationException("A continuous session requires explicit actor proposals.");
            return StepCore(action, null, cancellationToken);
        }

        public RlStepResult Step(RlAction direction, float rawSize, float rawStop, CancellationToken cancellationToken = default)
        {
            if (!_continuous) throw new InvalidOperationException("Actor proposals require a T16 session.");
            return StepCore(direction, RlContinuousContract.Map(rawSize, rawStop), cancellationToken);
        }

        public RlStepResult StepFixedControl(RlAction direction, CancellationToken cancellationToken = default)
        {
            if (!_continuous) throw new InvalidOperationException("Fixed continuous control requires a T16 session.");
            var control = RlContinuousContract.Map(RlContinuousContract.BaselineSizeAction, RlContinuousContract.BaselineStopAction)
                with { SizeRatio = RlContinuousContract.MaximumSizeRatio, StopDistance = RlContinuousContract.BaselineStopDistance };
            return StepCore(direction, control, cancellationToken);
        }

        private RlStepResult StepCore(RlAction action, RlContinuousAction? actor, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown RL action.");
                if (_state is null || _snapshot is null) throw new InvalidOperationException("Reset is required before Step.");
                if (_failed || _snapshot.Terminated) throw new InvalidOperationException("The session ended; Reset is required before Step.");
                // R03 continuous attempts fail closed until Reset; legacy T15 pre-cancel validation is unchanged.
                if (!_continuous) cancellationToken.ThrowIfCancellationRequested();
                RunState state = _state;
                RlSessionSnapshot previous = _snapshot;
                BarSnapshot before = BarSnapshot.Capture(state);
                decimal previousDistance = _retainedStopDistance;
                decimal? previousRatio = _retainedEntrySizeRatio;
                bool previousDeferred = _deferredExit;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int orderCount = state.Orders.Count;
                    int fillCount = state.Fills.Count;
                    bool eligibleBuy = _continuous && action == RlAction.Buy && state.Q == 0m
                        && state.PendingEntryOrderIndex == -1 && !_deferredExit;
                    RlActionOutcome outcome = SubmitAction(state, previous, action, actor);
                    int nextBar = previous.MarketAnchorBarIndex + 1;
                    int tradeCount = state.Trades.Count;
                    // This is the same primitive and mutable accountant used by Run; exactly one next bar is processed.
                    RunStatus? stop = ProcessBar(state, in _environment, nextBar);
                    // RL-only completed-Close hook: shared Run callbacks are unchanged. No same-fill-bar stop execution.
                    if (_continuous) CompleteContinuousClose(state, nextBar, stop is null);
                    if (_continuous)
                        _retainedEntrySizeRatio = state.Q == 0m ? null
                            : state.EntryBar == nextBar && eligibleBuy ? actor!.SizeRatio : _retainedEntrySizeRatio;
                    int completed = previous.CompletedDecisions + 1;
                    bool intrabar = stop == RunStatus.Insolvent && state.Trades.Count > tradeCount && state.Trades[^1].IsForcedLiquidation;
                    RlTerminalReason reason = stop == RunStatus.Insolvent
                        ? intrabar ? RlTerminalReason.ForcedLiquidation : RlTerminalReason.EquityNonPositive
                        : completed == RlContract.EpisodeHorizon ? RlTerminalReason.Horizon : RlTerminalReason.None;
                    if (reason == RlTerminalReason.Horizon)
                    {
                        // Mark-only final Close: pending orders expire, while the residual position and account stay intact.
                        EndPendingOrder(state, ref state.PendingEntryOrderIndex, OrderStatus.Expired, ExpiredReason.EndOfData);
                        EndPendingOrder(state, ref state.PendingExitOrderIndex, OrderStatus.Expired, ExpiredReason.EndOfData);
                        _deferredExit = false;
                    }
                    RunStatus status = stop ?? RunStatus.Completed;
                    int anchor = intrabar ? previous.MarketAnchorBarIndex : nextBar;
                    RlSessionSnapshot snapshot = CreateSnapshot(state, anchor, reason == RlTerminalReason.None ? null : nextBar,
                        completed, previous.PeakEquity, previous.MaximumDrawdown, reason, status);
                    decimal reward = (snapshot.EquityPoint.Equity - previous.EquityPoint.Equity) / _configuration.InitialCapital;
                    // The decimal result is finalized first. A future ML transport must narrow only at its explicit float32 boundary.
                    if (!float.IsFinite((float)reward)) throw new OverflowException("Reward is not representable as finite float32.");
                    RejectedReason? rejection = null;
                    if (state.Orders.Count > orderCount && state.Orders[orderCount].Status == OrderStatus.Rejected)
                    {
                        outcome = RlActionOutcome.Rejected;
                        rejection = state.Orders[orderCount].RejectedReason;
                    }
                    RlEntryExecution? entry = null;
                    if (eligibleBuy)
                    {
                        BacktestOrder? order = state.Orders.Count > orderCount ? state.Orders[orderCount] : null;
                        BacktestFill? fill = null;
                        for (int i = fillCount; i < state.Fills.Count; i++)
                            if (state.Fills[i].Side == OrderSide.Buy) fill = state.Fills[i];
                        decimal filled = fill?.Quantity ?? 0m;
                        entry = new(previous.CompletedDecisions, previous.MarketAnchor, _input.Bars[previous.MarketAnchorBarIndex].Close,
                            previous.EquityPoint.Equity, actor!.SizeRatio, actor.StopDistance, previous.EquityPoint.Equity * actor.SizeRatio,
                            previous.EquityPoint.Cash, order?.Quantity ?? 0m,
                            fill.HasValue ? filled : outcome is RlActionOutcome.NoEntry or RlActionOutcome.Rejected ? 0m : null,
                            filled, fill?.Price, filled * (fill?.Price ?? 0m), filled * (fill?.Price ?? 0m) * _configuration.InitialMarginRatio,
                            fill?.Commission ?? 0m, order?.OrderId, outcome, rejection);
                    }
                    var result = new RlStepResult(snapshot, reward, snapshot.Terminated, false, action, outcome, rejection)
                        { ActorAction = actor, EntryExecution = entry };
                    cancellationToken.ThrowIfCancellationRequested();
                    _snapshot = snapshot;
                    _status = status;
                    return result;
                }
                catch (Exception ex)
                {
                    before.RestoreTo(state);
                    _retainedStopDistance = previousDistance;
                    _retainedEntrySizeRatio = previousRatio;
                    _deferredExit = previousDeferred;
                    _failed = true;
                    _status = ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed;
                    _logger.LogWarning(ex, "RL session transition failed at completed market anchor {Anchor}; no transition was published", previous.MarketAnchorBarIndex);
                    throw;
                }
            }
        }

        private RlActionOutcome SubmitAction(RunState state, RlSessionSnapshot snapshot, RlAction action, RlContinuousAction? actor)
        {
            if (_deferredExit) return RlActionOutcome.DeferredExit;
            if (action == RlAction.Hold) return RlActionOutcome.Hold;
            bool buy = action == RlAction.Buy;
            if (buy && state.PendingEntryOrderIndex != -1) return RlActionOutcome.PendingEntry;
            if (!buy && state.PendingExitOrderIndex != -1)
            {
                var pending = state.Orders[state.PendingExitOrderIndex];
                if (_continuous && state.Q > 0m && pending.Type == OrderType.Stop)
                {
                    QueueCancellations(state, new OrderCancellationIntent(pending.OrderId, null));
                    _deferredExit = true;
                    return RlActionOutcome.StopCancellationQueued;
                }
                return RlActionOutcome.PendingExit;
            }
            if (buy && state.Q > 0m) return RlActionOutcome.AlreadyLong;
            if (!buy && state.Q == 0m) return RlActionOutcome.AlreadyFlat;
            if (state.Q < 0m) throw new InvalidOperationException("Short positions are outside the RL contract.");
            int anchor = snapshot.MarketAnchorBarIndex;
            if (buy && actor is not null && ComputeEntryQuantity(_configuration, snapshot.EquityPoint.Equity, _input.Bars[anchor].Close, actor.SizeRatio) < 1m)
                return RlActionOutcome.NoEntry;
            var request = new StrategyOrderRequest(buy ? SignalType.LongEntry : SignalType.LongExit, OrderType.Market,
                null, null, new TimeInForce(true, -1), action.ToString()) { EntrySizeRatio = buy ? actor?.SizeRatio : null };
            state.Signals.Add(new BacktestSignal(request.SignalType, anchor, _input.Bars[anchor].Timestamp, request.Reason));
            SubmitOrderFromSignal(state, anchor, _input.Bars[anchor], request, _configuration, snapshot.EquityPoint.Equity);
            if (buy && actor is not null && state.PendingEntryOrderIndex != -1) _retainedStopDistance = actor.StopDistance;
            return RlActionOutcome.Submitted;
        }

        private void CompleteContinuousClose(RunState state, int barIndex, bool active)
        {
            if (state.Q == 0m || !active)
            {
                if (state.PendingEntryOrderIndex == -1 || !active) _retainedStopDistance = 0m;
                _deferredExit = false;
                return;
            }
            if (state.PendingExitOrderIndex != -1) return;
            if (_deferredExit)
            {
                SubmitOrderFromSignal(state, barIndex, _input.Bars[barIndex],
                    new StrategyOrderRequest(SignalType.LongExit, OrderType.Market, null, null, new TimeInForce(true, -1), "rl.deferred_full_exit"),
                    _configuration, state.EquityPoints[^1].Equity);
                if (state.PendingExitOrderIndex != -1) _deferredExit = false;
                return;
            }
            if (_retainedStopDistance > 0m)
                SubmitOrderFromSignal(state, barIndex, _input.Bars[barIndex],
                    new StrategyOrderRequest(SignalType.LongExit, OrderType.Stop, null, state.EntryPrice * (1m - _retainedStopDistance),
                        new TimeInForce(true, -1), "rl.fixed_protective_stop"), _configuration, state.EquityPoints[^1].Equity);
        }

        private RlSessionSnapshot CreateSnapshot(RunState state, int anchor, int? terminalBar, int completed,
            decimal previousPeak, decimal previousMaximumDrawdown, RlTerminalReason reason, RunStatus status)
        {
            EquityPoint equity = state.EquityPoints[^1];
            decimal peak = Math.Max(previousPeak, equity.Equity);
            decimal drawdown = 1m - equity.Equity / peak;
            decimal maximumDrawdown = Math.Max(previousMaximumDrawdown, drawdown);
            bool ended = reason != RlTerminalReason.None;
            int remaining = ended ? 0 : RlContract.EpisodeHorizon - completed;
            decimal unrealized = equity.Equity - equity.Cash - equity.HeldMargin;
            int heldBars = state.Q == 0m ? 0 : equity.BarIndex - state.EntryBar + 1;
            bool pendingEntry = state.PendingEntryOrderIndex != -1;
            bool pendingExit = state.PendingExitOrderIndex != -1;
            decimal? stopPrice = pendingExit && state.Orders[state.PendingExitOrderIndex].Type == OrderType.Stop
                ? state.Orders[state.PendingExitOrderIndex].StopPrice : null;
            ImmutableArray<decimal> account = RlAccountObservation.Build(state.Q, state.EntryPrice, _input.Bars[anchor].Close,
                equity, unrealized, _configuration.InitialCapital, drawdown, heldBars, remaining, pendingEntry, pendingExit,
                stopPrice, _retainedStopDistance, _deferredExit);
            ImmutableArray<CandleData> market = _input.Bars.Slice(anchor - RlContract.WindowLength + 1, RlContract.WindowLength);
            return new RlSessionSnapshot(anchor, _input.Bars[anchor].Timestamp, terminalBar, completed, remaining, equity,
                state.Q, state.Q == 0m ? 0m : state.EntryPrice, unrealized, peak, drawdown, maximumDrawdown, heldBars,
                pendingEntry, pendingExit, ended, reason, status, account, market)
                { ActiveStopPrice = stopPrice, RetainedStopDistance = _retainedStopDistance, DeferredMarketExit = _deferredExit,
                    RetainedEntrySizeRatio = _retainedEntrySizeRatio };
        }

        public BacktestResult GetResult()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_state is null) throw new InvalidOperationException("Reset is required before GetResult.");
                return BuildResult(_input, _configuration, _strategy, _state, _status);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _state = null;
                _snapshot = null;
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class RlIdleStrategy : IBacktestStrategy
    {
        public string Name => "rl.direction.fixed1.v1";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();
        public StrategyOrderRequest? Evaluate(StrategyContext context) => null;
    }
}
