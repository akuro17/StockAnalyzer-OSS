using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Interface for all drawable chart objects (Avalonia Version)
/// </summary>
public interface IChartObject
{
    Guid Id { get; }
    ChartObjectType Type { get; }

    /// <summary>
    /// Optional user-assigned display name shown in the Layers Panel in place of the localized type name.
    /// Null or empty means "use the default localized type name". Must be a real settable property on every
    /// implementer (not a default interface member) so it round-trips through <c>ChartObjectJsonConverter</c>'s
    /// reflection-based serialization.
    /// </summary>
    string? CustomName { get; set; }

    List<ChartPoint> Points { get; }
    Color Color { get; set; }
    double Thickness { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    bool IsSelected { get; set; }

    /// <summary>
    /// Controls visibility. When false, the object is not rendered and is excluded from hit tests.
    /// </summary>
    bool IsVisible { get; set; }

    /// <summary>
    /// Prevents deletion when locked.
    /// </summary>
    bool IsLocked { get; set; }

    /// <summary>
    /// Z-order for rendering. Higher values are drawn on top of lower values.
    /// </summary>
    int ZIndex { get; set; }

    /// <summary>
    /// Priority level for resolving rendering or signal conflicts.
    /// Higher values carry more weight. Default is 0.
    /// </summary>
    int Priority { get => 0; set { } }

    /// <summary>
    /// Movement axis constraint mode (XY, X, Y). Default is XY (free 2D movement). Must be a real
    /// settable property on every implementer (not a default interface member) so it round-trips
    /// through <c>ChartObjectJsonConverter</c>'s reflection-based serialization -- see
    /// <see cref="CustomName"/> for why a DIM here would silently fail to persist.
    /// </summary>
    DrawingMoveAxisMode MoveAxisMode { get; set; }

    /// <summary>
    /// Whether <see cref="MoveAxisMode"/> was explicitly chosen for this object (via
    /// <c>ChartObjectManager.SetMoveAxisMode</c>), as opposed to still following the chart-wide
    /// default. False by default. Needed because an explicit choice of XY (the same value as the
    /// default) would otherwise be indistinguishable from "never touched" -- see
    /// <c>ChartObjectManager.HasExplicitMoveAxisMode</c>. Must be a real settable property on every
    /// implementer (not a default interface member) so it round-trips through
    /// <c>ChartObjectJsonConverter</c>'s reflection-based serialization -- see
    /// <see cref="CustomName"/> for why a DIM here would silently fail to persist.
    /// </summary>
    bool IsMoveAxisModeExplicit { get; set; }

    /// <summary>
    /// Index into <see cref="Points"/> designating the reference/anchor control point
    /// used as the pivot for future rotation and extension-line operations.
    /// Default is 0 (the start point).
    /// </summary>
    int AnchorPointIndex { get => 0; set { } }


    /// <summary>Render object to SKCanvas (SkiaSharp)</summary>
    void Render(SkiaSharp.SKCanvas canvas, ICoordinateTransform transform);

    /// <summary>Get SkiaSharp Color</summary>
    SkiaSharp.SKColor SkiaColor { get; }

    /// <summary>Hit test at screen coordinate</summary>
    bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance);

    /// <summary>Translate object by offset</summary>
    void Translate(TimeSpan timeDelta, decimal priceDelta);
}

