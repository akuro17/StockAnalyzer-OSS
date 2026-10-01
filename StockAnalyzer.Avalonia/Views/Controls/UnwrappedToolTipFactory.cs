using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Views.Controls
{
    /// <summary>
    /// Builds the hover popup of the Tickers-tab Notes/Reminder cells. Avalonia's Fluent theme gives every
    /// <see cref="ToolTip"/> a MaxWidth of 320 px, so a non-wrapping line longer than that was silently cut at
    /// the popup edge - which looked like a per-line character limit. Passing an explicit
    /// <see cref="ToolTip"/> instance with no width limit (instead of a bare content control that Avalonia wraps
    /// in a default ToolTip) lets each text line show in full; the app-wide ToolTip style (background, border,
    /// padding in App.axaml) still applies to it because that style targets the ToolTip type.
    /// A line wider than the screen is accepted by design (user decision): the popup neither wraps nor scrolls.
    /// </summary>
    internal static class UnwrappedToolTipFactory
    {
        public static ToolTip Create(Control content) => new ToolTip
        {
            Content = content,
            MaxWidth = double.PositiveInfinity
        };
    }
}
