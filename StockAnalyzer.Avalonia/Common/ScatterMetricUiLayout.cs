using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Dimensions of the scatter metric registration window, in device independent pixels. Header 56 and footer 64 come from
/// <see cref="ToolWindowUiLayout"/> (SA_UI_SUBWINDOW_STANDARD 1.1); the control heights follow 2.1 (32 for text boxes and combo boxes, 34 for the primary
/// action). The sizes of the window are those of the other registration windows (Source Indicator Registration).
/// </summary>
public static class ScatterMetricUiLayout
{
    public const double WindowWidth = 1100, WindowHeight = 650, WindowMinWidth = 900, WindowMinHeight = 550;
    public const double NavMinWidth = 150, CatalogMinWidth = 180, DetailMinWidth = 300;
    public const double ControlHeight = 32, AddButtonHeight = 34, RowButtonSize = 26, RowIconSize = 12, EditorControlWidth = 140;

    /// <summary>The width of the caption column of the "target" card, the same 260 the Screener registration card uses.</summary>
    public static GridLength LabelColumn { get; } = new(260);
}
