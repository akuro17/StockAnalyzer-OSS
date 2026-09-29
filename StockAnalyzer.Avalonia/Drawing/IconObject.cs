using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Chart drawing object representing an icon/stamp placed on the chart.
/// Anchored by an invisible control point at Points[0] (center of the icon).
/// Supports .ico, .icns, .png, .svg with per-icon Font Color, Font Size, and Font Opacity.
/// Optimized for Zero-Allocation 60fps rendering via cached SKBitmap and SKPaint.
/// </summary>
public class IconObject : IChartObject, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Icon;

    public string? CustomName { get; set; }

    public List<ChartPoint> Points { get; private set; }

    private string _iconPath = string.Empty;

    [Category("Style")]
    [DisplayName("Icon Path")]
    [ParameterTag(DrawingParameterTags.Common)]
    public string IconPath
    {
        get => _iconPath;
        set
        {
            if (_iconPath == value) return;
            _iconPath = value ?? string.Empty;
            _cachedBitmap = null;
        }
    }

    private Color _fontColor = DrawingThemeContext.MainTextColor;

    [Category("Style")]
    [DisplayName("Font Color")]
    [ParameterTag(DrawingParameterTags.Common)]
    public Color FontColor
    {
        get => _fontColor;
        set
        {
            if (_fontColor == value) return;
            _fontColor = value;
            UpdatePaint();
        }
    }

    private double _fontSize = DrawingThemeContext.DrawingIconFontSize;

    [Category("Style")]
    [DisplayName("Font Size")]
    [Range(8.0, 500.0)]
    [ParameterTag(DrawingParameterTags.Common)]
    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < 1e-6) return;
            _fontSize = Math.Max(4.0, value);
        }
    }

    private double _fontOpacity = 100.0;

    [Category("Style")]
    [DisplayName("Font Opacity")]
    [Range(0.0, 100.0)]
    [ParameterTag(DrawingParameterTags.Common)]
    public double FontOpacity
    {
        get => _fontOpacity;
        set
        {
            if (Math.Abs(_fontOpacity - value) < 1e-6) return;
            _fontOpacity = Math.Clamp(value, 0.0, 100.0);
            UpdatePaint();
        }
    }

    // IChartObject interface conformance
    [Category("Style")]
    [DisplayName("Color")]
    [ParameterTag(DrawingParameterTags.Common)]
    public Color Color
    {
        get => FontColor;
        set => FontColor = value;
    }

    [Category("Style")]
    [DisplayName("Thickness")]
    [Range(0.1, 10.0)]
    [ParameterTag(DrawingParameterTags.Common)]
    public double Thickness { get; set; } = 1.0;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected { get; set; }

    [Category("Style")]
    [DisplayName("Visible")]
    [ParameterTag(DrawingParameterTags.Common)]
    public bool IsVisible { get; set; } = true;

    [Category("Style")]
    [DisplayName("Locked")]
    [ParameterTag(DrawingParameterTags.Common)]
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;

    [Category("Style")]
    [DisplayName("Z-Index")]
    [Range(0, 100)]
    [ParameterTag(DrawingParameterTags.Common)]
    public int ZIndex { get; set; } = 0;

    [Category("Coordinates")]
    [DisplayName("Move Axis Mode")]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;

    public bool IsMoveAxisModeExplicit { get; set; } = false;

    public int AnchorPointIndex { get; set; } = 0;

    public SKColor SkiaColor => new(FontColor.R, FontColor.G, FontColor.B, (byte)Math.Clamp((int)Math.Round(FontOpacity * 2.55), 0, 255));

    // Zero-allocation pre-allocated cached paints
    private readonly SKPaint _bitmapPaint;
    private readonly SKPaint _selectedBorderPaint;
    private SKBitmap? _cachedBitmap;
    private bool _disposed;

    public IconObject() : this(new ChartPoint(DateTime.UtcNow, 0m))
    {
    }

    public IconObject(ChartPoint point, string iconPath = "", string customName = "")
    {
        Points = new List<ChartPoint> { point };
        _iconPath = iconPath ?? string.Empty;
        CustomName = !string.IsNullOrEmpty(customName) ? customName : (!string.IsNullOrEmpty(iconPath) ? System.IO.Path.GetFileNameWithoutExtension(iconPath) : "Icon");
        _fontColor = DrawingThemeContext.MainTextColor;
        _fontSize = DrawingThemeContext.DrawingIconFontSize > 0 ? DrawingThemeContext.DrawingIconFontSize : 32.0;
        _fontOpacity = 100.0;

        _bitmapPaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High
        };

        _selectedBorderPaint = new SKPaint
        {
            Color = new SKColor(74, 144, 226, 200), // Subtle blue selection indicator
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            PathEffect = SKPathEffect.CreateDash([4f, 4f], 0f),
            IsAntialias = true
        };

        UpdatePaint();
    }

    private void UpdatePaint()
    {
        byte alpha = (byte)Math.Clamp((int)Math.Round(_fontOpacity * 2.55), 0, 255);
        _bitmapPaint.Color = new SKColor(_fontColor.R, _fontColor.G, _fontColor.B, alpha);
        _bitmapPaint.ColorFilter?.Dispose();
        _bitmapPaint.ColorFilter = SKColorFilter.CreateBlendMode(
            new SKColor(_fontColor.R, _fontColor.G, _fontColor.B, alpha),
            SKBlendMode.SrcIn
        );
    }

    private SKBitmap? EnsureBitmap()
    {
        if (_cachedBitmap != null) return _cachedBitmap;
        if (string.IsNullOrEmpty(_iconPath)) return null;

        var service = DrawingThemeContext.IconDrawingService;
        _cachedBitmap = service?.GetSkiaBitmap(_iconPath);
        return _cachedBitmap;
    }

    internal void SetBitmap(SKBitmap? bitmap)
    {
        _cachedBitmap = bitmap;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count == 0 || _disposed || !IsVisible) return;

        var pt = transform.ChartToScreen(Points[0]);
        var bitmap = EnsureBitmap();

        float targetSize = (float)_fontSize;
        float w = targetSize;
        float h = targetSize;

        if (bitmap != null && bitmap.Width > 0 && bitmap.Height > 0)
        {
            float maxDim = Math.Max(bitmap.Width, bitmap.Height);
            w = targetSize * (bitmap.Width / maxDim);
            h = targetSize * (bitmap.Height / maxDim);
        }

        float x = (float)pt.X;
        float y = (float)pt.Y;
        var destRect = new SKRect(x - w / 2f, y - h / 2f, x + w / 2f, y + h / 2f);

        if (bitmap != null)
        {
            canvas.DrawBitmap(bitmap, destRect, _bitmapPaint);
        }
        else
        {
            // Fallback placeholder if image not found
            using var placeholderPaint = new SKPaint
            {
                Color = SkiaColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true
            };
            canvas.DrawRect(destRect, placeholderPaint);
        }

        // Invisible control point: SelectionHandleRenderer is NOT called for Points[0].
        // If selected, display a subtle dashed bounding outline around the icon.
        if (IsSelected)
        {
            canvas.DrawRect(destRect, _selectedBorderPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count == 0 || _disposed || !IsVisible) return false;

        var pt = transform.ChartToScreen(Points[0]);
        float targetSize = (float)_fontSize;
        float w = targetSize;
        float h = targetSize;

        var bitmap = EnsureBitmap();
        if (bitmap != null && bitmap.Width > 0 && bitmap.Height > 0)
        {
            float maxDim = Math.Max(bitmap.Width, bitmap.Height);
            w = targetSize * (bitmap.Width / maxDim);
            h = targetSize * (bitmap.Height / maxDim);
        }

        float x = (float)pt.X;
        float y = (float)pt.Y;

        var rect = new global::Avalonia.Rect(x - w / 2f - tolerance, y - h / 2f - tolerance, w + tolerance * 2, h + tolerance * 2);
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
        _bitmapPaint.ColorFilter?.Dispose();
        _bitmapPaint.Dispose();
        _selectedBorderPaint.PathEffect?.Dispose();
        _selectedBorderPaint.Dispose();
    }
}
