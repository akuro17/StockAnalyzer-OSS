using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 価格連動ラベル描画オブジェクト (PriceBoundText)。
/// アンカー点の価格に基づき "$0.00" 形式でラベルを表示し、破線引出線を描画します。
/// </summary>
public class PriceLabelObject : IChartObject, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.PriceLabel;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();

    private Color _color = DrawingThemeContext.DefaultColor;
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            _borderPaint.Color = SkiaColor;
            _linePaint.Color = SkiaColor;
        }
    }

    private double _thickness = DrawingThemeContext.DefaultStrokeThickness;
    public double Thickness
    {
        get => _thickness;
        set
        {
            if (Math.Abs(_thickness - value) < 1e-6) return;
            _thickness = value;
            _borderPaint.StrokeWidth = (float)value;
        }
    }

    public bool IsSelected { get; set; }

    private double _fontSize = DrawingThemeContext.DrawingFontSize;
    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < 1e-6) return;
            _fontSize = value;
            _textPaint.TextSize = (float)value;
        }
    }

    private bool _showBackgroundBox = true;
    public bool ShowBackgroundBox
    {
        get => _showBackgroundBox;
        set => _showBackgroundBox = value;
    }

    private Color _backgroundColor = Color.FromArgb(200, DrawingThemeContext.AppBackgroundColor.R, DrawingThemeContext.AppBackgroundColor.G, DrawingThemeContext.AppBackgroundColor.B);
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (_backgroundColor == value) return;
            _backgroundColor = value;
            _bgPaint.Color = new SKColor(value.R, value.G, value.B, value.A);
        }
    }

    private float _backgroundPadding = ChartConstants.DefaultPriceLabelPadding;
    public float BackgroundPadding
    {
        get => _backgroundPadding;
        set => _backgroundPadding = value;
    }

    private float _cornerRadius = ChartConstants.DefaultDrawingCornerRadius;
    public float CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = value;
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    private readonly SKPaint _linePaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPathEffect _dashEffect;
    private bool _disposed;

    public PriceLabelObject(ChartPoint position)
    {
        Points.Add(position); // Anchor
        Points.Add(position); // Label Display Position (Initially same)
        _fontSize = DrawingThemeContext.DrawingFontSize;
        _color = DrawingThemeContext.DefaultColor;

        _dashEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0);

        _linePaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = _dashEffect
        };

        _textPaint = new SKPaint
        {
            Color = DrawingThemeContext.MainTextSkColor,
            IsAntialias = true,
            TextSize = (float)_fontSize,
            Typeface = SKTypeface.Default
        };

        _bgPaint = new SKPaint
        {
            Color = new SKColor(_backgroundColor.R, _backgroundColor.G, _backgroundColor.B, _backgroundColor.A),
            Style = SKPaintStyle.Fill
        };

        _borderPaint = new SKPaint
        {
            Color = SkiaColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
    }

    public PriceLabelObject(ChartPoint anchor, ChartPoint labelPos) : this(anchor)
    {
        if (Points.Count > 1)
        {
            Points[1] = labelPos;
        }
        else
        {
            Points.Add(labelPos);
        }
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2 || canvas == null || transform == null || _disposed) return;

        var p1 = transform.ChartToScreen(Points[0]); // Anchor (Fixed Point)
        var p2 = transform.ChartToScreen(Points[1]); // Label Position (Movable)

        var price = Points[0].Price;
        string text = $"{price:F2}";

        SKRect textBounds = new SKRect();
        _textPaint.MeasureText(text, ref textBounds);

        float w = textBounds.Width + _backgroundPadding * 2;
        float h = textBounds.Height + _backgroundPadding * 2;

        float x = (float)p2.X;
        float y = (float)p2.Y;

        // Draw dashed line from Anchor to Label
        canvas.DrawLine((float)p1.X, (float)p1.Y, x, y, _linePaint);

        // Center label at P2
        SKRect rect = new SKRect(x - w / 2f, y - h / 2f, x + w / 2f, y + h / 2f);
        if (_showBackgroundBox)
        {
            canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _bgPaint);
        }
        canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _borderPaint);

        canvas.DrawText(text, x - textBounds.MidX, y - textBounds.MidY, _textPaint);

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2 || _disposed) return false;

        var p1 = transform.ChartToScreen(Points[0]); // Anchor
        var p2 = transform.ChartToScreen(Points[1]); // Label

        if (Distance(screenPoint, p1) <= tolerance * 2) return true;
        if (Distance(screenPoint, p2) <= tolerance * 2) return true;

        var price = Points[0].Price;
        string text = $"{price:F2}";

        SKRect textBounds = new SKRect();
        _textPaint.MeasureText(text, ref textBounds);

        float w = textBounds.Width + _backgroundPadding * 2;
        float h = textBounds.Height + _backgroundPadding * 2;
        float x = (float)p2.X;
        float y = (float)p2.Y;

        double left = x - w / 2f;
        double top = y - h / 2f;
        double right = x + w / 2f;
        double bottom = y + h / 2f;

        if (screenPoint.X >= left && screenPoint.X <= right &&
            screenPoint.Y >= top && screenPoint.Y <= bottom)
        {
            return true;
        }

        return false;
    }

    private static double Distance(global::Avalonia.Point p1, global::Avalonia.Point p2)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _linePaint.Dispose();
        _textPaint.Dispose();
        _bgPaint.Dispose();
        _borderPaint.Dispose();
        _dashEffect.Dispose();
    }
}
