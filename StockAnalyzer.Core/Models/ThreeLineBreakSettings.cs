namespace StockAnalyzer.Core.Models;

/// <summary>
/// Settings for Three Line Break chart generation.
/// </summary>
public class ThreeLineBreakSettings
{
    /// <summary>
    /// Number of lines to look back for reversal detection (default: 3).
    /// </summary>
    public int LineCount { get; }

    /// <summary>
    /// Tolerance for price comparison to filter noise.
    /// </summary>
    public decimal Tolerance { get; }

    public ThreeLineBreakSettings(int lineCount = ChartConstants.DefaultThreeLineBreakLineCount, decimal tolerance = 0.0001m)
    {
        LineCount = lineCount > 0 ? lineCount : ChartConstants.DefaultThreeLineBreakLineCount;
        Tolerance = tolerance >= 0 ? tolerance : 0.0001m;
    }
}
