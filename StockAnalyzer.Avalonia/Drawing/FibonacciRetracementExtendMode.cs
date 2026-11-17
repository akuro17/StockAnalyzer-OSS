namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Specifies the extension and vertical line display mode for Fibonacci Retracement lines.
/// Values are persisted by name or number: never reorder or renumber existing members.
/// </summary>
public enum FibonacciRetracementExtendMode
{
    /// <summary>
    /// Default display: horizontal lines span between the start and end points only.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Cyclic Lines mode: horizontal lines extend to the right, and vertical lines are displayed at intervals of the start-to-end period.
    /// </summary>
    CyclicLines = 1,

    /// <summary>
    /// Fibonacci Time Zone mode: horizontal lines extend to the right, and vertical lines are displayed at Fibonacci multiples (0, 1, 2, 3, 5, 8...).
    /// </summary>
    FibTimeZone = 2,

    /// <summary>
    /// Extend Right mode: horizontal lines extend from the left-side anchor to the right edge of the chart.
    /// </summary>
    ExtendRight = 3,

    /// <summary>
    /// Extend Both mode: horizontal lines extend across both edges of the chart.
    /// </summary>
    ExtendBoth = 4,

    /// <summary>
    /// Extend Left mode: horizontal lines extend from the left edge of the chart to the right-side anchor.
    /// </summary>
    ExtendLeft = 5
}
