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
    List<ChartPoint> Points { get; }
    Color Color { get; set; }
    double Thickness { get; set; }
    bool IsSelected { get; set; }

    /// <summary>
    /// Controls visibility. When false, the object is not rendered and is excluded from hit tests.
    /// </summary>
    bool IsVisible { get => true; set { } }

    /// <summary>
    /// Z-order for rendering. Higher values are drawn on top of lower values.
    /// </summary>
    int ZIndex { get => 0; set { } }

    /// <summary>
    /// Priority level for resolving rendering or signal conflicts.
    /// Higher values carry more weight. Default is 0.
    /// </summary>
    int Priority { get => 0; set { } }


    /// <summary>Render object to SKCanvas (SkiaSharp)</summary>
    void Render(SkiaSharp.SKCanvas canvas, ICoordinateTransform transform);

    /// <summary>Get SkiaSharp Color</summary>
    SkiaSharp.SKColor SkiaColor { get; }

    /// <summary>Hit test at screen coordinate</summary>
    bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance);

    /// <summary>Translate object by offset</summary>
    void Translate(TimeSpan timeDelta, decimal priceDelta);
}

