using Avalonia;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Layout tokens of the state labels shown in the footer of the training windows (the look of the other badges: secondary background, small radius, 6 / 2 padding), in device independent pixels.</summary>
public static class RunModeBadgeLayout
{
    public const double Spacing = 6, CornerRadiusValue = 4, ReasonMaximumWidth = 420, LineSpacing = 2, FooterGap = 16;
    public static Thickness Padding { get; } = new(6, 2);
    public static Thickness FooterMargin { get; } = new(FooterGap, 0);
    public static CornerRadius Corner { get; } = new(CornerRadiusValue);
}
