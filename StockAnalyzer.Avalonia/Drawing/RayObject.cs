using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public class RayObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.Ray;

    public RayObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        var vX = p2.X - p1.X;
        var vY = p2.Y - p1.Y;

        var bounds = canvas.LocalClipBounds;
        float extension = Math.Max(bounds.Width, bounds.Height) * 2;
        
        double len = Math.Sqrt(vX * vX + vY * vY);
        if (len == 0) return;

        double exX = p1.X + (vX / len) * extension;
        double exY = p1.Y + (vY / len) * extension;

        // Draw Ray using base _cachedPaint
        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)exX, (float)exY, _cachedPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double vX = p2.X - p1.X;
        double vY = p2.Y - p1.Y;
        double vLen = Math.Sqrt(vX * vX + vY * vY);
        
        if (vLen == 0) return false;

        double wX = screenPoint.X - p1.X;
        double wY = screenPoint.Y - p1.Y;
        
        double dot = wX * vX + wY * vY;
        if (dot < 0) return false;
        
        double det = vX * wY - vY * wX;
        double dist = Math.Abs(det) / vLen;
        
        return dist <= tolerance;
    }
}
