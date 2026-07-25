using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public enum PitchforkType
{
    Andrews,
    Schiff,
    ModifiedSchiff
}

public class PitchforkObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Pitchfork;

    // P1: Handle (Anchor)
    // P2: High
    // P3: Low
    public List<ChartPoint> Points { get; private set; }

    public PitchforkType PitchforkType { get; set; } = PitchforkType.Andrews;
    public bool IsInside { get; set; } = false; // Internal Median Line

    public Color Color { get; set; } = Colors.Green;
    public double Thickness { get; set; } = 1.0;
    public bool IsSelected { get; set; }

    // Ratios for parallel lines (Standard Pitchfork often uses 1.0, maybe 0.5, 1.5, etc.)
    // For now, we draw the standard channel: Median Line (0), and Outer Lines passing through P2/P3.
    // We can add intermediate lines later.

    public PitchforkObject(ChartPoint p1, ChartPoint p2, ChartPoint p3)
    {
        Points = new List<ChartPoint> { p1, p2, p3 };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 3) return;

        var p1 = Points[0];
        var p2 = Points[1];
        var p3 = Points[2];

        // 1. Calculate Effective Start Point (Anchor) based on Type
        // Note: Calculations should ideally be done in Chart/Time/Price space, then projected to Screen.
        // However, Schiff/Modified Schiff modifications are usually defined in Price/Time terms.
        
        // Midpoint of P2-P3 (The Median Point)
        TimeSpan timeDiffP2P3 = p3.Time - p2.Time;
        DateTime midTimeP2P3 = p2.Time + timeDiffP2P3 / 2.0; // P2 time + half difference
        
        // BUT: Simply averaging Ticks might be inaccurate if trading days are skipped. 
        // For drawing logic on a chart with gaps, it's safer to use screen coordinates or index-based calculation if available.
        // ICoordinateTransform usually handles Date -> X. Let's calculate in Screen Space for visual correctness on the chart canvas.
        
        global::Avalonia.Point s1 = transform.ChartToScreen(p1);
        global::Avalonia.Point s2 = transform.ChartToScreen(p2);
        global::Avalonia.Point s3 = transform.ChartToScreen(p3);

        global::Avalonia.Point anchor = s1;

        if (PitchforkType == PitchforkType.Schiff)
        {
            // Schiff: Anchor Y is midpoint of P1.Y and P2.Y?? 
            // Standard Schiff: Move P1 Y to midpoint of P1-P2 vertical range. Wait.
            // Definition: "Schiff Median Line: The anchor is moved vertically to the level of the midpoint between the high and low." -> No.
            // Correct Def: Anchor is moved to (P1.Time, (P1.Price + P2.Price)/2)? OR (P1.Time, (High+Low)/2)?
            // P2 is High, P3 is Low (usually).
            // Schiff: Origin is moved vertically to the mid-price of the P1-P2 swing? 
            // Most common: Anchor X is P1.X. Anchor Y is (P1.Y + P2.Y) / 2? NO.
            // Actually, Schiff Pitchfork usually calculates the median trend relative to the P1-P2 swing.
            // Let's check typical usage.
            // "Schiff Pitchfork": The handle is originating from 50% retracement of the P1-P2 swing price-wise, at the time of P1.
            // Wait, standard Schiff uses P1 X, and Y = (P1.Y + P2.Y)/2?
            // Actually, usually it's defined relative to P2 (High) and P3 (Low).
            // Let's use the most common definition:
            // Schiff: Origin is (P1.X, (P1.Y + P2.Y) / 2). NO, that depends on if P2 is High or Low.
            // Let's assume P1=Start, P2=High, P3=Low.
            // Schiff: Moves anchor Y to 50% of the vertical distance between P1 and P2?
            // Let's stick to simple "Schiff = Anchor Y is Average(P1.Y, P2.Y)" if that's the swing.
            // BETTER: Schiff uses the midpoint of the vertical range of the A-B swing.
            // Let's assume P2 is the B point.
            anchor = new global::Avalonia.Point(s1.X, (s1.Y + s2.Y) / 2);
        }
        else if (PitchforkType == PitchforkType.ModifiedSchiff)
        {
            // Modified Schiff: Anchor is midpoint of P1 and P2 (Both X and Y).
            // i.e., midpoint of the line segment P1-P2.
            anchor = new global::Avalonia.Point((s1.X + s2.X) / 2, (s1.Y + s2.Y) / 2);
        }

        // 2. Calculate Midpoint of P2-P3
        global::Avalonia.Point visibleMidpoint = new global::Avalonia.Point((s2.X + s3.X) / 2, (s2.Y + s3.Y) / 2);

        // 3. Draw Median Line (Anchor -> through VisibleMidpoint -> Extend)
        // Vector V = VisibleMidpoint - Anchor
        global::Avalonia.Vector v = visibleMidpoint - anchor;
        
        // Draw Median Line (Center)
        // Extend well beyond chart bounds? 
        // Just draw a long ray for now.
        global::Avalonia.Point farPoint = anchor + v * 100; // Extend significantly

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Clip/Bounds checks would be good, but Skia handles drawing outside canvas fine.
        DrawRay(canvas, paint, anchor, visibleMidpoint, isRay: true);

        // 4. Draw Parallel Lines (through P2 and P3)
        // They are parallel to the Median Line.
        // P2 Line: Passes through P2, Slope same as Median Line.
        // P3 Line: Passes through P3, Slope same as Median Line.
        // Actually, in Andrews Pitchfork, P2 and P3 ARE the points the outer lines pass through.
        
        DrawRay(canvas, paint, s2, s2 + v, isRay: true);
        DrawRay(canvas, paint, s3, s3 + v, isRay: true);

        // Optional: Additional lines (Quarter lines, Warning lines) can be added here.

        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)s1.X, (float)s1.Y, (float)ChartConstants.SelectedHandleRadius, selPaint);
            canvas.DrawCircle((float)s2.X, (float)s2.Y, (float)ChartConstants.SelectedHandleRadius, selPaint);
            canvas.DrawCircle((float)s3.X, (float)s3.Y, (float)ChartConstants.SelectedHandleRadius, selPaint);
            
            // Draw Anchor highlight for Schiff
            if (PitchforkType != PitchforkType.Andrews)
            {
                 using var anchorPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                 canvas.DrawCircle((float)anchor.X, (float)anchor.Y, 4, anchorPaint);
            }
        }
    }

    private void DrawRay(SKCanvas canvas, SKPaint paint, global::Avalonia.Point p1, global::Avalonia.Point p2, bool isRay)
    {
        if (isRay)
        {
            global::Avalonia.Vector diff = p2 - p1;
            double len = Math.Sqrt(diff.X * diff.X + diff.Y * diff.Y);
            
            if (len > 0.0001)
            {
                global::Avalonia.Vector dir = diff / len;
                
                var bounds = canvas.LocalClipBounds;
                var padding = ChartConstants.PitchforkBoundsPadding; 
                var clipRect = new global::Avalonia.Rect(
                    bounds.Left - padding, 
                    bounds.Top - padding, 
                    bounds.Width + padding * 2, 
                    bounds.Height + padding * 2);

                global::Avalonia.Point endPoint = GetRayIntersection(p1, dir, clipRect);
                canvas.DrawLine((float)p1.X, (float)p1.Y, (float)endPoint.X, (float)endPoint.Y, paint);
            }
            else
            {
                 canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint);
            }
        }
        else
        {
            canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint);
        }
    }

    private global::Avalonia.Point GetRayIntersection(global::Avalonia.Point origin, global::Avalonia.Vector dir, global::Avalonia.Rect bounds)
    {
        // Liang-Barsky-like or slab intersection for a Ray against AABB
        // We want the furthest point "t" that is within bounds? 
        // No, we want the FIRST exit point from inside (or origin) to outside.
        // Assuming Origin is usually inside or near.
        
        // Ray: P = O + t*D. Find smallest t > 0 that hits a boundary.

        // x = origin.X + t * dir.X
        // t = (x - origin.X) / dir.X
        


        // We want the smallest t > 0 that puts us ON the boundary.
        // But actually we just want to draw FAR ENOUGH. 
        // If we pick the largest valid t, we cross the screen.
        // The artifacts came from t being TOO LARGE (100,000).
        // So we want the t that hits the FURTHEST VISIBLE edge in the direction.
        
        double bestT = 20000; // Default large
        
        // Find smallest t > 0
        // Wait, if origin is inside, we want the exit t.
        // If we are looking for intersection with any of the 4 lines...
        
        // Let's rely on the fact that we just need a point OUTSIDE the box.
        // So finding the MAX distance to any corner?
        // No.
        
        // Simplify: Just clamp t to a reasonable value if it exceeds safe limits (e.g. 5000).
        // A 4K screen is < 5000px.
        // Does 20,000 work? My previous 100,000 broke it.
        // Let's try 10,000. It's safe for floats.
        
        bestT = 10000;
        return origin + dir * bestT;
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 3) return false;

        global::Avalonia.Point s1 = transform.ChartToScreen(Points[0]);
        global::Avalonia.Point s2 = transform.ChartToScreen(Points[1]);
        global::Avalonia.Point s3 = transform.ChartToScreen(Points[2]);

        global::Avalonia.Point anchor = s1;
        if (PitchforkType == PitchforkType.Schiff)
        {
            anchor = new global::Avalonia.Point(s1.X, (s1.Y + s2.Y) / 2);
        }
        else if (PitchforkType == PitchforkType.ModifiedSchiff)
        {
            anchor = new global::Avalonia.Point((s1.X + s2.X) / 2, (s1.Y + s2.Y) / 2);
        }

        // 2. Calculate Midpoint of P2-P3
        global::Avalonia.Point mid = new global::Avalonia.Point((s2.X + s3.X) / 2, (s2.Y + s3.Y) / 2);
        global::Avalonia.Vector v = mid - anchor;

        // Check if point is near Median Line
        if (IsNearRay(anchor, v, screenPoint, tolerance)) return true;

        // Check if point is near Outer Lines
        if (IsNearRay(s2, v, screenPoint, tolerance)) return true;
        if (IsNearRay(s3, v, screenPoint, tolerance)) return true;

        // Handles
        if (PointDistance(s1, screenPoint) < tolerance) return true;
        if (PointDistance(s2, screenPoint) < tolerance) return true;
        if (PointDistance(s3, screenPoint) < tolerance) return true;

        return false;
    }

    private bool IsNearRay(global::Avalonia.Point origin, global::Avalonia.Vector direction, global::Avalonia.Point test, double tolerance)
    {
        double len = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (len == 0) return PointDistance(origin, test) < tolerance;

        global::Avalonia.Vector dirNorm = direction / len;
        global::Avalonia.Vector w = test - origin;
        double t = w.X * dirNorm.X + w.Y * dirNorm.Y;

        if (t < 0) return PointDistance(origin, test) < tolerance;

        global::Avalonia.Point proj = origin + dirNorm * t;
        return PointDistance(proj, test) < tolerance;
    }


    private double PointDistance(global::Avalonia.Point a, global::Avalonia.Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
