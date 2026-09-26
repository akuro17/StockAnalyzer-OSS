namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Fixed DIP dimensions for BacktestWindow (spec §5.7 / Gate G6:
/// Y:\0915 Backtesting\03_P3_UIWindow.md). Physical-pixel conversion (RenderScaling) is applied at
/// the View layer only; every value here stays in DIP so it composes with Avalonia layout as-is.
/// </summary>
public static class BacktestUiConstants
{
    public const double HeaderHeight = 56;
    public const double FooterHeight = 64;
    public const double OuterMargin = 16;
    public const double ControlHeight = 32;
    public const double ControlSpacing = 8;
    public const double PlotAxisMarginLeft = 64;
    public const double PlotAxisMarginRight = 16;
    public const double PlotAxisMarginTop = 16;
    public const double PlotAxisMarginBottom = 32;
    public const double EquityLineWidth = 2;
    public const double EquityPointRadius = 4;
    public const double ChartLabelGap = 4;
    public const double ChartLabelBaselineFactor = 2.5;
}
