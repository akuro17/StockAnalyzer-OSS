namespace StockAnalyzer.Core.Models;

/// <summary>
/// Specifies what happens to the active drawing tool after a shape with a fixed
/// start/end point count (1+ required steps) finishes being placed.
/// </summary>
public enum DrawingToolContinuationMode
{
    /// <summary>
    /// The active tool reverts to Pointer once the shape is finished (default).
    /// </summary>
    ReturnToPointer,

    /// <summary>
    /// The active tool stays selected after the shape is finished, so the next click
    /// starts a brand-new shape at that click's position.
    /// </summary>
    ContinueDrawing
}
