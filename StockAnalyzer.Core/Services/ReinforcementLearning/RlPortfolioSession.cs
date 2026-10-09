using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Core-owned decimal accountant of one portfolio episode block (long-only, no leverage, no short, no margin). Python and the policy only
/// ever see snapshots. A decision at calendar index t is taken after the close of t; orders fill at the open of t+1 (slippage and commission
/// from <see cref="FillPricing"/>); the account is marked at the close of t+1. The episode ends after <see cref="RlContract.EpisodeHorizon"/>
/// decisions with the account marked, never liquidated. One gate serializes every call; a step is atomic (all-or-nothing) and cancellation is
/// honored only before it starts.
/// </summary>
public sealed class RlPortfolioSession : IDisposable
{
    private sealed record Account(decimal Cash, ImmutableArray<decimal> Quantities, decimal Peak, int Completed, decimal PreviousTurnover,
        int Orders, int RejectedBuys, decimal Commission, decimal Slippage);

    private readonly object _gate = new();
    private readonly IRlPanelBars _bars;
    private readonly RlPanelBlock _block;
    private readonly ImmutableArray<int> _tradable;
    private readonly BacktestConfiguration _costs;
    private readonly int _firstDecision;
    private RlPortfolioSessionState _state = RlPortfolioSessionState.Created;
    private bool _faulted;
    private Account? _account;

