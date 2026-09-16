namespace StockAnalyzer.Core.Models.Backtest.Engine;

public enum OrderSide { Buy, Sell }

public enum OrderType { Market, MarketOnClose, Limit, Stop, StopLimit }

public enum OrderStatus { Submitted, Filled, Cancelled, Expired, Rejected }

/// <summary>
/// Long and Short are both executable in the margin-account model (StockAnalyzer.Core/Models/Backtest/Engine).
/// See Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5 for the accounting model.
/// </summary>
public enum TradeSide { Long, Short }

public enum SignalType { None, LongEntry, LongExit, ShortEntry, ShortExit }

public enum PositionSizingModel { FixedQuantity, PercentOfEquity }

/// <summary>
/// Insolvent covers both the original end-of-bar Equity&lt;=0 condition and a maintenance-margin
/// forced liquidation (see Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6) — both stop the run.
/// </summary>
public enum RunStatus { Completed, Cancelled, Failed, Insolvent }

public enum ExpiredReason { EndOfData, Insolvency, GTDExpired }

public enum RejectedReason { InsufficientFunds, PositionConflict, InvalidQuantity }
