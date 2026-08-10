using SkiaSharp;
using StockAnalyzer.Core.Models.Analysis;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia; // Added for Point and Rect
using StockAnalyzer.Avalonia.Drawing; // Added for ICoordinateTransform

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renderer for Reverse Watch Curve (XY plot of Volume Average vs Price Average).
/// Unlike other ChartType renderers, this does NOT use time-series X-axis.
/// </summary>
public class ReverseWatchRenderer : IChartRenderer
{
    // Use centralized constants
    private const float MARGIN = ChartTheme.ReverseWatchMargin;
    private const float POINT_RADIUS = ChartTheme.ReverseWatchPointRadius;

    // --- Cached Skia Objects (ZeroAllocation Optimization) ---
    private readonly SKPaint _gridPaint = new SKPaint { 
        Color = SKColors.Gray.WithAlpha(128), StrokeWidth = 1, IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
    };
    private readonly SKPaint _textPaint = new SKPaint {
        Color = SKColors.Black, TextSize = 14, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
    };
    private readonly SKPaint _linePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _markerTextPaint = new SKPaint {
        TextSize = 14, IsAntialias = true, TextAlign = SKTextAlign.Center,
        Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
    };
    private readonly SKPaint _arrowOutlinePaint = new SKPaint {
        Color = new SKColor(255, 255, 255, 210), Style = SKPaintStyle.Stroke, IsAntialias = true,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
    };
    private readonly SKPaint _arrowPaint = new SKPaint {
        Color = SKColors.Red, Style = SKPaintStyle.Stroke, IsAntialias = true,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
    };
    private readonly SKPaint _fillPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _strokePaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
    
    private static readonly SKPath _arrowPath;
    
    static ReverseWatchRenderer()
    {
        _arrowPath = new SKPath();
        _arrowPath.MoveTo(-10, -6);
        _arrowPath.LineTo(0, 0);
        _arrowPath.LineTo(-10, 6);
    }