    /// <param name="tradable">Panel symbol indices the policy may hold in this block (ascending): a subset of the block's eligible symbols, for example the training or the holdout group.</param>
    public RlPortfolioSession(IRlPanelBars bars, RlPanelBlock block, ImmutableArray<int> tradable, BacktestConfiguration costs)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(costs);
        costs.Validate();
        if (block.EndExclusive - block.StartIndex != RlContract.SupportLength)
            throw new ArgumentException("The block must span exactly one episode support.", nameof(block));
        if (tradable.IsDefault || tradable.Length < RlPortfolioContract.MinimumBlockSymbols)
            throw new ArgumentException($"At least {RlPortfolioContract.MinimumBlockSymbols} tradable symbols are required.", nameof(tradable));
        for (int i = 0; i < tradable.Length; i++)
        {
            if (i > 0 && tradable[i] <= tradable[i - 1]) throw new ArgumentException("Tradable symbols must be strictly ascending.", nameof(tradable));
            if (tradable[i] < 0 || tradable[i] >= bars.SymbolCount || !block.EligibleSymbols.Contains(tradable[i]))
                throw new ArgumentException("Every tradable symbol must be a block-eligible panel symbol.", nameof(tradable));
            for (int t = block.StartIndex; t < block.EndExclusive; t++)
                if (!bars.TryGetBar(tradable[i], t, out _))
                    throw new ArgumentException("A tradable symbol must have a bar on every index of the block.", nameof(tradable));
        }
        _bars = bars; _block = block; _tradable = tradable; _costs = costs;
        _firstDecision = block.StartIndex + RlContract.WindowLength - 1;
    }

    public RlPortfolioSessionState State { get { lock (_gate) return _state; } }

    /// <summary>Returns to the first decision with the initial capital in cash. Valid in Created, Ready and Terminated; deterministic.</summary>
    public RlPortfolioSnapshot Reset(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RequireUsable();
            cancellationToken.ThrowIfCancellationRequested();
            _account = new Account(_costs.InitialCapital, Enumerable.Repeat(0m, _bars.SymbolCount).ToImmutableArray(), _costs.InitialCapital,
                0, 0m, 0, 0, 0m, 0m);
            _state = RlPortfolioSessionState.Ready;
            return Snapshot(_account, _firstDecision);
        }
    }

    /// <summary>
    /// One rebalance. <paramref name="assetWeights"/> has one non-negative weight per panel symbol, sums to at most one (the remainder is
    /// cash) and is zero for every symbol the session does not trade; a violation throws <see cref="ArgumentException"/> and changes nothing.
    /// </summary>
    public RlPortfolioStepResult Step(ImmutableArray<decimal> assetWeights, CancellationToken cancellationToken = default)
        => Advance(assetWeights, cancellationToken);

    /// <summary>
    /// One decision without any order: the target quantities are the held quantities, so nothing is bought, sold or charged; the account is
    /// marked, the reward and the terminal rule are those of <see cref="Step"/>. Used by the buy-and-hold baseline after its first decision.
    /// </summary>
    public RlPortfolioStepResult Hold(CancellationToken cancellationToken = default)
        => Advance(null, cancellationToken);

    /// <param name="assetWeights">Target weights of a rebalance, or null for a decision that keeps the held quantities.</param>
    private RlPortfolioStepResult Advance(ImmutableArray<decimal>? assetWeights, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RequireUsable();
            if (_state != RlPortfolioSessionState.Ready)
                throw new InvalidOperationException($"A step is valid only in the Ready state; the session is {_state}.");
            if (assetWeights is { } requested) ValidateWeights(requested);
            cancellationToken.ThrowIfCancellationRequested();
            var account = _account!;
            int t = _firstDecision + account.Completed;
            decimal[] close = new decimal[_bars.SymbolCount], open = new decimal[_bars.SymbolCount], next = new decimal[_bars.SymbolCount];
            foreach (int i in _tradable)
            {
                close[i] = Bar(i, t).Close; open[i] = Bar(i, t + 1).Open; next[i] = Bar(i, t + 1).Close;
            }
            decimal equity = account.Cash + _tradable.Sum(i => account.Quantities[i] * close[i]);
            var quantities = account.Quantities.ToBuilder();
            var targets = new decimal[_bars.SymbolCount];
            foreach (int i in _tradable)
                targets[i] = assetWeights is { } weights ? Math.Floor(weights[i] * equity / close[i]) : account.Quantities[i];

            decimal cash = account.Cash, commissionPaid = account.Commission, slippageCost = account.Slippage, traded = 0m;
            int orders = account.Orders, rejected = account.RejectedBuys;
            var trace = new RlPortfolioOrderTrace[_tradable.Length];
            for (int n = 0; n < _tradable.Length; n++)
                trace[n] = new RlPortfolioOrderTrace(_tradable[n], account.Quantities[_tradable[n]], targets[_tradable[n]], 0m, null, 0m, null);

            // Sells first (ascending symbol order), then buys (ascending), so sale proceeds can fund the buys of the same decision.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int n = 0; n < _tradable.Length; n++)
                {
                    int i = _tradable[n];
                    decimal delta = targets[i] - account.Quantities[i];
                    bool sell = pass == 0;
                    if (delta == 0m || (delta < 0m) != sell) continue;
                    decimal quantity = Math.Abs(delta);
                    var side = sell ? OrderSide.Sell : OrderSide.Buy;
                    decimal fill = FillPricing.ApplySlippage(open[i], side, _costs.SlippageRatio);
                    decimal commission = FillPricing.ComputeCommission(_costs.CommissionFlat, _costs.CommissionPerUnit, quantity);
                    orders++;
                    if (sell)
                    {
                        cash += quantity * fill - commission;
                    }
                    else
                    {
                        decimal cost = quantity * fill + commission;
                        if (cost > cash)
                        {
                            rejected++;
                            trace[n] = trace[n] with { Rejection = RlPortfolioRejection.InsufficientFunds };
                            continue;
                        }
                        cash -= cost;
                    }
                    quantities[i] = targets[i];
                    commissionPaid += commission;
                    slippageCost += FillPricing.ComputeSlippageAmount(fill, open[i], quantity);
                    traded += quantity * fill;
                    trace[n] = trace[n] with { ExecutedQuantity = delta, FillPrice = fill, Commission = commission };
                }
            }

            var held = quantities.ToImmutable();
            decimal marked = cash + _tradable.Sum(i => held[i] * next[i]);
            if (cash < 0m || marked <= 0m || held.Any(q => q < 0m || q != Math.Truncate(q)))
            {
                _faulted = true;
                throw new InvalidOperationException("Portfolio account invariant violated; the session is unusable.");
            }
            decimal reward = (marked - equity) / _costs.InitialCapital;
            if (!float.IsFinite((float)reward)) throw new OverflowException("Reward is not representable as finite float32.");
            var updated = new Account(cash, held, Math.Max(account.Peak, marked), account.Completed + 1, equity > 0m ? traded / equity : 0m,
                orders, rejected, commissionPaid, slippageCost);
            bool terminated = updated.Completed == RlContract.EpisodeHorizon;
            _account = updated;
            if (terminated) _state = RlPortfolioSessionState.Terminated;
            return new RlPortfolioStepResult(Snapshot(updated, t + 1), reward, terminated, trace.ToImmutableArray());
        }
    }

    public void Dispose()
    {
        lock (_gate) _state = RlPortfolioSessionState.Disposed;
    }

    private void RequireUsable()
    {
        if (_state == RlPortfolioSessionState.Disposed) throw new ObjectDisposedException(nameof(RlPortfolioSession));
        if (_faulted) throw new InvalidOperationException("The session is unusable after an account invariant violation.");
    }

    private void ValidateWeights(ImmutableArray<decimal> weights)
    {
        if (weights.IsDefault || weights.Length != _bars.SymbolCount)
            throw new ArgumentException("One weight per panel symbol is required.", nameof(weights));
        decimal sum = 0m;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] < 0m) throw new ArgumentException("Weights must not be negative (long-only).", nameof(weights));
            if (weights[i] != 0m && !_tradable.Contains(i)) throw new ArgumentException($"Symbol {i} is not tradable in this block and must have weight 0.", nameof(weights));
            sum += weights[i];
        }
        if (sum > 1m) throw new ArgumentException("Weights must sum to at most 1 (no leverage).", nameof(weights));
    }

    private CandleData Bar(int symbol, int index)
        => _bars.TryGetBar(symbol, index, out var bar) ? bar
            : throw new InvalidOperationException($"Block-eligible symbol {symbol} has no bar at calendar index {index}.");

    private RlPortfolioSnapshot Snapshot(Account account, int anchor)
    {
        decimal[] close = new decimal[_bars.SymbolCount];
        foreach (int i in _tradable) close[i] = Bar(i, anchor).Close;
        decimal equity = account.Cash + _tradable.Sum(i => account.Quantities[i] * close[i]);
        var weights = Enumerable.Range(0, _bars.SymbolCount).Select(i => account.Quantities[i] * close[i] / equity).ToImmutableArray();
        decimal peak = Math.Max(account.Peak, equity);
        return new RlPortfolioSnapshot(_block.Id, account.Completed, RlContract.EpisodeHorizon - account.Completed, anchor,
            Bar(_tradable[0], anchor).Timestamp, account.Cash, account.Quantities, weights, account.Cash / equity, equity, peak,
            (peak - equity) / peak, account.PreviousTurnover, account.Completed == RlContract.EpisodeHorizon, account.Orders,
            account.RejectedBuys, account.Commission, account.Slippage);
    }
}
