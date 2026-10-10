namespace StockAnalyzer.Core.Models.Backtest.Configuration;

/// <summary>End date resolution strategy for backtest defaults.</summary>
public enum BacktestEndDateMode
{
    /// <summary>Dynamically resolves to the current UTC date at execution time.</summary>
    Today = 0,
    /// <summary>Uses a user-specified fixed UTC calendar date.</summary>
    FixedDate = 1,
}
