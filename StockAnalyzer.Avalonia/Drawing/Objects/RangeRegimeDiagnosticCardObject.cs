using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SkiaSharp;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Range regime diagnostic card: a small panel at the left edge of the range that lists the Sharpe, Sortino, CVaR 95 %, CAGR, Hurst (R/S and DFA), ADF and KPSS statistics of the closes
/// between the two anchors (<see cref="RangeRegimeDiagnosticCardValues"/> holds the calculation, the definitions and the Data-tab rows). The header line always shows the annualisation
/// (<c>Ann: 252/yr</c>); a statistic that cannot be calculated shows "-". The card draws nothing in price space, so it needs no scale and no price axis.
/// </summary>
public sealed class RangeRegimeDiagnosticCardObject : RangeAnalysisObjectBase
{
    private const float PanelMargin = 6f;
    private const float PanelPadding = 6f;
    private const float LineSpacingFactor = 1.35f;
    private const byte PanelFillAlpha = 190;
    private const byte PanelBorderAlpha = 220;

    private int _periodsPerYear = RangeRegimeDiagnosticCardValues.DefaultPeriodsPerYear;

    private RangeRegimeDiagnosticCard _card = RangeRegimeDiagnosticCard.Empty;
    private string[] _lines = Array.Empty<string>();
    private IReadOnlyList<DrawingCalculatedValue> _cachedValues = Array.Empty<DrawingCalculatedValue>();
    private IndicatorColor _cachedValuesColor;
    private bool _hasCachedValues;
    private SKRect _panel;

    private readonly SKPaint _panelFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _panelBorderPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1f };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    public RangeRegimeDiagnosticCardObject()
    {
    }

    public RangeRegimeDiagnosticCardObject(ChartPoint start, ChartPoint end)
    {
        Points.Add(start);
        Points.Add(end);
    }

    public override ChartObjectType Type => ChartObjectType.RangeRegimeDiagnosticCard;

    /// <summary>Bars per year of the annualised rows (Sharpe, Sortino, CAGR). Never derived from the chart time frame: choose 52 for weekly bars.</summary>
    [Category("Parameters")]
    [DisplayName("Periods Per Year")]
    [Range(RangeRegimeDiagnosticCardValues.MinPeriodsPerYear, RangeRegimeDiagnosticCardValues.MaxPeriodsPerYear)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public int PeriodsPerYear
    {
        get => _periodsPerYear;
        set => _periodsPerYear = Math.Clamp(value, RangeRegimeDiagnosticCardValues.MinPeriodsPerYear, RangeRegimeDiagnosticCardValues.MaxPeriodsPerYear);
    }

    /// <summary>The cached card (tests read it; it is not persisted).</summary>
    internal RangeRegimeDiagnosticCard Card
    {
        get
        {
            lock (SyncRoot)
            {
                return _card;
            }
        }
    }

    protected override string? OverlayText => null;

    protected override void ComputeRange(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex)
    {
        _card = RangeRegimeDiagnosticCardValues.Calculate(candles, firstIndex, lastIndex, _periodsPerYear);
        _lines = RangeRegimeDiagnosticCardValues.Lines(_card);
        _hasCachedValues = false;
    }

    protected override void ClearResult()
    {
        _card = RangeRegimeDiagnosticCard.Empty;
        _lines = Array.Empty<string>();
        _hasCachedValues = false;
        _panel = default;
    }

    protected override void RenderResult(SKCanvas canvas, ICoordinateTransform transform, float leftX, float rightX, SKColor color)
    {
        if (_lines.Length == 0)
        {
            return;
        }

        SKRect clip = canvas.LocalClipBounds;
        _linePaint.Color = DrawingThemeContext.MainTextSkColor;
        _linePaint.TextSize = DrawingThemeContext.DetailFontSize > 0f ? DrawingThemeContext.DetailFontSize : DrawingThemeContext.DrawingFontSize;
        _linePaint.Typeface = DrawingThemeContext.DefaultTextTypeface;

        float lineHeight = _linePaint.TextSize * LineSpacingFactor;
        float textWidth = 0f;
        for (int i = 0; i < _lines.Length; i++)
        {
            textWidth = MathF.Max(textWidth, _linePaint.MeasureText(_lines[i]));
        }

        float left = leftX + PanelMargin;
        float top = clip.Top + PanelMargin;
        _panel = new SKRect(left, top, left + textWidth + (2f * PanelPadding), top + (lineHeight * _lines.Length) + (2f * PanelPadding));

        _panelFillPaint.Color = DrawingThemeContext.ChartBackground.WithAlpha(PanelFillAlpha);
        canvas.DrawRoundRect(_panel, PanelPadding, PanelPadding, _panelFillPaint);
        _panelBorderPaint.Color = color.WithAlpha(PanelBorderAlpha);
        canvas.DrawRoundRect(_panel, PanelPadding, PanelPadding, _panelBorderPaint);

        float y = _panel.Top + PanelPadding + _linePaint.TextSize;
        for (int i = 0; i < _lines.Length; i++)
        {
            canvas.DrawText(_lines[i], _panel.Left + PanelPadding, y, _linePaint);
            y += lineHeight;
        }
    }

    protected override IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color)
    {
        if (!_hasCachedValues || _cachedValuesColor != color)
        {
            _cachedValues = RangeRegimeDiagnosticCardValues.Calculate(_card, color);
            _cachedValuesColor = color;
            _hasCachedValues = true;
        }

        return _cachedValues;
    }

    protected override bool HitTestResult(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance)
        => _lines.Length > 0
            && screenPoint.X >= _panel.Left - tolerance && screenPoint.X <= _panel.Right + tolerance
            && screenPoint.Y >= _panel.Top - tolerance && screenPoint.Y <= _panel.Bottom + tolerance;

    protected override void DisposeNativeResources()
    {
        _panelFillPaint.Dispose();
        _panelBorderPaint.Dispose();
        _linePaint.Dispose();
    }
}
