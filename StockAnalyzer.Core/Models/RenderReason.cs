namespace StockAnalyzer.Core.Models;

/// <summary>
/// Defines the reasons for a chart rendering request.
/// Used as bit flags to accumulate multiple reasons between render frames.
/// </summary>
[System.Flags]
public enum RenderReason
{
    /// <summary>No reason specified.</summary>
    None = 0,

    /// <summary>Primary data (Candles, Indicators) has changed. Requires recalculation.</summary>
    DataChanged = 1 << 0,

    /// <summary>Visual settings (Colors, Thickness, Theme) have changed. Redraw only.</summary>
    VisualChanged = 1 << 1,

    /// <summary>Selection state (Hovered candle, Selected tool) has changed. Redraw only.</summary>
    SelectionChanged = 1 << 2,

    /// <summary>Viewport (Zoom, Scroll) has changed. Redraw only.</summary>
    ViewportChanged = 1 << 3,

    /// <summary>Forced redraw (e.g., Resize, Window focus). Redraw only.</summary>
    ForceRedraw = 1 << 7
}
