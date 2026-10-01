namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// DIP dimensions and UI metrics for BacktestWindow (the window dimensions come from spec §5.7 / Gate G6:
/// Y:\0915 Backtesting\03_P3_UIWindow.md). Physical-pixel conversion (RenderScaling) is applied at
/// the View layer only; every value here stays in DIP so it composes with Avalonia layout as-is.
/// The equity chart's plot margins are an owner decision (2026-10-01), not a spec value: the Y-axis labels are drawn in the RIGHT
/// margin (wide, <see cref="PlotAxisMarginRight"/>), the left margin is only a gutter. The Equity* members below are UI gesture and
/// hover metrics of the equity chart (zoom step, hit tolerance, marker and crosshair sizes), kept here by owner decision.
/// </summary>
public static class BacktestUiConstants
{
    public const double HeaderHeight = 56;
    public const double FooterHeight = 64;
    public const double OuterMargin = 16;
    public const double ControlHeight = 32;
    public const double ControlSpacing = 8;
    public const double PlotAxisMarginLeft = 16;
    public const double PlotAxisMarginRight = 64;
    public const double PlotAxisMarginTop = 16;
    public const double PlotAxisMarginBottom = 32;
    public const double EquityLineWidth = 2;
    public const double EquityPointRadius = 4;
    public const double ChartLabelGap = 4;
    public const double ChartLabelBaselineFactor = 2.5;

    /// <summary>Equity chart wheel zoom: scale multiplier of one wheel notch towards zoom-in. Zoom-out uses the reciprocal (0.909), so a notch in and a notch out cancel exactly; the main chart's 0.9 does not.</summary>
    public const double EquityZoomInFactor = 1.1;

    /// <summary>Equity chart axes: minimum DIP between two neighbouring tick labels (a label is dropped or the tick step grows otherwise).</summary>
    public const double EquityAxisLabelMinGap = 8;

    /// <summary>Equity chart hairline: the one thickness (DIP) of its grid lines, crosshair lines and marker outlines.</summary>
    public const double EquityHairlineWidth = 1;

    /// <summary>Equity chart crosshair: the dash/gap length of its dashed lines (same look as the main chart's crosshair).</summary>
    public const double EquityCrosshairDashLength = 4;

    /// <summary>Equity chart hover readout (value panel and axis labels): inner padding, and the distance between the pointer and the panel.</summary>
    public const double EquityHoverPadding = 6;
    public const double EquityHoverPointerOffset = 12;
    public const double EquityHoverCornerRadius = 3;

    /// <summary>Equity chart trade markers: half size of a marker, and how close (DIP) the pointer must be to a marker's center to hover it.</summary>
    public const double EquityMarkerRadius = 5;
    public const double EquityMarkerHitTolerance = 8;
}
