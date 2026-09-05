using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 自由入力複数行テキスト描画オブジェクト (UserEditableText)。
/// 60fps レンダリングホットパスにおける Zero-Allocation キャッシュ管理と
/// 水平アライメント（Left / Center / Right）をサポートします。
/// </summary>
public class TextObject : IChartObject, IDisposable, ITextAnnotatedObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Text;
    public List<ChartPoint> Points { get; private set; }

    private Color _color = DrawingThemeContext.DefaultColor;
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            _borderPaint.Color = SkiaColor;
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

    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private string _text = "Text";
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

    // Pre-allocated cached paints for Zero-Allocation 60fps rendering
    private readonly SKPaint _textPaint;
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _borderPaint;
    private bool _disposed;

    // Layout cache
    private string[]? _cachedLines;
    private SKSize? _cachedBlockSize;

    public TextObject(ChartPoint point, string text = "Text")
    {
        Points = new List<ChartPoint> { point };
        _text = text ?? string.Empty;
        _color = DrawingThemeContext.DefaultColor;
        _fontSize = DrawingThemeContext.DrawingFontSize;

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
        if (canvas == null || transform == null || Points.Count == 0 || _disposed) return;

        var pt = transform.ChartToScreen(Points[0]);
        var lines = GetLines();
        var blockSize = GetBlockSize();

        float w = blockSize.Width + _backgroundPadding * 2;
        float h = blockSize.Height + _backgroundPadding * 2;

        float x = (float)pt.X;
        float y = (float)pt.Y;

        SKRect rect = new SKRect(x - w / 2f, y - h / 2f, x + w / 2f, y + h / 2f);

        if (_showBackgroundBox)
        {
            canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _bgPaint);
        }

        canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _borderPaint);

        float textLeftX = rect.Left + _backgroundPadding;
        MultilineTextRenderer.DrawBlock(canvas, lines, textLeftX, y, _textPaint, Alignment, blockSize.Width);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0 || string.IsNullOrEmpty(Text) || _disposed) return false;
        var pt = transform.ChartToScreen(Points[0]);

        var blockSize = GetBlockSize();
        float w = blockSize.Width + _backgroundPadding * 2;
        float h = blockSize.Height + _backgroundPadding * 2;

        float x = (float)pt.X;
        float y = (float)pt.Y;

        var rect = new global::Avalonia.Rect(x - w / 2f, y - h / 2f, w, h);
        return rect.Contains(screenPoint);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            Points[0] = new ChartPoint(Points[0].Time.Add(timeDelta), Points[0].Price + priceDelta);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textPaint.Dispose();
        _bgPaint.Dispose();
        _borderPaint.Dispose();
    }
}

