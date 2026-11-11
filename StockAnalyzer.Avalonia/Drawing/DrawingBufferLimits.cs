namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Shared buffer-size limit of the drawing and hit-test code that copies the screen points of an object into a scratch buffer. Drawing
/// code has no access to configuration, so this is a deliberate compiled constant; drawing objects must reference it instead of
/// declaring their own copy.
/// </summary>
internal static class DrawingBufferLimits
{
    /// <summary>
    /// Largest element count of a per-draw or per-hit-test scratch buffer (screen points, weights, segment lengths) taken with
    /// <c>stackalloc</c> (at most 1 KiB for 8-byte elements); larger buffers come from <see cref="System.Buffers.ArrayPool{T}"/>.
    /// </summary>
    internal const int StackAllocElementThreshold = 128;
}
