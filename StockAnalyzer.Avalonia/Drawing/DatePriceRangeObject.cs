using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Two-point Date/Price Range measurement drawing tool.
/// Displays a translucent bounding rectangle, directional center arrows, and a configurable multi-line metrics box.
/// </summary>
public class DatePriceRangeObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    private bool _showPriceLabel = true;
    private bool _showBarLabel = true;
    private bool _showVolumeLabel = true;

    private IReadOnlyList<CoreCandleData>? _lastCandles;
    private DrawingAnchor _cachedP1;
    private DrawingAnchor _cachedP2;
    private IndicatorColor _cachedColor;
    private bool _hasCalculated;
    private DrawingCalculatedValue[] _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private string[] _cachedLabelLines = Array.Empty<string>();
    private bool _labelLinesDirty = true;
    private readonly NativeResourceGate _gate = new();

    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _borderPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _arrowLinePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _arrowHeadPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPath _arrowPath = new();
    private readonly SKPaint _labelBgPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _labelBorderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
    private readonly SKPaint _labelTextPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    public Guid Id { get; set; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.DatePriceRange;
    public string? CustomName { get; set; }
    public List<ChartPoint> Points { get; set; } = new();

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;

    [Category("Style")]
    [DisplayName("Stroke Style")]
    [ParameterTag(DrawingParameterTags.Common)]
    public DrawingStrokeStyle StrokeStyle { get; set; } = DrawingStrokeStyle.Solid;

    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public int ZIndex { get; set; }
    public int Priority { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; }
    public int AnchorPointIndex { get; set; }
    public int PanelIndex { get; set; } = -1;

    [Category("Analysis")]
    [DisplayName("Show Price Label")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowPriceLabel
    {
        get => _showPriceLabel;
        set
        {
            if (_showPriceLabel != value)
            {
                _showPriceLabel = value;
                _labelLinesDirty = true;
            }
        }
    }

    [Category("Analysis")]
    [DisplayName("Show Bar Label")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowBarLabel
    {
        get => _showBarLabel;
        set
        {
            if (_showBarLabel != value)
            {
                _showBarLabel = value;
                _labelLinesDirty = true;
            }
        }
    }

    [Category("Analysis")]
    [DisplayName("Show Volume Label")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowVolumeLabel
    {
        get => _showVolumeLabel;
        set
        {
            if (_showVolumeLabel != value)
            {
                _showVolumeLabel = value;
                _labelLinesDirty = true;
            }
        }
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public DatePriceRangeObject()
    {
    }

    public DatePriceRangeObject(ChartPoint start, ChartPoint end)
    {
        Points.Add(start);
        Points.Add(end);
    }

    /// <summary>
    /// Recomputes range statistics against <paramref name="candles"/> and refreshes the cached label lines.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles)
    {
        _lastCandles = candles;
        if (Points.Count < 2)
        {
            _hasCalculated = false;
            _cachedValues = Array.Empty<DrawingCalculatedValue>();
            _cachedLabelLines = Array.Empty<string>();
            _labelLinesDirty = false;
            return;
        }

        ComputeFromCurrentState();
        RebuildLabelLines();
        _labelLinesDirty = false;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || canvas == null || transform == null || Points.Count < 2)
            {
                return;
            }

            EnsureCalculatedAndLabelLines();

            global::Avalonia.Point pt1 = transform.ChartToScreen(Points[0]);
            global::Avalonia.Point pt2 = transform.ChartToScreen(Points[1]);

            float x1 = (float)pt1.X;
            float y1 = (float)pt1.Y;
            float x2 = (float)pt2.X;
            float y2 = (float)pt2.Y;

            float left = MathF.Min(x1, x2);
            float top = MathF.Min(y1, y2);
            float right = MathF.Max(x1, x2);
            float bottom = MathF.Max(y1, y2);
            var rect = new SKRect(left, top, right, bottom);

            SKColor baseColor = SkiaColor;

            // 1. Translucent rectangle fill + border
            _fillPaint.Color = baseColor.WithAlpha(ChartConstants.DefaultShapeFillAlpha);
            canvas.DrawRect(rect, _fillPaint);

            _borderPaint.Color = baseColor;
            _borderPaint.StrokeWidth = (float)Thickness;
            _borderPaint.PathEffect = StrokeStyle.ToSkPathEffect();
            canvas.DrawRect(rect, _borderPaint);

            // 2. Center directional arrows
            _arrowLinePaint.Color = baseColor;
            _arrowLinePaint.StrokeWidth = (float)Thickness;
            _arrowHeadPaint.Color = baseColor;

            if (MathF.Abs(y2 - y1) >= 1f)
            {
                float centerX = (left + right) * 0.5f;
                var vTail = new SKPoint(centerX, y1);
                var vTip = new SKPoint(centerX, y2);
                canvas.DrawLine(vTail, vTip, _arrowLinePaint);
                DrawingArrowHeadRenderer.DrawFilledHead(canvas, vTail, vTip, _arrowPath, _arrowHeadPaint);
            }

            if (MathF.Abs(x2 - x1) >= 1f)
            {
                float centerY = (top + bottom) * 0.5f;
                var hTail = new SKPoint(x1, centerY);
                var hTip = new SKPoint(x2, centerY);
                canvas.DrawLine(hTail, hTip, _arrowLinePaint);
                DrawingArrowHeadRenderer.DrawFilledHead(canvas, hTail, hTip, _arrowPath, _arrowHeadPaint);
            }

            // 3. Multiline summary label box
            if (_cachedLabelLines.Length > 0)
            {
                _labelBgPaint.Color = DrawingThemeContext.ChartBackground;
                _labelBorderPaint.Color = baseColor;
                _labelTextPaint.Color = DrawingThemeContext.MainTextSkColor;
                _labelTextPaint.TextSize = DrawingThemeContext.DetailFontSize > 0f
                    ? DrawingThemeContext.DetailFontSize
                    : DrawingThemeContext.DrawingFontSize;
                _labelTextPaint.Typeface = DrawingThemeContext.DefaultTextTypeface;

                bool placeAbove = Points[1].Price >= Points[0].Price;
                float boxCenterX = (left + right) * 0.5f;
                float anchorY = placeAbove
                    ? (rect.Top - ChartConstants.DefaultPriceLabelPadding)
                    : (rect.Bottom + ChartConstants.DefaultPriceLabelPadding);

                DrawingLabelBoxRenderer.DrawCenteredBox(
                    canvas,
                    boxCenterX,
                    anchorY,
                    placeAbove,
                    _cachedLabelLines,
                    _labelBgPaint,
                    _labelBorderPaint,
                    _labelTextPaint);
            }

            // 4. Selection handles
            if (IsSelected)
            {
                SelectionHandleRenderer.Draw(canvas, pt1, baseColor);
                SelectionHandleRenderer.Draw(canvas, pt2, baseColor);
            }
        }
    }

    public bool HitTest(
        global::Avalonia.Point screenPoint,
        ICoordinateTransform transform,
        double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (_gate.IsDisposed || !IsVisible || transform == null || Points.Count < 2)
        {
            return false;
        }

        global::Avalonia.Point pt1 = transform.ChartToScreen(Points[0]);
        global::Avalonia.Point pt2 = transform.ChartToScreen(Points[1]);

        double minX = Math.Min(pt1.X, pt2.X);
        double maxX = Math.Max(pt1.X, pt2.X);
        double minY = Math.Min(pt1.Y, pt2.Y);
        double maxY = Math.Max(pt1.Y, pt2.Y);

        double clampedX = Math.Clamp(screenPoint.X, minX, maxX);
        double clampedY = Math.Clamp(screenPoint.Y, minY, maxY);
        double dx = screenPoint.X - clampedX;
        double dy = screenPoint.Y - clampedY;

        return ((dx * dx) + (dy * dy)) <= (tolerance * tolerance);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            ChartPoint p = Points[i];
            Points[i] = new ChartPoint(p.Time.Add(timeDelta), p.Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < 2)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        EnsureCalculated();
        return _cachedValues;
    }

    private bool EnsureCalculated()
    {
        DrawingAnchor p1 = Points[0].ToAnchor();
        DrawingAnchor p2 = Points[1].ToAnchor();
        var indColor = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        if (!_hasCalculated || p1 != _cachedP1 || p2 != _cachedP2 || indColor != _cachedColor)
        {
            ComputeFromAnchors(p1, p2, indColor);
            _labelLinesDirty = true;
            return true;
        }

        return false;
    }

    private void EnsureCalculatedAndLabelLines()
    {
        EnsureCalculated();
        if (_labelLinesDirty)
        {
            RebuildLabelLines();
            _labelLinesDirty = false;
        }
    }

    private void ComputeFromCurrentState()
    {
        ComputeFromAnchors(
            Points[0].ToAnchor(),
            Points[1].ToAnchor(),
            new IndicatorColor(Color.A, Color.R, Color.G, Color.B));
    }

    private void ComputeFromAnchors(DrawingAnchor p1, DrawingAnchor p2, IndicatorColor indColor)
    {
        _cachedValues = DatePriceRangeValues.Calculate(p1, p2, _lastCandles, indColor);
        _cachedP1 = p1;
        _cachedP2 = p2;
        _cachedColor = indColor;
        _hasCalculated = true;
    }

    private void RebuildLabelLines()
    {
        if (_cachedValues.Length < 6)
        {
            _cachedLabelLines = Array.Empty<string>();
            return;
        }

        int lineCount = (_showPriceLabel ? 1 : 0) + (_showBarLabel ? 1 : 0) + (_showVolumeLabel ? 1 : 0);
        if (lineCount == 0)
        {
            _cachedLabelLines = Array.Empty<string>();
            return;
        }

        var lines = new string[lineCount];
        int index = 0;

        if (_showPriceLabel)
        {
            string deltaText = _cachedValues[0].FormattedText;
            lines[index++] = _cachedValues[1].NumericValue.HasValue
                ? $"{deltaText} ({_cachedValues[1].FormattedText})"
                : deltaText;
        }

        if (_showBarLabel)
        {
            lines[index++] = $"{_cachedValues[2].FormattedText} bars, {_cachedValues[3].FormattedText}d";
        }

        if (_showVolumeLabel)
        {
            lines[index++] = $"Vol: {_cachedValues[4].FormattedText} (Avg: {_cachedValues[5].FormattedText})";
        }

        _cachedLabelLines = lines;
    }

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed())
            {
                return;
            }

            _fillPaint.Dispose();
            _borderPaint.Dispose();
            _arrowLinePaint.Dispose();
            _arrowHeadPaint.Dispose();
            _arrowPath.Dispose();
            _labelBgPaint.Dispose();
            _labelBorderPaint.Dispose();
            _labelTextPaint.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
