using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Chart drawing object representing a sequential numbered badge/stamp (e.g. ①, ②, ③...).
/// Anchored by Points[0]. Provides auto-incrementing step tracking for trade scenario documentation.
/// Zero-allocation enabled via cached SKPaint instances.
/// </summary>
public class StepBadgeObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.StepBadge;

    private int _stepNumber = 1;

    [Category("Badge")]
    [DisplayName("Step Number")]
    [Range(1, 999)]
    [ParameterTag(DrawingParameterTags.Common)]
    public int StepNumber
    {
        get => _stepNumber;
        set => _stepNumber = Math.Clamp(value, 1, 999);
    }

    private double _badgeRadius = 14.0;

    [Category("Badge")]
    [DisplayName("Badge Radius")]
    [Range(4.0, 100.0)]
    [ParameterTag(DrawingParameterTags.Common)]
    public double BadgeRadius
    {
        get => _badgeRadius;
        set => _badgeRadius = Math.Max(4.0, value);
    }

    private Color _textColor = Colors.White;

    [Category("Badge")]
    [DisplayName("Text Color")]
    [ParameterTag(DrawingParameterTags.Common)]
    public Color TextColor
    {
        get => _textColor;
        set => _textColor = value;
    }

    private readonly SKPaint _bgPaint;
    private readonly SKPaint _textPaint;

    public StepBadgeObject() : base()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = 1.0;

        _bgPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _textPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = DrawingThemeContext.DefaultTextTypeface
        };
    }

    public StepBadgeObject(ChartPoint position) : this()
    {
        Points.Add(position);
    }

    public StepBadgeObject(ChartPoint position, int stepNumber) : this(position)
    {
        StepNumber = stepNumber;
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count == 0) return;

        var center = transform.ChartToScreen(Points[0]);
        if (float.IsNaN((float)center.X) || float.IsNaN((float)center.Y) ||
            float.IsInfinity((float)center.X) || float.IsInfinity((float)center.Y)) return;

        float radius = (float)Math.Max(4.0, BadgeRadius);

        // 1. Draw Background Circle Fill
        _bgPaint.Color = SkiaColor;
        canvas.DrawCircle((float)center.X, (float)center.Y, radius, _bgPaint);

        // 2. Draw Border Stroke if Thickness > 0
        if (Thickness > 0)
        {
            _cachedPaint.Style = SKPaintStyle.Stroke;
            _cachedPaint.StrokeWidth = (float)Thickness;
            _cachedPaint.Color = SkiaColor;
            canvas.DrawCircle((float)center.X, (float)center.Y, radius, _cachedPaint);
        }

        // 3. Render Centered Glyph
        string text;
        if (_stepNumber >= 1 && _stepNumber <= 20)
        {
            text = char.ConvertFromUtf32(0x2460 + (_stepNumber - 1));
        }
        else
        {
            text = _stepNumber.ToString();
        }

        _textPaint.Color = new SKColor(TextColor.R, TextColor.G, TextColor.B, TextColor.A);

        float fontSize;
        if (_stepNumber <= 20)
        {
            fontSize = radius * 1.4f;
        }
        else if (text.Length <= 2)
        {
            fontSize = radius * 1.1f;
        }
        else
        {
            fontSize = radius * 0.75f;
        }

        _textPaint.TextSize = fontSize;

        var textBounds = new SKRect();
        _textPaint.MeasureText(text, ref textBounds);
        float textX = (float)center.X - textBounds.MidX;
        float textY = (float)center.Y - textBounds.MidY;

        canvas.DrawText(text, textX, textY, _textPaint);
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0 || !IsVisible) return false;

        var center = transform.ChartToScreen(Points[0]);
        double dx = screenPoint.X - center.X;
        double dy = screenPoint.Y - center.Y;
        double effectiveRadius = BadgeRadius + tolerance;

        return (dx * dx + dy * dy) <= (effectiveRadius * effectiveRadius);
    }

    public override void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (!IsVisible) return;

        DrawGeometry(canvas, transform);

        if (IsSelected && Points.Count > 0)
        {
            var center = transform.ChartToScreen(Points[0]);
            SelectionHandleRenderer.Draw(canvas, center, DrawingThemeContext.AnchorPointColor);
        }
    }

    public override void Dispose()
    {
        _bgPaint.Dispose();
        _textPaint.Dispose();
        base.Dispose();
    }
}
