using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

    public enum PolylineLabelType { None, Numeric, Alphabet }

    /// <summary>
    /// Polyline object implementation (connecting N points)
    /// </summary>
    public class PolylineObject : IChartObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Polyline;
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; }
    public double Thickness { get; set; }
    public bool IsSelected { get; set; }
    
    // New Properties for Labeling (Elliott Wave support)
    public PolylineLabelType LabelType { get; set; } = PolylineLabelType.None;
    public bool ShowLabels { get; set; } = true;
    public double FontSize { get; set; } = 12.0;

    public PolylineObject(List<ChartPoint> points)
    {
        Points = new List<ChartPoint>(points);
        Color = Colors.Green;
        Thickness = 1.0;
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void AddPoint(ChartPoint point)
    {
        Points.Add(point);
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeJoin = SKStrokeJoin.Round
        };

        var path = new SKPath();
        var start = transform.ChartToScreen(Points[0]);
        path.MoveTo((float)start.X, (float)start.Y);

        for (int i = 1; i < Points.Count; i++)
        {
            var pt = transform.ChartToScreen(Points[i]);
            path.LineTo((float)pt.X, (float)pt.Y);
        }

        canvas.DrawPath(path, paint);

        // Render Labels
        if (ShowLabels && LabelType != PolylineLabelType.None)
        {
            using var textPaint = new SKPaint
            {
                Color = SkiaColor,
                IsAntialias = true,
                TextSize = (float)FontSize,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
            };

            for (int i = 0; i < Points.Count; i++)
            {
               string label = GetLabel(i);
               if (!string.IsNullOrEmpty(label))
               {
                   var screenPt = transform.ChartToScreen(Points[i]);
                   // Slight offset
                   float offY = (i % 2 == 0) ? ChartConstants.PolylineLabelOffsetYEven : ChartConstants.PolylineLabelOffsetYOdd; 
                   canvas.DrawText(label, (float)screenPt.X - 4, (float)screenPt.Y + offY, textPaint);
               }
            }
        }

        if (IsSelected)
        {
            using var selPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            foreach (var pt in Points)
            {
                var screenPt = transform.ChartToScreen(pt);
                canvas.DrawCircle((float)screenPt.X, (float)screenPt.Y, ChartConstants.DefaultHandleRadius, selPaint);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        for (int i = 0; i < Points.Count - 1; i++)
        {
            var p1 = transform.ChartToScreen(Points[i]);
            var p2 = transform.ChartToScreen(Points[i + 1]);
            
            // Simple distance check to segment
             double dist = DistancePointToSegment(screenPoint, p1, p2);
             if (dist <= tolerance) return true;
        }
        return false;
    }

    private double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        // Avalonia Point operations
        double l2 = (w.X - v.X) * (w.X - v.X) + (w.Y - v.Y) * (w.Y - v.Y);
        if (l2 == 0) return Distance(p, v);
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        global::Avalonia.Point projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Distance(p, projection);
    }

    private double Distance(global::Avalonia.Point p1, global::Avalonia.Point p2)
    {
        return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }


    private string GetLabel(int index)
    {
         if (index == 0 && LabelType != PolylineLabelType.None) return ""; // Usually start point has no label in wave counts
         
         if (LabelType == PolylineLabelType.Numeric)
         {
             return index.ToString();
         }
         else if (LabelType == PolylineLabelType.Alphabet)
         {
             int charIndex = index - 1;
             if (charIndex >= 0 && charIndex < 26)
             {
                 return ((char)('A' + charIndex)).ToString();
             }
             return "?";
         }
         return "";
    }
}
