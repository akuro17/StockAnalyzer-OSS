using Avalonia;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Layout tokens of the RL Playback settings page, in device independent pixels (same visual values as the AI Predictions cards).</summary>
public static class RlPlaybackUiLayout
{
    public static Thickness CardPadding { get; } = new(16);
    public const double SectionSpacing = 10.0;
    public const double ModelListMaxHeight = 190.0;
    /// <summary>SA_UI_SUBWINDOW_STANDARD 2.1: TextBox / ComboBox height of 32 or 36 px.</summary>
    public const double InputHeight = 36.0;
}
