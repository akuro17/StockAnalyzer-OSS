using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Represents a user-drawn region on the chart to scan for harmonic patterns.
/// Stores the start/end bounds and the cached detection results.
/// </summary>
public class HarmonicPatternObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.HarmonicPatternRenderer _renderer = new();
    private bool _disposed;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.HarmonicPattern;
    
    public List<ChartPoint> Points { get; } = new(2);
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;
    public bool ShowPrz { get; set; } = false;

    /// <summary>
    /// Opacity (0-100%) of the light background band drawn between the start/end points.
    /// </summary>
    public int FillOpacity { get; set; } = 10;

    /// <summary>
    /// Color of the light background band drawn between the start/end points, independent of
    /// the border/line <see cref="Color"/>.
    /// </summary>
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    /// <summary>
    /// Index of the currently hovered pattern result label.
    /// -1 means no label is hovered (show all patterns).
    /// </summary>
    public int HoveredResultIndex { get; set; } = -1;
    
    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    /// <summary>
    /// The ZigZag threshold percentage used for extracting pivot points within the region.
    /// If null, uses the multi-scale default logic in the detector.
    /// </summary>
    [DisplayName("ZigZag Threshold")]
    [Category("Detection")]
    [CoreParameterRange(1.0, 10.0)]
    public decimal? ZigZagThreshold { get; set; } = null;
    
    /// <summary>
    /// Cached detection results within the defined window.
    /// The renderer reads this to draw the patterns.
    /// </summary>
    public IReadOnlyList<HarmonicPatternResult> CachedResults { get; private set; } = Array.Empty<HarmonicPatternResult>();

    public HarmonicPatternObject()
    {
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        _renderer.Render(canvas, this, transform, IsSelected);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double minX = Math.Min(p1.X, p2.X) - tolerance;
        double maxX = Math.Max(p1.X, p2.X) + tolerance;

        return screenPoint.X >= minX && screenPoint.X <= maxX;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(
                Points[i].Time + timeDelta,
                Points[i].Price + priceDelta
            );
        }
    }

    /// <summary>
    /// Hit-tests the label areas of all cached pattern results.
    /// Returns the index of the label under the screen point, or -1 if none.
    /// Must use the same positioning logic as HarmonicPatternRenderer.RenderPattern.
    /// </summary>
    public int HitTestLabel(global::Avalonia.Point screenPoint, ICoordinateTransform transform)
    {
        if (CachedResults == null || CachedResults.Count == 0 || Points.Count < 2)
            return -1;

        const float textSize = 12f;

        for (int i = 0; i < CachedResults.Count; i++)
        {
            var result = CachedResults[i];
            var screenD = transform.ChartToScreen(new ChartPoint(result.D.Time, result.D.Price));

            // Replicate PRZ rect calculation from HarmonicPatternRenderer
            var przLowScreen = transform.ChartToScreen(new ChartPoint(result.D.Time, result.PrzLow));
            var przHighScreen = transform.ChartToScreen(new ChartPoint(result.D.Time, result.PrzHigh));

            float przTop = (float)Math.Min(przHighScreen.Y, przLowScreen.Y);
            float przBottom = (float)Math.Max(przHighScreen.Y, przLowScreen.Y);

            // Label position matches renderer: below PRZ for bullish, above for bearish
            float textY = result.IsBullish ? przBottom + 16f : przTop - 8f;
            float textX = (float)screenD.X;

            // Measure text width
            string labelText = $"{result.PatternType} ({(result.ConfidenceScore * 100):F1}%)";
            float textWidth = labelText.Length * textSize * 0.6f; // Approximate character width

            // Make the hit area generously large to ensure easy selection
            // The text is rendered at X=textX, Baseline=textY.
            // A 40px tall box centered roughly around the text vertically ensures it covers the text.
            float boxStartY = textY - 25f; // Starts above the text
            var labelRect = new global::Avalonia.Rect(textX - 10f, boxStartY, textWidth + 20f, 40f);

            if (labelRect.Contains(screenPoint))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Updates the cached results by running the detection engine on the candles within the start/end bounds.
    /// Called by the interaction controller when data changes or the object is modified.
    /// </summary>
    public void Recalculate(IEnumerable<CandleData> candles)
    {
        if (Points.Count < 2 || candles == null)
        {
            CachedResults = Array.Empty<HarmonicPatternResult>();
            return;
        }

        var minTime = Points[0].Time < Points[1].Time ? Points[0].Time : Points[1].Time;
        var maxTime = Points[0].Time > Points[1].Time ? Points[0].Time : Points[1].Time;

        var subCandles = new List<CandleData>();
        int startIndex = -1;
        int endIndex = -1;
        int currentIndex = 0;

        foreach (var candle in candles)
        {
            if (candle.Timestamp >= minTime && candle.Timestamp <= maxTime)
            {
                if (startIndex == -1) startIndex = currentIndex;
                endIndex = currentIndex;
                subCandles.Add(candle);
            }
            currentIndex++;
        }

        if (subCandles.Count < StockAnalyzer.Core.ChartConstants.HarmonicMinPivotCount)
        {
            CachedResults = Array.Empty<HarmonicPatternResult>();
            return;
        }

        // Run detection engine
        List<HarmonicPatternResult> resultsList;
        if (ZigZagThreshold.HasValue)
        {
            // User explicitly specified a threshold. Do not use multiscale.
            resultsList = HarmonicPatternDetector.Detect(subCandles, ZigZagThreshold.Value, new[] { ZigZagThreshold.Value }).ToList();
        }
        else
        {
            // Auto mode. Use multiscale fallback.
            resultsList = HarmonicPatternDetector.Detect(subCandles).ToList();
        }

        // Adjust indices back to global coordinates
        var adjustedResults = new List<HarmonicPatternResult>(resultsList.Count);
        foreach (var r in resultsList)
        {
            var x = new PivotPoint(r.X.Index + startIndex, r.X.Time, r.X.Price, r.X.IsHigh);
            var a = new PivotPoint(r.A.Index + startIndex, r.A.Time, r.A.Price, r.A.IsHigh);
            var b = new PivotPoint(r.B.Index + startIndex, r.B.Time, r.B.Price, r.B.IsHigh);
            var c = new PivotPoint(r.C.Index + startIndex, r.C.Time, r.C.Price, r.C.IsHigh);
            var d = new PivotPoint(r.D.Index + startIndex, r.D.Time, r.D.Price, r.D.IsHigh);
            
            adjustedResults.Add(new HarmonicPatternResult(r.PatternType, x, a, b, c, d, r.ConfidenceScore, r.PrzLow, r.PrzHigh, r.IsBullish));
        }

        CachedResults = adjustedResults;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.Dispose();
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (CachedResults == null || CachedResults.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        HarmonicPatternResult pattern = (HoveredResultIndex >= 0 && HoveredResultIndex < CachedResults.Count)
            ? CachedResults[HoveredResultIndex]
            : CachedResults.OrderByDescending(r => r.ConfidenceScore).First();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        string typeStr = $"{pattern.PatternType} ({(pattern.IsBullish ? "Bullish" : "Bearish")})";

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("PatternType", "Pattern", null, typeStr, color),
            new DrawingCalculatedValue("Confidence", "Confidence", (decimal)pattern.ConfidenceScore, $"{pattern.ConfidenceScore * 100:F1}%", color),
            new DrawingCalculatedValue("DPrice", "D (Target)", pattern.D.Price, $"{pattern.D.Price:F2}", color),
            new DrawingCalculatedValue("PRZ", "PRZ Range", (pattern.PrzLow + pattern.PrzHigh) / 2m, $"{pattern.PrzLow:F2} - {pattern.PrzHigh:F2}", IndicatorColor.Gray)
        };
    }
}

