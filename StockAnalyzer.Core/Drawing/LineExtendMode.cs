namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// Which ends of a two-point line are extended to the border of the drawing area.
/// "Right" is the screen-right direction (larger X; for vertical lines, the lower/second endpoint)
/// and "Left" is the screen-left direction (smaller X; for vertical lines, the upper/first endpoint).
/// The V2 drawing file stores the member name (JsonStringEnumConverter, see DrawingDocumentJsonOptions): never rename, reorder or renumber a member.
/// </summary>
public enum LineExtendMode
{
    /// <summary>The segment between the two points only.</summary>
    None = 0,

    /// <summary>Extended beyond the right-side endpoint.</summary>
    Right = 1,

    /// <summary>Extended beyond the left-side endpoint.</summary>
    Left = 2,

    /// <summary>Extended beyond both points.</summary>
    Both = 3
}
