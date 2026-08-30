using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 引出線付き吹き出し描画オブジェクト (UserEditableText)。
/// アンカー点と本文ボックス間の破線引出線を描画し、複数行テキストおよび水平アライメントをサポートします。
/// </summary>
public class CalloutObject : IChartObject, IDisposable, ITextAnnotatedObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Callout;
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
            _linePaint.StrokeWidth = (float)value;
        }
    }

    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private string _text = "Callout";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value ?? string.Empty;
            InvalidateCache();
        }
    }

    private double _fontSize = DrawingThemeContext.DrawingFontSize;
    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < 1e-6) return;
            _fontSize = value;
            _textPaint.TextSize = (float)value;
            InvalidateCache();
        }
    }

    private TextHorizontalAlignment _alignment = TextHorizontalAlignment.Left;
    public TextHorizontalAlignment Alignment
    {
        get => _alignment;
        set
        {
            if (_alignment == value) return;
            _alignment = value;
            InvalidateCache();
        }
    }

    private bool _showBackgroundBox = true;
    public bool ShowBackgroundBox
    {
        get => _showBackgroundBox;
        set => _showBackgroundBox = value;
    }

    private Color _backgroundColor = Color.FromArgb(220, DrawingThemeContext.AppBackgroundColor.R, DrawingThemeContext.AppBackgroundColor.G, DrawingThemeContext.AppBackgroundColor.B);
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

    private float _backgroundPadding = ChartConstants.DefaultTextBackgroundPadding;
    public float BackgroundPadding
    {
        get => _backgroundPadding;
        set
        {
            if (Math.Abs(_backgroundPadding - value) < 1e-6f) return;
            _backgroundPadding = value;
            InvalidateCache();
        }
    }

    private float _cornerRadius = ChartConstants.DefaultDrawingCornerRadius;
    public float CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = value;
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    // Pre-allocated cached paints
    private readonly SKPaint _linePaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPathEffect _dashEffect;
    private bool _disposed;

    // Layout cache
    private string[]? _cachedLines;
    private SKSize? _cachedBlockSize;

    public CalloutObject(ChartPoint anchor, ChartPoint body)
    {
        Points.Add(anchor);
        Points.Add(body);
        _color = DrawingThemeContext.DefaultColor;
        _fontSize = DrawingThemeContext.DrawingFontSize;

        _dashEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0);

        _linePaint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)_thickness,
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
            StrokeWidth = (float)_thickness,
            IsAntialias = true
        };
    }

    private string[] GetLines() => _cachedLines ??= MultilineTextRenderer.SplitLines(_text);

    private SKSize GetBlockSize() => _cachedBlockSize ??= MultilineTextRenderer.MeasureBlock(_textPaint, GetLines());

    public void InvalidateCache()
    {
        _cachedLines = null;
        _cachedBlockSize = null;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2 || canvas == null || transform == null || _disposed) return;

        var p1 = transform.ChartToScreen(Points[0]); // Anchor
        var p2 = transform.ChartToScreen(Points[1]); // Body (Text Box)

        var lines = GetLines();
        var blockSize = GetBlockSize();

        float w = blockSize.Width + _backgroundPadding * 2;
        float h = blockSize.Height + _backgroundPadding * 2;

        float x = (float)p2.X;
        float y = (float)p2.Y;

        SKRect rect = new SKRect(x - w / 2f, y - h / 2f, x + w / 2f, y + h / 2f);

        // Leader Line: Calculate intersection with bounding box
        float p0X = (float)p1.X;
        float p0Y = (float)p1.Y;
        bool p0Inside = p0X >= rect.Left && p0X <= rect.Right && p0Y >= rect.Top && p0Y <= rect.Bottom;

        if (!p0Inside)
        {
            float dx = p0X - x;
            float dy = p0Y - y;
            float tx = MathF.Abs(dx) > 1e-6f ? (w / 2f) / MathF.Abs(dx) : float.MaxValue;
            float ty = MathF.Abs(dy) > 1e-6f ? (h / 2f) / MathF.Abs(dy) : float.MaxValue;
            float t = MathF.Min(tx, ty);

            float intersectX = x + dx * t;
            float intersectY = y + dy * t;

            canvas.DrawLine(p0X, p0Y, intersectX, intersectY, _linePaint);
        }

        if (_showBackgroundBox)
        {
            canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _bgPaint);
        }

        canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _borderPaint);

        float textLeftX = rect.Left + _backgroundPadding;
        MultilineTextRenderer.DrawBlock(canvas, lines, textLeftX, y, _textPaint, Alignment, blockSize.Width);

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2 || _disposed) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        if (Distance(screenPoint, p1) <= tolerance * 2) return true;

        var blockSize = GetBlockSize();
        float w = blockSize.Width + _backgroundPadding * 2;
        float h = blockSize.Height + _backgroundPadding * 2;

        float x = (float)p2.X;
        float y = (float)p2.Y;

        var rect = new global::Avalonia.Rect(x - w / 2f, y - h / 2f, w, h);
        if (rect.Contains(screenPoint)) return true;

        if (Distance(screenPoint, p2) <= tolerance * 4) return true;

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

