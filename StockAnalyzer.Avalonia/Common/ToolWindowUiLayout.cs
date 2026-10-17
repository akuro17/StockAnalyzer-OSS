using Avalonia;
using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Frame of the operation windows opened from the Tools menu (RL Playback, Prediction Models), in device independent pixels: header 56 and footer 64
/// follow SA_UI_SUBWINDOW_STANDARD 1.1, the content padding is the 24 the Settings window gives its pages. One owner, so the windows cannot drift apart; the header and footer heights are also the ones of the Backtest, Portfolio RL and Training Wizard frames.
/// </summary>
public static class ToolWindowUiLayout
{
    public const double HeaderHeight = 56, FooterHeight = 64, ContentPadding = 24, ButtonSpacing = 12, ButtonWidth = 100, HeaderIconSize = 20;
    public static GridLength HeaderRow { get; } = new(HeaderHeight);
    public static GridLength BodyRow { get; } = new(1, GridUnitType.Star);
    public static GridLength FooterRow { get; } = new(FooterHeight);
    public static Thickness ContentMargin { get; } = new(ContentPadding);
    public static Thickness BarPadding { get; } = new(ContentPadding, 0);
    public static Thickness HeaderBorder { get; } = new(0, 0, 0, 1);
    public static Thickness FooterBorder { get; } = new(0, 1, 0, 0);
}
