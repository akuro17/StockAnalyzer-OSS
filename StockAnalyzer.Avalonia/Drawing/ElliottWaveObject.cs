using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Manual Elliott Wave drawing tool (Impulse 12345, Correction ABC, Triangle ABCDE).
/// Draws a polyline connecting placed wave pivots up to the active pattern mode's point count,
/// renders vertex wave labels, highlights Impulse rule violations, and preserves extra points across mode switches (U-16=A).
/// </summary>
public class ElliottWaveObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    private const int MaxActivePivotCount = 6;
    private static readonly string[] ImpulseLabels = { "0", "(1)", "(2)", "(3)", "(4)", "(5)" };
    private static readonly string[] CorrectionLabels = { "0", "(A)", "(B)", "(C)" };
    private static readonly string[] TriangleLabels = { "0", "(A)", "(B)", "(C)", "(D)", "(E)" };
    private static readonly SKColor RuleViolationColor = new(Colors.Red.R, Colors.Red.G, Colors.Red.B, Colors.Red.A);

    private readonly SKPaint _linePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true
    };

    private readonly SKPaint _violationPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true,
        Color = RuleViolationColor
    };

    private readonly SKPaint _textPaint = new()
    {
        Style = SKPaintStyle.Fill,
        IsAntialias = true
    };

    private readonly SKPath _linePath = new();
    private readonly SKPath _violationPath = new();
    private readonly decimal[] _cachedPrices = new decimal[MaxActivePivotCount];
    private DrawingCalculatedValue[] _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private int _cachedActiveCount = -1;
    private WavePatternMode _cachedPatternMode;
    private IndicatorColor _cachedIndColor;
    private bool _hasCalculated;
    private readonly NativeResourceGate _gate = new();

    public Guid Id { get; set; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.ElliottWave;
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
    [DisplayName("Pattern Mode")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public WavePatternMode PatternMode { get; set; } = WavePatternMode.Impulse12345;

    [Category("Analysis")]
    [DisplayName("Show Rule Validation")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowRuleValidation { get; set; } = true;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    /// <summary>Maximum number of displayed/active pivots for the current <see cref="PatternMode"/>.</summary>
    public int RequiredPointCount => GetRequiredPointCount(PatternMode);

    /// <summary>Number of points currently rendered, hit-tested, and exposed as handles under <see cref="PatternMode"/>.</summary>
    public int ActivePointCount => Math.Min(Points.Count, RequiredPointCount);

    public ElliottWaveObject()
    {
    }

    public ElliottWaveObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    public void AddPoint(ChartPoint point)
    {
        Points.Add(point);
    }

    public static int GetRequiredPointCount(WavePatternMode mode) => mode switch
    {
        WavePatternMode.Impulse12345 => ImpulseLabels.Length,
        WavePatternMode.CorrectionABC => CorrectionLabels.Length,
        WavePatternMode.TriangleABCDE => TriangleLabels.Length,
        _ => ImpulseLabels.Length
    };

    public static string? GetLabel(WavePatternMode mode, int index)
    {
        string[] table = mode switch
        {
            WavePatternMode.Impulse12345 => ImpulseLabels,
            WavePatternMode.CorrectionABC => CorrectionLabels,
            WavePatternMode.TriangleABCDE => TriangleLabels,
            _ => ImpulseLabels
        };

        return (uint)index < (uint)table.Length ? table[index] : null;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || canvas == null || transform == null)
            {
                return;
            }

            RenderLocked(canvas, transform);
        }
    }

    private void RenderLocked(SKCanvas canvas, ICoordinateTransform transform)
    {
        int activeCount = ActivePointCount;
        if (activeCount == 0)
        {
            return;
        }

        SKPoint[]? rented = null;
        decimal[]? rentedPrices = null;
        Span<SKPoint> screenPoints = activeCount <= DrawingBufferLimits.StackAllocElementThreshold
            ? stackalloc SKPoint[activeCount]
            : (rented = ArrayPool<SKPoint>.Shared.Rent(activeCount)).AsSpan(0, activeCount);

        try
        {
            Span<decimal> activePrices = activeCount <= DrawingBufferLimits.StackAllocElementThreshold
                ? stackalloc decimal[activeCount]
                : (rentedPrices = ArrayPool<decimal>.Shared.Rent(activeCount)).AsSpan(0, activeCount);

            for (int i = 0; i < activeCount; i++)
            {
                global::Avalonia.Point p = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)p.X, (float)p.Y);
                activePrices[i] = Points[i].Price;
            }

            ElliottWaveValidationResult validation = (ShowRuleValidation && PatternMode == WavePatternMode.Impulse12345)
                ? ElliottWaveValues.ValidateImpulse(activePrices, isImpulse: true)
                : default;

            SKColor baseColor = SkiaColor;

            // 1. Polyline connecting active pivots (violated Impulse segments rendered in loss color)
            if (activeCount >= 2)
            {
                SKPathEffect? strokeEffect = StrokeStyle.ToSkPathEffect();
                _linePaint.Color = baseColor;
                _linePaint.StrokeWidth = (float)Thickness;
                _linePaint.PathEffect = strokeEffect;

                _linePath.Rewind();

                if (!validation.HasAnyViolation)
                {
                    _linePath.MoveTo(screenPoints[0]);
                    for (int i = 1; i < activeCount; i++)
                    {
                        _linePath.LineTo(screenPoints[i]);
                    }

                    canvas.DrawPath(_linePath, _linePaint);
                }
                else
                {
                    _violationPath.Rewind();
                    _violationPaint.Color = RuleViolationColor;
                    _violationPaint.StrokeWidth = (float)Thickness;
                    _violationPaint.PathEffect = strokeEffect;

                    bool inNormalRun = false;
                    for (int seg = 0; seg < activeCount - 1; seg++)
                    {
                        if (validation.IsSegmentViolated(seg))
                        {
                            _violationPath.MoveTo(screenPoints[seg]);
                            _violationPath.LineTo(screenPoints[seg + 1]);
                            inNormalRun = false;
                        }
                        else
                        {
                            if (!inNormalRun)
                            {
                                _linePath.MoveTo(screenPoints[seg]);
                                inNormalRun = true;
                            }
                            _linePath.LineTo(screenPoints[seg + 1]);
                        }
                    }

                    if (!_linePath.IsEmpty)
                    {
                        canvas.DrawPath(_linePath, _linePaint);
                    }
                    if (!_violationPath.IsEmpty)
                    {
                        canvas.DrawPath(_violationPath, _violationPaint);
                    }
                }
            }

            // 2. Vertex labels
            _textPaint.TextSize = DrawingThemeContext.DrawingFontSize > 0f
                ? DrawingThemeContext.DrawingFontSize
                : DrawingThemeContext.FontSize;
            _textPaint.Typeface = DrawingThemeContext.DefaultTextTypeface;

            float ascentAbs = MathF.Abs(_textPaint.FontMetrics.Ascent);

            for (int i = 0; i < activeCount; i++)
            {
                string? label = GetLabel(PatternMode, i);
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                bool placeAbove = activeCount == 1
                    || (i >= 1
                        ? Points[i].Price >= Points[i - 1].Price
                        : Points[1].Price < Points[0].Price);

                float labelWidth = _textPaint.MeasureText(label);
                float x = screenPoints[i].X - (labelWidth * 0.5f);
                float y = placeAbove
                    ? (screenPoints[i].Y - ChartConstants.ElliottWaveLabelOffsetY)
                    : (screenPoints[i].Y + ChartConstants.ElliottWaveLabelOffsetY + ascentAbs);

                _textPaint.Color = validation.IsTerminalPointViolated(i)
                    ? RuleViolationColor
                    : baseColor;
                canvas.DrawText(label, x, y, _textPaint);
            }

            // 3. Selection handles only for active pivots
            if (IsSelected)
            {
                for (int i = 0; i < activeCount; i++)
                {
                    SelectionHandleRenderer.Draw(
                        canvas,
                        new global::Avalonia.Point(screenPoints[i].X, screenPoints[i].Y),
                        baseColor);
                }
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
            if (rentedPrices != null)
            {
                ArrayPool<decimal>.Shared.Return(rentedPrices);
            }
        }
    }

    public bool HitTest(
        global::Avalonia.Point screenPoint,
        ICoordinateTransform transform,
        double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        int activeCount = ActivePointCount;
        if (_gate.IsDisposed || !IsVisible || transform == null || activeCount < 2)
        {
            return false;
        }

        var skScreenPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);
        SKPoint[]? rented = null;
        Span<SKPoint> screenPoints = activeCount <= DrawingBufferLimits.StackAllocElementThreshold
            ? stackalloc SKPoint[activeCount]
            : (rented = ArrayPool<SKPoint>.Shared.Rent(activeCount)).AsSpan(0, activeCount);

        try
        {
            for (int i = 0; i < activeCount; i++)
            {
                global::Avalonia.Point p = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)p.X, (float)p.Y);
            }

            return BezierSplineMath.HitTestPolyline(skScreenPt, screenPoints, tolerance);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
        }
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
        int activeCount = Math.Min(ActivePointCount, MaxActivePivotCount);
        var indColor = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        bool changed = !_hasCalculated
            || _cachedActiveCount != activeCount
            || _cachedPatternMode != PatternMode
            || _cachedIndColor != indColor;

        if (!changed)
        {
            for (int i = 0; i < activeCount; i++)
            {
                if (_cachedPrices[i] != Points[i].Price)
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            Span<decimal> activePrices = stackalloc decimal[activeCount];
            for (int i = 0; i < activeCount; i++)
            {
                decimal price = Points[i].Price;
                activePrices[i] = price;
                _cachedPrices[i] = price;
            }

            _cachedValues = ElliottWaveValues.Calculate(
                activePrices,
                isImpulse: PatternMode == WavePatternMode.Impulse12345,
                indColor);
            _cachedActiveCount = activeCount;
            _cachedPatternMode = PatternMode;
            _cachedIndColor = indColor;
            _hasCalculated = true;
        }

        return _cachedValues;
    }

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed())
            {
                return;
            }

            _linePaint.Dispose();
            _violationPaint.Dispose();
            _textPaint.Dispose();
            _linePath.Dispose();
            _violationPath.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
