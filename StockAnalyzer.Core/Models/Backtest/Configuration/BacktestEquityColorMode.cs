namespace StockAnalyzer.Core.Models.Backtest.Configuration;

/// <summary>
/// What switches the color of the Backtest Results equity curve. The numeric values are persisted
/// (<c>user_chart_settings.json</c> stores enums as numbers): never renumber or reorder them.
/// </summary>
public enum BacktestEquityColorMode
{
    /// <summary>The whole line uses the single line color.</summary>
    Single = 0,

    /// <summary>A segment is "up" when its end equity is greater than or equal to its start equity, otherwise "down".</summary>
    PreviousBar = 1,

    /// <summary>A segment is "up" when its end point sets or ties the running high, "down" when it ends below the high (drawdown).</summary>
    Drawdown = 2,
}
