namespace StockAnalyzer.Core.Models.Backtest.Configuration;

/// <summary>Start date resolution strategy for backtest defaults.</summary>
public enum BacktestStartDateMode
{
    /// <summary>Dynamically resolves to N years before the resolved End Date.</summary>
    RelativeYears = 0,
    /// <summary>Uses a user-specified fixed UTC calendar date.</summary>
    FixedDate = 1,
}
