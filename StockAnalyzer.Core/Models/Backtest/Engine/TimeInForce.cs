namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// GTC: new TimeInForce(true, -1) — ExpiryBar unused/ignored.
/// GTD: new TimeInForce(false, expiryBar) — expiryBar must be &gt;= the order's EarliestFillBar.
/// </summary>
public readonly record struct TimeInForce(bool IsGoodTilCancelled, int ExpiryBar);
