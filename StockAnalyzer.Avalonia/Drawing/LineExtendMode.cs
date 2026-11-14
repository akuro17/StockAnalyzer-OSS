namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Which ends of a two-point line are extended to the border of the drawing area.
/// "Right" is the P1 to P2 direction and "Left" is the P2 to P1 direction (click order, not screen side).
/// Values are persisted by number: never reorder or renumber.
/// </summary>
public enum LineExtendMode
{
    /// <summary>The segment between the two points only.</summary>
    None = 0,

    /// <summary>Extended beyond P2 (P1 to P2 direction).</summary>
    Right = 1,

    /// <summary>Extended beyond P1 (P2 to P1 direction).</summary>
    Left = 2,

    /// <summary>Extended beyond both points.</summary>
    Both = 3
}
