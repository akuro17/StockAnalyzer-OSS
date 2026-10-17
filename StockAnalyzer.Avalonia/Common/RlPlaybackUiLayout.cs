using Avalonia;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Layout tokens of the RL Playback page and window, in device independent pixels (same visual values as the AI Predictions cards).</summary>
public static class RlPlaybackUiLayout
{
    public static Thickness CardPadding { get; } = new(16);
    public const double SectionSpacing = 10.0;
    public const double ModelListMaxHeight = 190.0;
    /// <summary>SA_UI_SUBWINDOW_STANDARD 2.1: TextBox / ComboBox height of 32 or 36 px.</summary>
    public const double InputHeight = 36.0;
    /// <summary>Size of the RL Playback window; the frame (bars, padding) is <see cref="ToolWindowUiLayout"/>.</summary>
    public const double WindowWidth = 880.0, WindowHeight = 780.0, MinimumWidth = 720.0, MinimumHeight = 560.0;
}
