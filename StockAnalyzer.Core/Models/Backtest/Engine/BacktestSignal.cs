using System;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

public readonly record struct BacktestSignal(
    SignalType Type, int BarIndex, DateTime SignalTime, string Reason);
