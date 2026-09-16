using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

public class RayObject : RelativeGeometricRenderer, IDrawingCalculatedValuesProvider
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

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < 2) return Array.Empty<DrawingCalculatedValue>();

        var p1 = Points[0];
        var p2 = Points[1];
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        decimal rayPrice = DrawingMath.InterpolatePrice(p1, p2, timestamp);

        var values = new List<DrawingCalculatedValue>();

        string rayPriceText = $"{rayPrice:F3}";
        if (currentPrice.HasValue)
        {
            decimal diff = currentPrice.Value - rayPrice;
            rayPriceText += $" ({diff:+0.000;-0.000;0.000})";
        }
        values.Add(new DrawingCalculatedValue("RayPrice", "Ray Price", rayPrice, rayPriceText, color));

        var slope = DrawingMath.CalculateSlopePerDay(p1, p2);
        if (slope.HasValue)
        {
            values.Add(new DrawingCalculatedValue("Slope", "Slope", slope.Value, $"{slope.Value:+0.000;-0.000;0.000} / day", IndicatorColor.Gray));
        }

        return values;
    }
}

