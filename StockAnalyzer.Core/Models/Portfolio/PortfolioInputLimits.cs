namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// The lower bounds of the numeric inputs of a portfolio transaction (the transaction dialog). They are input contracts of the system, not user settings,
/// so no configuration can change them. The dialog binds its numeric boxes to these values instead of keeping its own literals.
/// </summary>
public static class PortfolioInputLimits
{
    /// <summary>Smallest quantity of a trade (fractional shares are accepted down to this value).</summary>
    public const decimal MinTradeQuantity = 0.000001m;

    /// <summary>Smallest amount of a deposit or a withdrawal.</summary>
    public const decimal MinCashAmount = 0.01m;

    /// <summary>Smallest exchange rate (currency to USD) that can be entered.</summary>
    public const decimal MinExchangeRate = 0.000001m;

    /// <summary>Lower bound of every amount that may be zero but never negative: price per unit, fee, target price, stop loss.</summary>
    public const decimal MinNonNegativeAmount = 0m;
}
