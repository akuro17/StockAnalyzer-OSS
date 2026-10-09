using Avalonia;
using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Layout tokens of the portfolio RL window that the shared <see cref="RlUiLayout"/> does not own, in device independent pixels. The bar heights follow
/// SA_UI_SUBWINDOW_STANDARD 1.1 (header 56, footer 64) and the control height 2.1 (36); the list viewport is a number of rows so the scrollbar appears
/// exactly when more rows than that are shown.
/// </summary>
public static class PrlUiLayout
{
    public const double HeaderHeight = 56, FooterHeight = 64, ControlHeight = 36;
    public const double SourceRowHeight = 28, HeaderIconSize = 20, TreeIconSize = 12, LabelGap = 4, ButtonSpacing = 12;
    public const double ButtonMinimumWidth = 110, ButtonPadding = 16;
    public const int SourceListVisibleRows = 10;
    public const double SourceListMaxHeight = SourceListVisibleRows * SourceRowHeight;
    public static GridLength HeaderRow { get; } = new(HeaderHeight);
    public static GridLength FooterRow { get; } = new(FooterHeight);
    public static GridLength BodyRow { get; } = new(1, GridUnitType.Star);
    public static Thickness BarPadding { get; } = new(24, 0);
    public static Thickness HeaderBorder { get; } = new(0, 0, 0, 1);
    public static Thickness FooterBorder { get; } = new(0, 1, 0, 0);
    public static Thickness ActionPadding { get; } = new(ButtonPadding, 0);
    public static Thickness LabelMargin { get; } = new(0, 0, 0, LabelGap);
    public static Thickness TableRowMargin { get; } = new(0, 1);
    public static CornerRadius SectionCornerRadius { get; } = new(8);
}
