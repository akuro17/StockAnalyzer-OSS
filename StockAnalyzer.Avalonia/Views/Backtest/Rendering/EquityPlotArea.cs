namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>
/// The equity chart's plot rectangle in DIP and the single place that turns a normalized point
/// (<see cref="EquityCurveNormalizedPoint"/>, data coordinates already projected to [0,1] by <see cref="EquityCurveLayout"/>)
/// into screen coordinates. Screen pixels and data values are different quantities: no other code in the equity control may
/// multiply a fraction by a plot dimension. The rectangle is kept as four edges (not left/top/width/height) so
/// <see cref="Width"/> and <see cref="Height"/> are the exact differences the control computed before this type existed.
/// Avalonia-free so it is testable without a UI session.
/// </summary>
public readonly record struct EquityPlotArea(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    /// <summary>Whether both extents are finite and strictly positive (a drawable rectangle).</summary>
    public bool IsDrawable => IsFinitePositive(Width) && IsFinitePositive(Height);

    /// <summary>Whether a screen position (same DIP space as the edges) is inside the plot rectangle, edges included.</summary>
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>Maps a normalized point to screen X/Y: X grows rightwards from <see cref="Left"/>, Y grows upwards from <see cref="Bottom"/>.</summary>
    public (double X, double Y) ToScreen(EquityCurveNormalizedPoint point) =>
        (Left + point.XFraction * Width, Bottom - point.YFraction * Height);

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0d;
}
