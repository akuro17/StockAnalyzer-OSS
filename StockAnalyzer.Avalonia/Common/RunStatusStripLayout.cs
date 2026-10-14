using Avalonia;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Layout tokens of the shared run-status strip (<c>Views/Controls/RunStatusStrip</c>) in device independent pixels. The horizontal padding equals the
/// footer padding of the three training windows (24) so the strip and the footer line up; the bar height follows the progress bars it replaces (6).
/// </summary>
public static class RunStatusStripLayout
{
    public const double BarHeight = 6, RowSpacing = 4, PercentGap = 8, PercentMinimumWidth = 40;
    public const int MessageMaxLines = 2;
    public static Thickness StripPadding { get; } = new(24, 8);
    public static Thickness StripBorder { get; } = new(0, 1, 0, 0);
    public static Thickness PercentMargin { get; } = new(PercentGap, 0, 0, 0);
}
