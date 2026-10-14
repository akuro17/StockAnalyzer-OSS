using Avalonia;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Versioned RL dialog layout tokens, in device independent pixels.</summary>
public static class RlUiLayout
{
    public const double WindowWidth = 900, WindowHeight = 780, MinimumWidth = 600, MinimumHeight = 400;
    public const double SectionGap = 12, FieldGap = 8, LabelMaximumWidth = 320;
    public const double FormMaximumWidth = 840, InputMinimumWidth = 140, DateMinimumWidth = 180;
    public const double InputMaximumWidth = 320, ChoiceMaximumWidth = 320;
    public const double InputWidth = 160, ChoiceWidth = 240;
    public const double ColumnSpacing = 20, RowSpacing = 12;
    public static Thickness OuterMargin { get; } = new(12);
    public static Thickness SectionPadding { get; } = new(12);
    public static Thickness SectionBorder { get; } = new(1);
    public static Thickness ChildMargin { get; } = new(0, 0, FieldGap, FieldGap);
    public static CornerRadius SectionCornerRadius { get; } = new(8);
}
