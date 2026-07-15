namespace StockAnalyzer.Core.Models;

/// <summary>
/// Specifies the display mode for relative performance comparison charts.
/// </summary>
public enum ComparisonMode
{
    /// <summary>
    /// Percentage change from the first visible candle (Baseline Alignment).
    /// </summary>
    Performance,

    /// <summary>
    /// Ratio of the comparison symbol's close to the primary symbol's close (A/B Ratio).
    /// </summary>
    Ratio,

    /// <summary>
    /// Standardized deviation from a moving average (Z-Score).
    /// </summary>
    ZScore,

    /// <summary>
    /// Signed price difference: Comparison.Close − Primary.Close (Spread).
    /// </summary>
    Spread
}
