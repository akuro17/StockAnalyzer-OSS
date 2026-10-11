using System;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Interface for chart drawing objects that support 8-point Axis-Aligned Bounding Box (AABB)
/// affine deformation and bounding box extraction (e.g. FreehandObject, HighlighterObject).
/// </summary>
public interface IAabbDeformableObject : IChartObject
{
    /// <summary>
    /// Gets the axis-aligned bounding box in chart coordinates.
    /// </summary>
    void GetDataBounds(out DateTime minTime, out DateTime maxTime, out decimal minPrice, out decimal maxPrice);

    /// <summary>
    /// Computes the 8 deformation handle positions in screen coordinates:
    /// 0=TopLeft, 1=TopCenter, 2=TopRight, 3=RightCenter,
    /// 4=BottomRight, 5=BottomCenter, 6=BottomLeft, 7=LeftCenter.
    /// </summary>
    global::Avalonia.Point[] GetAabbHandlePositions(ICoordinateTransform transform);

    /// <summary>
    /// Deforms the points using normalized affine mapping to match the new bounding box.
    /// </summary>
    void TransformAabb(DateTime newMinTime, DateTime newMaxTime, decimal newMinPrice, decimal newMaxPrice);
}
