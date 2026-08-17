using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Ellipse object implementation (defined by 2 diagonal bounding points) supporting fills, gradients, and blend modes.
/// </summary>
public class EllipseObject : IFillableChartObject
{
    private const float GeometryEpsilon = 1e-4f;

    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Ellipse;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }

    // Properties for Fill, Gradient, and Blending
    public bool IsFilled { get; set; } = true;
    public DrawingBlendMode BlendMode { get; set; } = DrawingBlendMode.Normal;
    public DrawingGradientType GradientType { get; set; } = DrawingGradientType.None;
    public Color? GradientEndColor { get; set; } = null;
    public byte FillAlpha { get; set; } = 30;
    public byte GradientEndAlpha { get; set; } = 30;

    public EllipseObject(ChartPoint p1, ChartPoint p2)
    {
        Points.Add(p1);
        Points.Add(p2);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        if (float.IsNaN((float)p1.X) || float.IsNaN((float)p1.Y) ||
            float.IsNaN((float)p2.X) || float.IsNaN((float)p2.Y) ||
            float.IsInfinity((float)p1.X) || float.IsInfinity((float)p1.Y) ||
            float.IsInfinity((float)p2.X) || float.IsInfinity((float)p2.Y)) return;

        // Calculate Bounding Box
        var left = (float)Math.Min(p1.X, p2.X);
        var top = (float)Math.Min(p1.Y, p2.Y);
        var right = (float)Math.Max(p1.X, p2.X);
        var bottom = (float)Math.Max(p1.Y, p2.Y);
        var rect = new SKRect(left, top, right, bottom);

        bool isDegenerate = rect.Width <= GeometryEpsilon || rect.Height <= GeometryEpsilon;

        // [Layer 1: Fill Layer]
        if (IsFilled && !isDegenerate)
        {
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                BlendMode = BlendMode.ToSkBlendMode()
            };

            if (GradientType == DrawingGradientType.None)
            {
                fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                canvas.DrawOval(rect, fillPaint);
            }
            else
            {
                using var shader = DrawingShaderFactory.CreateShader(this, rect, (float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y);
                if (shader != null)
                {
                    fillPaint.Shader = shader;
                    canvas.DrawOval(rect, fillPaint);
                }
                else
                {
                    fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                    canvas.DrawOval(rect, fillPaint);
                }
            }
        }

        // [Layer 2: Stroke Layer]
        if (Thickness > 0)
        {
            using var strokePaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                BlendMode = SKBlendMode.SrcOver,
                IsAntialias = true
            };
            canvas.DrawOval(rect, strokePaint);
        }

        // [Layer 3: Selection Handles]
        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1);
            SelectionHandleRenderer.Draw(canvas, p2);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        // Ellipse Equation: (x-h)^2/a^2 + (y-k)^2/b^2 <= 1
        double h = (p1.X + p2.X) / 2.0;
        double k = (p1.Y + p2.Y) / 2.0;
        double a = Math.Abs(p1.X - p2.X) / 2.0;
        double b = Math.Abs(p1.Y - p2.Y) / 2.0;

        if (a <= GeometryEpsilon || b <= GeometryEpsilon) return false; // Degenerate

        double normalizedX = (screenPoint.X - h) / a;
        double normalizedY = (screenPoint.Y - k) / b;
        double distSq = normalizedX * normalizedX + normalizedY * normalizedY;

        if (IsFilled)
        {
            if (distSq <= 1.0) return true;
        }

        // Check if close to boundary within tolerance
        double minR = Math.Min(a, b);
        if (minR < 1.0) minR = 1.0;

        double dist = Math.Sqrt(distSq);
        double diff = Math.Abs(dist - 1.0);
        double pixelDiff = diff * minR;

        return pixelDiff <= tolerance * 2.0;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }
}
