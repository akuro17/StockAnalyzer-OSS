namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Specifies the movement axis constraint mode for chart drawing tools.
/// </summary>
public enum DrawingMoveAxisMode
{
    /// <summary>
    /// Free 2D movement in both X (Time) and Y (Price) axes.
    /// Magnet snapping to candles is enabled if active.
    /// </summary>
    XY = 0,

    /// <summary>
    /// Horizontal parallel translation only along the X (Time) axis.
    /// Y (Price) position is strictly preserved, and magnet snapping is disabled.
    /// </summary>
    X = 1,

    /// <summary>
    /// Vertical parallel translation only along the Y (Price) axis.
    /// X (Time) position is strictly preserved, and magnet snapping is disabled.
    /// </summary>
    Y = 2
}
