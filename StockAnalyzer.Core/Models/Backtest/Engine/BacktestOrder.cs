namespace StockAnalyzer.Core.Models.Backtest.Engine;

public readonly record struct BacktestOrder(
    long OrderId, OrderSide Side, OrderType Type,
    decimal Quantity, decimal? LimitPrice, decimal? StopPrice,
    OrderStatus Status, TimeInForce TimeInForce,
    int SubmittedBar, int EarliestFillBar,
    bool StopActivated,
    ExpiredReason? ExpiredReason, RejectedReason? RejectedReason);
