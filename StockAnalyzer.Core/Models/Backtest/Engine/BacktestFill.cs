using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

public readonly record struct BacktestFill(
    long FillId, long OrderId, int BarIndex, DateTime FillTime,
    OrderSide Side, decimal Price,
    decimal Quantity, decimal Commission,
    decimal SlippageAmount);
