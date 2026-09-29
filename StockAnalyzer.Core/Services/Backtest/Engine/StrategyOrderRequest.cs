using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// A strategy's requested action for the current bar (section 5.5 Step 7's "OrderRequest"). The engine
/// resolves <see cref="SignalType"/> into an <see cref="OrderSide"/> and a sized <see cref="BacktestOrder"/>
/// (entries are sized per section 1.5.7 using the current Equity snapshot; exits always close the full
/// open quantity per the existing no-partial-exit rule) — the strategy itself never computes Quantity or
/// touches account state directly, matching section 9's "do not leak broader engine internals" constraint.
/// </summary>
public readonly record struct StrategyOrderRequest(
    SignalType SignalType,
    OrderType OrderType,
    decimal? LimitPrice,
    decimal? StopPrice,
    TimeInForce TimeInForce,
    string Reason);
