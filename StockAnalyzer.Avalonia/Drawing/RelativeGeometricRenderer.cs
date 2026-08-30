using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Base class for all drawing elements that require pixel-independent, geometric precision.
/// Provides logical snapping and ZeroAllocation rendering capabilities (FR-39-5-01, FR-39-5-04).
/// </summary>
public abstract class RelativeGeometricRenderer : IChartObject, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public abstract ChartObjectType Type { get; }
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    // FR-39-5-04: ZeroAllocation cached paint and path
    protected readonly SKPaint _cachedPaint;
    protected readonly SKPath _cachedPath;

    protected RelativeGeometricRenderer()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = DrawingThemeContext.DefaultStrokeThickness;

        _cachedPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _cachedPath = new SKPath();
    }

    /// <summary>
    /// Implements ZeroAllocation rendering by updating cached properties and invoking DrawGeometry.
    /// </summary>
    public virtual void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (!IsVisible) return;

        // Update paint properties
        _cachedPaint.Color = SkiaColor;
        _cachedPaint.StrokeWidth = (float)Thickness;

        _cachedPath.Reset();

        // Delegate to subclass
        DrawGeometry(canvas, transform);

        // Draw selection handles, highlighting the reference/anchor control point distinctly.
        if (IsSelected)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                var screenPt = transform.ChartToScreen(Points[i]);
                bool isAnchor = i == AnchorPointIndex;
                SelectionHandleRenderer.Draw(canvas, screenPt, isAnchor ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            }
        }
    }

    /// <summary>
    /// Subclasses must implement actual drawing logic utilizing the cached _cachedPath and _cachedPaint overhead-free.
    /// </summary>
    protected abstract void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform);

    public abstract bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance);

    public virtual void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    /// <summary>
    /// Calculates the correct end coordinate for a True Angle (e.g. 45 degrees) assuming a 1:1 Aspect Ratio logic.
    /// FR-39-5-01, FR-39-5-03: 1:1アスペクト比を前提とした斜め45度線�E数学皁E��義
    /// </summary>
    /// <param name="start">Start coordinate</param>
    /// <param name="angleDegrees">Angle in degrees (0 is horizontal right, positive is mathematically "up")</param>
    /// <param name="distanceInBars">Length of the hypotenuse in logical bar units</param>
    /// <param name="pricePerBar">The logical Y/X ratio (e.g. 1 bar equals how much price)</param>
    /// <param name="timeframeInterval">The timespan of a single bar (e.g. 1 day)</param>
    protected ChartPoint CalculateTrueAnglePoint(ChartPoint start, double angleDegrees, double distanceInBars, double pricePerBar, TimeSpan timeframeInterval)
    {
        double angleRad = angleDegrees * Math.PI / 180.0;
        
        // Logical dx and dy in a perfectly square 1:1 grid
        double logicalDx = distanceInBars * Math.Cos(angleRad);
        double logicalDy = distanceInBars * Math.Sin(angleRad);
        
        // Map to chart scale
        long deltaTicks = (long)(logicalDx * timeframeInterval.Ticks);
        decimal deltaPrice = (decimal)(logicalDy * pricePerBar);
        
        return new ChartPoint(start.Time.AddTicks(deltaTicks), start.Price + deltaPrice);
    }

    /// <summary>
    /// Snaps a screen point to the nearest logical grid or candle OHLC point recursively.
    /// FR-39-5-02: 描画時にマスの「角」や「端�E�高値/安値�E�」を検�Eし、�E動スナップすめE
    /// </summary>
    protected ChartPoint SnapToLogicalPoint(
        global::Avalonia.Point screenPoint, 
        ICoordinateTransform transform, 
        IReadOnlyList<CoreCandleData> candles, 
        StockAnalyzer.Avalonia.Services.Drawing.IMagnetSnapService magnetSnapService)
    {
        // 1. Existing MagnetSnapService (OHLC wick/body snapping - preserves all past behavior)
        if (magnetSnapService != null && candles != null && candles.Count > 0)
        {
            var snapResult = magnetSnapService.GetMagnetSnap(screenPoint, candles, transform);
            if (snapResult.IsSnapped)
            {
                return snapResult.SnappedChartPoint;
            }
        }

        var rawPoint = transform.ScreenToChart(screenPoint);

        // 2. Logical Geometric Grid Snapping (マスの角への吸着)
        // Check if we are drawing on a discrete coordinate grid (like P&F, Renko, Kagi)
        if (transform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Index)
        {
            double index;
            if (rawPoint.Time.Ticks > 31536000000000000L) // 1000 years threshold
            {
                index = gct.GetFractionalIndex(rawPoint.Time);
            }
            else
            {
                index = (double)rawPoint.Time.Ticks;
            }

            double snappedIndex = Math.Round(index);
            DateTime snappedTime;
            if (gct.TimeMap != null && gct.TimeMap.Count > 0)
            {
                snappedTime = gct.GetTimeFromFractionalIndex(snappedIndex);
            }
            else
            {
                snappedTime = GenericCoordinateTransform.SafeIndexPoint(snappedIndex).Time;
            }
            
            decimal snappedPrice = rawPoint.Price;
            
            // Snap Y to the nearest logical box size if a 1:1 aspect ratio geometric constraint exists
            if (gct.FixedPricePerIndex.HasValue && gct.FixedPricePerIndex.Value > 0)
            {
                decimal boxRatio = gct.FixedPricePerIndex.Value;
                snappedPrice = Math.Round(rawPoint.Price / boxRatio) * boxRatio;
            }
            
            return new ChartPoint(snappedTime, snappedPrice);
        }

        // Fallback: return raw mathematical logical point for unconstrained time-based spaces
        return rawPoint;
    }

    public virtual void Dispose()
    {
        _cachedPaint?.Dispose();
        _cachedPath?.Dispose();
        GC.SuppressFinalize(this);
    }
}