    /// <summary>
    /// Renders the chart content onto the canvas.
    /// Implementation of IChartRenderer.
    /// </summary>
    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (IReverseWatchRenderConfig)baseConfig;
        if (config.ReverseWatchData != null && config.Transform != null)
        {
            Render(
                canvas, 
                chartArea, 
                config.ReverseWatchData, 
                config.ShowReverseWatchGrid, 
                config.MousePosition, 
                config.Transform,
                config.ReverseWatchLineThickness,
                config);
        }
    }

    /// <summary>
    /// Renders the Reverse Watch Curve in the given chart area.
    /// </summary>
    public void Render(SKCanvas canvas, global::Avalonia.Rect chartArea, ReverseWatchCurveData data, bool showGrid, StockAnalyzer.Core.Models.Point mousePosition, ICoordinateTransform transform, float lineThickness, IChartRenderConfig baseConfig)
    {
        var config = (IReverseWatchRenderConfig)baseConfig;
        if (data == null || data.Points.Count == 0) return;

        var bounds = data.Bounds;
        var volumeRange = bounds.MaxVolume - bounds.MinVolume;
        var priceRange = bounds.MaxPrice - bounds.MinPrice;

        if (volumeRange <= 0m || priceRange <= 0m) return;

        // Use transform for coordinates
        // Calculate Center based on Data Bounds (Fixed relative to data)
        var centerVol = bounds.MinVolume + volumeRange / 2m;
        var centerPrice = bounds.MinPrice + priceRange / 2m;
        
        // Store volume in Ticks (workaround)
        var centerPoint = transform.NumericToScreen((double)centerVol, (double)centerPrice);
        float centerX = (float)centerPoint.X;
        float centerY = (float)centerPoint.Y + (float)chartArea.Y;

        // Draw grid if enabled (Centered on Data)
        if (showGrid)
        {
            DrawPhaseGrid(canvas, centerX, centerY, (float)chartArea.Width, (float)chartArea.Height);
        }

        // Find nearest point for hover indicator and header info
        // mousePosition is in CONTROL-LOCAL coordinates (includes margins).
        // sp from transform.ChartToScreen() is in CHART-AREA-LOCAL coordinates (0..Width, 0..Height).
        // To compare: sp.X + ChartTheme.MarginLeft == mouseX, sp.Y + chartArea.Y == mouseY
        var (nearestPoint, nearestScreenPoint) = GetNearestPointWithScreen(transform, config, data, mousePosition, (float)StockAnalyzer.Avalonia.Views.Chart.ChartTheme.MarginLeft, (float)chartArea.Y);

        // Draw Header Info (Stock Code and Period) removed as per user request (moved to DataWindow)
        if (transform != null)
        {
            DrawCurve(canvas, transform, data, centerX, centerY, (float)chartArea.Width, (float)chartArea.Height, lineThickness, (float)chartArea.Y, config);
        }

        // Draw hover indicator circle at the nearest point
        if (nearestPoint != null && nearestScreenPoint.HasValue)
        {
            DrawHoverIndicator(canvas, nearestScreenPoint.Value);
        }
    }

    /// <summary>
    /// Finds the nearest data point to the mouse position and returns both the point and its screen coordinates.
    /// mousePosition is in CONTROL-LOCAL coordinates (includes layout margins).
    /// transform.ChartToScreen() returns CHART-AREA-LOCAL coordinates (0..Width, 0..Height).
    /// To match: screenX = sp.X + MarginHorizontal, screenY = sp.Y + chartAreaY
    /// </summary>
    private (ReverseWatchCurvePoint? point, SKPoint? screenPoint) GetNearestPointWithScreen(
        ICoordinateTransform transform, IChartRenderConfig baseConfig,
        ReverseWatchCurveData data, StockAnalyzer.Core.Models.Point mousePosition, float chartAreaX, float chartAreaY)
    {
        if (data.Points.Count == 0) return (null, null);

        ReverseWatchCurvePoint? bestPoint = null;
        SKPoint bestScreenPoint = default;
        double minDistSq = double.MaxValue;
        
        float mouseX = (float)mousePosition.X;
        float mouseY = (float)mousePosition.Y;

        int pointCount = data.Points.Count;
        int configCount = ((IReverseWatchRenderConfig)baseConfig).ReverseWatchDataCount;
        int renderCount = configCount > 0 ? Math.Min(pointCount, configCount) : pointCount;
        int startIndex = pointCount - renderCount;

        for (int i = startIndex; i < pointCount; i++)
        {
            var point = data.Points[i];
            var sp = transform.NumericToScreen((double)point.VolumeAverage, (double)point.PriceAverage);
            // Convert chart-area-local to control-local to match mousePosition
            // sp.X is relative to chartArea.Left, so we use MarginLeft (which matches control translation).
            float px = (float)sp.X + StockAnalyzer.Avalonia.Views.Chart.ChartTheme.MarginLeft;
            float py = (float)sp.Y + chartAreaY;

            double dx = mouseX - px;
            double dy = mouseY - py;
            double distSq = dx * dx + dy * dy;

            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                bestPoint = point;
                // Store the CANVAS-LOCAL coordinates for drawing (after pipeline translation)
                bestScreenPoint = new SKPoint((float)sp.X, (float)sp.Y + chartAreaY);
            }
        }
        
        // If mouse is too far from any point (>50px), show header for last point but no hover circle
        if (minDistSq > 50 * 50)
        {
            return (data.Points.LastOrDefault(), null);
        }

        return (bestPoint ?? data.Points.LastOrDefault(), bestScreenPoint);
    }

    /// <summary>
    /// Draws a hover indicator circle at the given screen position.
    /// The canvas is already translated by chartArea.Left in the pipeline.
    /// </summary>
    private void DrawHoverIndicator(SKCanvas canvas, SKPoint screenPoint)
    {
        canvas.DrawCircle(screenPoint.X, screenPoint.Y, POINT_RADIUS + 2, _fillPaint);
        canvas.DrawCircle(screenPoint.X, screenPoint.Y, POINT_RADIUS + 2, _strokePaint);
    }

    private void DrawPhaseGrid(SKCanvas canvas, float centerX, float centerY, float width, float height)
    {
        // centerX/centerY are updated to be the Screen Position of the Data Center
        // width/height are the canvas dimensions (for ray length)

        // Draw 8 Boundary Lines radiating from center to edges/corners
        float halfW = width / 2;
        float halfH = height / 2;
        
        // Lines extend beyond chart area to ensure they cover corners
        double len = Math.Max(width, height) * 1.5;

        for (int i = 0; i < 8; i++)
        {
            double thetaDeg = 22.5 + i * 45;
            double thetaRad = thetaDeg * Math.PI / 180.0;
            
            // Standard Math Vector
            double lx = Math.Cos(thetaRad);
            double ly = Math.Sin(thetaRad);
            
            // Project to Screen Vector (Stretch to Aspect Ratio)
            // ScreenX = lx * halfW
            // ScreenY = -ly * halfH (Y is flipped)
            double sx = lx * halfW;
            double sy = -ly * halfH; 
            
            // Normalize
            double mag = Math.Sqrt(sx*sx + sy*sy);
            if (mag > 0)
            {
                sx /= mag;
                sy /= mag;
                canvas.DrawLine(centerX, centerY, centerX + (float)(sx * len), centerY + (float)(sy * len), _gridPaint);
            }
        }
    }


    private void DrawCurve(SKCanvas canvas, ICoordinateTransform transform,
        ReverseWatchCurveData data, 
        float centerX, float centerY, float width, float height,
        float lineThickness, float chartAreaY, IChartRenderConfig baseConfig)
    {
        var config = (IReverseWatchRenderConfig)baseConfig;

        float scaling = (float)config.RenderScaling;
        _linePaint.StrokeWidth = Math.Max(1.5f, lineThickness) * scaling;

        var pointCount = data.Points.Count;
        if (pointCount < 2) return;

        // Apply Sliding Window: N days extraction
        int configCount = config.ReverseWatchDataCount;
        int renderCount = configCount > 0 ? Math.Min(pointCount, configCount) : pointCount;
        int startIndex = pointCount - renderCount;
        if (renderCount < 2) return;

        // Pre-calculate positions using transform (only for rendered points)
        var screenPoints = new SKPoint[pointCount];
        for (int i = startIndex; i < pointCount; i++)
        {
            var point = data.Points[i];
            var sp = transform.NumericToScreen((double)point.VolumeAverage, (double)point.PriceAverage);
            // Apply Pixel Snapping (Math.Round) to prevent anti-aliasing blur on thin lines
            screenPoints[i] = new SKPoint((float)Math.Round(sp.X), (float)Math.Round(sp.Y + chartAreaY));
        }

        // halfW/halfH used for aspect ratio normalization in angle calculation
        float halfW = width / 2;
        float halfH = height / 2;

        // iterate segments
        for (int i = startIndex + 1; i < pointCount; i++)
        {
             var p1 = screenPoints[i - 1];
             var p2 = screenPoints[i];
             
             // Fade-out effect removed to ensure line remains visible and unbroken
             byte alpha = 255;
             
             DrawSegmentSplitByPhases(canvas, p1, p2, centerX, centerY, halfW, halfH, _linePaint, baseConfig, alpha);
        }

        // Draw Start/End Markers

        // Start ('S') - Blue
        if (renderCount > 0)
        {
            var p0 = screenPoints[startIndex];
            _markerTextPaint.Color = SKColors.Blue;
            // Vertically center (approx +5 for 14px font)
            canvas.DrawText("S", p0.X, p0.Y + 5, _markerTextPaint);
        }

        // End (ArrowHead) - indicates direction
        if (renderCount > 1)
        {
            var pLast = screenPoints[pointCount - 1];
            var pPrev = screenPoints[pointCount - 2];
            
            float dx = pLast.X - pPrev.X;
            float dy = pLast.Y - pPrev.Y;
            float angle = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);

            _arrowOutlinePaint.StrokeWidth = (Math.Max(2f, lineThickness + 0.5f) + 3f) * scaling;
            _arrowPaint.StrokeWidth = Math.Max(2f, lineThickness + 0.5f) * scaling;

            canvas.Save();
            canvas.Translate(pLast.X, pLast.Y);
            canvas.RotateDegrees(angle);
            
            // Draw white halo first
            canvas.DrawPath(_arrowPath, _arrowOutlinePaint);
            // Draw red arrow on top
            canvas.DrawPath(_arrowPath, _arrowPaint);
            
            canvas.Restore();
        }
    }

    private void DrawSegmentSplitByPhases(SKCanvas canvas, SKPoint p1, SKPoint p2, 
        float centerX, float centerY, float halfW, float halfH,
        SKPaint paint, IChartRenderConfig baseConfig, byte alpha = 255)
    {
        var config = (IReverseWatchRenderConfig)baseConfig;
        // 1. Collect Intersections with 8 phase boundary rays
        // We use a list of (t, point) tuples
        var intersections = new List<(float t, SKPoint pt)>();
        
        float dx = p2.X - p1.X;
        float dy = p2.Y - p1.Y;

        // Check intersections with all 8 boundaries
        for (int i = 0; i < 8; i++)
        {
            // Boundary lines are drawn at 22.5 + i*45 degrees
            double thetaDeg = 22.5 + i * 45;
            double thetaRad = thetaDeg * Math.PI / 180.0;

            double lx = Math.Cos(thetaRad);
            double ly = Math.Sin(thetaRad); // Math Y (Up)
            
            // Screen Vector (from center)
            // Screen X = lx * halfW
            // Screen Y = -ly * halfH (Y is flipped)
            double rayDirX = lx * halfW;
            double rayDirY = -ly * halfH;

            // Solve Intersection: P1 + t*D = C + u*R
            // P1x + t*dx = Cx + u*rx
            // P1y + t*dy = Cy + u*ry
            // Rewrite as linear system for t, u:
            // t*dx - u*rx = Cx - P1x
            // t*dy - u*ry = Cy - P1y
            //
            // [ dx   -rx ] [ t ] = [ Cx - P1x ]
            // [ dy   -ry ] [ u ]   [ Cy - P1y ]
            
            double A1 = dx, B1 = -rayDirX, C1 = centerX - p1.X;
            double A2 = dy, B2 = -rayDirY, C2 = centerY - p1.Y;
            
            double det = A1 * B2 - A2 * B1;
            
            if (Math.Abs(det) < 1e-6) continue; // Parallel

            double t = (C1 * B2 - C2 * B1) / det;
            double u = (A1 * C2 - A2 * C1) / det;

            // Strict intersection within segment (exclusive of endpoints to avoid noise)
            // and along the positive ray direction
            if (t > 0.001 && t < 0.999 && u > 0)
            {
                intersections.Add(((float)t, new SKPoint(p1.X + (float)t * dx, p1.Y + (float)t * dy)));
            }
        }
        
        // 2. Sort Intersections by t
        intersections.Sort((a, b) => a.t.CompareTo(b.t));
        
        // 3. Draw Sub-segments
        var pointsToDraw = new List<SKPoint>();
        pointsToDraw.Add(p1);
        foreach (var inter in intersections) pointsToDraw.Add(inter.pt);
        pointsToDraw.Add(p2);
        
        for (int k = 0; k < pointsToDraw.Count - 1; k++)
        {
            var sp = pointsToDraw[k];
            var ep = pointsToDraw[k+1];
            
            // Determine phase at the midpoint of the sub-segment
            var mp = new SKPoint((sp.X + ep.X)/2, (sp.Y + ep.Y)/2);
            var geometricPhase = GetPhaseAtPoint(mp, centerX, centerY, halfW, halfH);
            
            // Adjust phase based on user feedback ("Colors are shifted left by 1").
            // User likely expects the phase color N to apply to the segment leading *towards* phase N+1,
            // or there is a phase definition offset.
            // Previous logic used "GetPreviousPhase" (N -> N-1). 
            // Applying this shift aligns "Phase 2 color" to the "Phase 2" geometric region (if the definitions were shifted) 
            var drawingPhase = GetPreviousPhase(geometricPhase);
             
            var phaseColor = config.GetPhaseColor(drawingPhase).ToSkColor();
            paint.Color = new SKColor(phaseColor.Red, phaseColor.Green, phaseColor.Blue, alpha);
            
            canvas.DrawLine(sp, ep, paint);
        }
    }

    private ReverseWatchPhase GetPhaseAtPoint(SKPoint p, float cx, float cy, float halfW, float halfH)
    {
         // Project back to Normalized Unit Circle (Math Coordinates)
         // sx = lx * halfW  => lx = sx / halfW
         // sy = -ly * halfH => ly = -sy / halfH
         
         double sx = p.X - cx;
         double sy = p.Y - cy;
         
         double lx = sx / halfW;
         double ly = -sy / halfH; // Flip Y back to standard math Y (Up is positive)
         
         double angleRad = Math.Atan2(ly, lx); 
         double angleDeg = angleRad * 180.0 / Math.PI;
         if (angleDeg < 0) angleDeg += 360.0;
         
         // Map angle to Phase
         // Phase 3: East (0 deg) -> Region [337.5, 22.5)
         // And so on, 45 degree steps
         
         if (angleDeg >= 337.5 || angleDeg < 22.5) return ReverseWatchPhase.Phase3; // East
         if (angleDeg < 67.5) return ReverseWatchPhase.Phase4;  // NE
         if (angleDeg < 112.5) return ReverseWatchPhase.Phase5; // North
         if (angleDeg < 157.5) return ReverseWatchPhase.Phase6; // NW
         if (angleDeg < 202.5) return ReverseWatchPhase.Phase7; // West
         if (angleDeg < 247.5) return ReverseWatchPhase.Phase8; // SW
         if (angleDeg < 292.5) return ReverseWatchPhase.Phase1; // South
         return ReverseWatchPhase.Phase2; // SE
    }

    private ReverseWatchPhase GetPreviousPhase(ReverseWatchPhase phase)
    {
        return phase switch
        {
            ReverseWatchPhase.Phase1 => ReverseWatchPhase.Phase8,
            ReverseWatchPhase.Phase2 => ReverseWatchPhase.Phase1,
            ReverseWatchPhase.Phase3 => ReverseWatchPhase.Phase2,
            ReverseWatchPhase.Phase4 => ReverseWatchPhase.Phase3,
            ReverseWatchPhase.Phase5 => ReverseWatchPhase.Phase4,
            ReverseWatchPhase.Phase6 => ReverseWatchPhase.Phase5,
            ReverseWatchPhase.Phase7 => ReverseWatchPhase.Phase6,
            ReverseWatchPhase.Phase8 => ReverseWatchPhase.Phase7,
            _ => phase
        };
    }
}

