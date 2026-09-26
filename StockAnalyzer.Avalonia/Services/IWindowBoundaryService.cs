using Avalonia;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Service for validating and adjusting window coordinates to ensure visibility on the current desktop.
/// </summary>
public interface IWindowBoundaryService
{
    /// <summary>
    /// Validates a proposed window rect and returns an adjusted rect guaranteed to be visible.
    /// </summary>
    /// <param name="x">Proposed X coordinate (screen coordinates).</param>
    /// <param name="y">Proposed Y coordinate.</param>
    /// <param name="width">Proposed width.</param>
    /// <param name="height">Proposed height.</param>
    /// <returns>A tuple of adjusted (X, Y, Width, Height).</returns>
    (double X, double Y, double Width, double Height) EnsureVisible(double x, double y, double width, double height);
}
