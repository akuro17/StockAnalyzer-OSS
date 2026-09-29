using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Defines the orientation of a smart guide line.
/// </summary>
public enum SmartGuideAxis : byte
{
    /// <summary>Horizontal guide line (snapped along the Y-axis/Price).</summary>
    Horizontal,

    /// <summary>Vertical guide line (snapped along the X-axis/Time).</summary>
    Vertical
}

/// <summary>
/// Immutable value-type representing an active smart guide line to be rendered.
/// Zero-allocation, stack-friendly.
/// </summary>
public readonly record struct SmartGuideLine(
    SmartGuideAxis Axis,
    float Position,     // Screen pixel coordinate (X for Vertical, Y for Horizontal)
    float SpanStart,    // Screen pixel start (Top for Vertical, Left for Horizontal)
    float SpanEnd,      // Screen pixel end (Bottom for Vertical, Right for Horizontal)
    Guid TargetObjectId // The object to which snapping occurred
);

/// <summary>
/// Result of a smart guide calculation.
/// </summary>
public readonly record struct SmartSnapResult(
    global::Avalonia.Point SnappedScreenPoint,
    ChartPoint SnappedChartPoint,
    double CorrectionX,  // Screen-space correction applied on X (0 if not snapped)
    double CorrectionY,  // Screen-space correction applied on Y (0 if not snapped)
    bool IsSnappedX,
    bool IsSnappedY
)
{
    public bool IsSnapped => IsSnappedX || IsSnappedY;
}

/// <summary>
/// High-performance, Zero-Allocation service for calculating smart guides and object-to-object snaps.
/// </summary>
public interface ISmartGuideService
{
    /// <summary>
    /// Computes snap corrections and generates guide lines during whole-object translation.
    /// </summary>
    /// <param name="draggedObject">The object currently being dragged (excluded from target comparison).</param>
    /// <param name="proposedBoundsScreen">The proposed Screen-space AABB of the dragged object if moved without snap.</param>
    /// <param name="allObjects">All managed objects in current context.</param>
    /// <param name="transform">Coordinate transform.</param>
    /// <param name="chartArea">Visible chart drawing rectangle (for clamping and guide extension).</param>
    /// <param name="snapThreshold">Pixel distance threshold for snapping.</param>
    /// <param name="outGuideLines">Caller-injected reusable buffer for output guide lines (Zero-Allocation).</param>
    /// <returns>Snap result containing corrected screen offset and snap status.</returns>
    SmartSnapResult SnapObjectMove(
        IChartObject draggedObject,
        Rect proposedBoundsScreen,
        IReadOnlyList<IChartObject> allObjects,
        ICoordinateTransform transform,
        Rect chartArea,
        double snapThreshold,
        List<SmartGuideLine> outGuideLines);

    /// <summary>
    /// Computes snap corrections and generates guide lines during single handle/vertex resizing.
    /// </summary>
    SmartSnapResult SnapHandleMove(
        IChartObject draggedObject,
        int handleIndex,
        global::Avalonia.Point proposedHandleScreen,
        IReadOnlyList<IChartObject> allObjects,
        ICoordinateTransform transform,
        Rect chartArea,
        double snapThreshold,
        List<SmartGuideLine> outGuideLines);
}
