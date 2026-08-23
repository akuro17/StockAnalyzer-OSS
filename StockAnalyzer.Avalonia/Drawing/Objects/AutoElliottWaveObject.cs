using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Represents a user-drawn region on the chart to scan for Auto Elliott Wave patterns.
/// Stores the start/end bounds and the cached detection results.
/// Follows the same architecture as <see cref="HarmonicPatternObject"/>.
/// </summary>
public class AutoElliottWaveObject : IChartObject, IDisposable
{
    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.AutoElliottWaveRenderer _renderer = new();
    private bool _disposed;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.AutoElliottWave;
    
    public List<ChartPoint> Points { get; } = new(2);
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>
    /// Opacity (0-100%) of the light background band drawn between the start/end points.
    /// </summary>
    public int FillOpacity { get; set; } = 10;

    /// <summary>
    /// Color of the light background band drawn between the start/end points, independent of
    /// the border/line <see cref="Color"/>.
    /// </summary>
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    /// <summary>
    /// Index of the currently hovered wave result label.
    /// -1 means no label is hovered (show all wave patterns).
    /// </summary>
    public int HoveredResultIndex { get; set; } = -1;

    /// <summary>
    /// The ZigZag threshold percentage used for extracting pivot points within the region.
    /// If null, uses the multi-scale default logic in the detector.
    /// </summary>
    [DisplayName("ZigZag Threshold")]
    [Category("Detection")]
    [CoreParameterRange(1.0, 15.0)]
    public decimal? ZigZagThreshold { get; set; } = null;
    
    /// <summary>
    /// Cached detection results within the defined window.
    /// The renderer reads this to draw the wave patterns.
    /// </summary>
    public IReadOnlyList<ElliottWaveResult> CachedResults { get; private set; } = Array.Empty<ElliottWaveResult>();

    public AutoElliottWaveObject()
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
    /// Hit-tests the confidence score label areas of all cached wave results.
    /// Returns the index of the label under the screen point, or -1 if none.
    /// Must use the same positioning logic as AutoElliottWaveRenderer.RenderWavePattern.
    /// </summary>
    public int HitTestLabel(global::Avalonia.Point screenPoint, ICoordinateTransform transform)
    {
        if (CachedResults == null || CachedResults.Count == 0 || Points.Count < 2)
            return -1;

        for (int i = 0; i < CachedResults.Count; i++)
        {
            var result = CachedResults[i];
            if (result.WavePoints.Count < 2) continue;

            // Replicate score text position from AutoElliottWaveRenderer
            var firstScreen = transform.ChartToScreen(new ChartPoint(result.WavePoints[0].Time, result.WavePoints[0].Price));
            var lastScreen = transform.ChartToScreen(new ChartPoint(
                result.WavePoints[result.WavePoints.Count - 1].Time,
                result.WavePoints[result.WavePoints.Count - 1].Price));

            float centerX = (float)(firstScreen.X + lastScreen.X) / 2f;

            float topY = float.MaxValue;
            foreach (var wp in result.WavePoints)
            {
                var sp = transform.ChartToScreen(new ChartPoint(wp.Time, wp.Price));
                topY = Math.Min(topY, (float)sp.Y);
            }

            // Score text is drawn at (centerX - textWidth/2, topY - 20) with textSize 11
            string type = result.IsImpulse ? "Impulse" : "Corrective";
            string scoreText = $"{type} ({(result.ConfidenceScore * 100):F0}%)";
            float textWidth = scoreText.Length * 11f * 0.6f;
            float textX = centerX - textWidth / 2f;
            float textY = topY - 20f;

            // Generous hit area around the label
            var labelRect = new global::Avalonia.Rect(textX - 10f, textY - 25f, textWidth + 20f, 40f);

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
            CachedResults = Array.Empty<ElliottWaveResult>();
            return;
        }

        var minTime = Points[0].Time < Points[1].Time ? Points[0].Time : Points[1].Time;
        var maxTime = Points[0].Time > Points[1].Time ? Points[0].Time : Points[1].Time;

        var subCandles = new List<CandleData>();
        int startIndex = -1;
        int currentIndex = 0;

        foreach (var candle in candles)
        {
            if (candle.Timestamp >= minTime && candle.Timestamp <= maxTime)
            {
                if (startIndex == -1) startIndex = currentIndex;
                subCandles.Add(candle);
            }
            currentIndex++;
        }

        if (subCandles.Count < StockAnalyzer.Core.ChartConstants.ElliottMinCandleCount)
        {
            CachedResults = Array.Empty<ElliottWaveResult>();
            return;
        }

        // Run detection engine
        IReadOnlyList<ElliottWaveResult> resultsList;
        if (ZigZagThreshold.HasValue)
        {
            resultsList = ElliottWaveDetector.Detect(subCandles, new[] { ZigZagThreshold.Value });
        }
        else
        {
            resultsList = ElliottWaveDetector.Detect(subCandles);
        }

        // Adjust indices back to global coordinates
        var adjustedResults = new List<ElliottWaveResult>(resultsList.Count);
        foreach (var r in resultsList)
        {
            var adjustedPoints = new List<PivotPoint>(r.WavePoints.Count);
            foreach (var wp in r.WavePoints)
            {
                adjustedPoints.Add(new PivotPoint(
                    wp.Index + startIndex, wp.Time, wp.Price, wp.IsHigh));
            }
            adjustedResults.Add(new ElliottWaveResult(
                r.IsImpulse, r.IsBullish, adjustedPoints, r.ConfidenceScore, r.CurrentPhase));
        }

        CachedResults = adjustedResults;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.Dispose();
    }
}

