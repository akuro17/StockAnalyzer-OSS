using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class DtwProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.DtwProjection;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Core properties
    private Color _color = DrawingThemeContext.DefaultColor;
    public Color Color { get => _color; set { _color = value; InvalidateCalculatedValues(); } }
    public Color UnmatchedColor { get; set; } = global::Avalonia.Media.Color.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDtwUnmatchedColor);
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>
    /// Opacity (0-100%) of the light selection-range background band drawn between the start/end points.
    /// </summary>
    public int FillOpacity { get; set; } = 10;

    /// <summary>
    /// Color of the light selection-range background band drawn between the start/end points,
    /// independent of <see cref="Color"/>/<see cref="UnmatchedColor"/> (used regardless of match status).
    /// </summary>
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    /// <summary>
    /// Number of future candles the ML search projects the pattern forward. Passed as
    /// `futureSteps` to <see cref="StockAnalyzer.Core.Services.MarketStructureService.SearchSimilarPatternsAsync"/>.
    /// </summary>
    public int FutureSteps { get; set; } = 20;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaUnmatchedColor => new(UnmatchedColor.R, UnmatchedColor.G, UnmatchedColor.B, UnmatchedColor.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    public bool IsUnmatched { get => _isUnmatched; set { _isUnmatched = value; InvalidateCalculatedValues(); } }
    private bool _isUnmatched;
    public bool HasMatch => ProjectedPath != null && ProjectedPath.Count > 0 && !IsUnmatched;

    /// <summary>
    /// When true, uses native Causal Left Matrix Profile + Top-K Petitjean DBA ensemble projection and renders
    /// the historical analog weighted 15%-85% empirical quantile range (not a parametric confidence interval).
    /// </summary>
    public bool UseDbaEnsemble { get => _useDbaEnsemble; set { _useDbaEnsemble = value; InvalidateCalculatedValues(); } }
    private bool _useDbaEnsemble;

    /// <summary>
    /// Number of non-overlapping historical matches used by the DBA ensemble (default 5).
    /// </summary>
    public int TopKMatches { get => _topKMatches; set { _topKMatches = value; InvalidateCalculatedValues(); } }
    private int _topKMatches = StockAnalyzer.Core.Models.IndicatorDefaultConstants.DbaDefaultTopK;

    internal readonly record struct SearchSettingsSnapshot(int FutureSteps, bool UseDbaEnsemble, int TopKMatches);

    internal SearchSettingsSnapshot CaptureSearchSettings() => new(FutureSteps, UseDbaEnsemble, TopKMatches);

    internal bool HasSearchSettingsChanged(in SearchSettingsSnapshot before)
        => before.FutureSteps != FutureSteps
           || before.UseDbaEnsemble != UseDbaEnsemble
           || before.TopKMatches != TopKMatches;

    // Projected path data populated by the ML / Matrix Profile DBA search
    // Point: X = timestamp (ticks), Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get => _projectedPath; set { _projectedPath = value; InvalidateCalculatedValues(); } }
    private List<StockAnalyzer.Core.Models.Point> _projectedPath = new();
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get => _upperBandPath; set { _upperBandPath = value; InvalidateCalculatedValues(); } }
    private List<StockAnalyzer.Core.Models.Point> _upperBandPath = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get => _lowerBandPath; set { _lowerBandPath = value; InvalidateCalculatedValues(); } }
    private List<StockAnalyzer.Core.Models.Point> _lowerBandPath = new();

    // Matched pattern location in the historical data (populated by ML search)
    // Stored as timestamps for coordinate-transform compatibility in the renderer.
    public DateTime? MatchedStartTime { get => _matchedStartTime; set { _matchedStartTime = value; InvalidateCalculatedValues(); } }
    private DateTime? _matchedStartTime;
    public DateTime? MatchedEndTime { get => _matchedEndTime; set { _matchedEndTime = value; InvalidateCalculatedValues(); } }
    private DateTime? _matchedEndTime;

    /// <summary>DTW / matrix-profile distance of the best historical match (0 when unmatched). Search result, never persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double BestMatchDistance { get => _bestMatchDistance; set { _bestMatchDistance = value; InvalidateCalculatedValues(); } }
    private double _bestMatchDistance;

    /// <summary>Similarity of the best historical match in percent (0-100; 0 when unmatched). Search result, never persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double BestMatchSimilarity { get => _bestMatchSimilarity; set { _bestMatchSimilarity = value; InvalidateCalculatedValues(); } }
    private double _bestMatchSimilarity;

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.DtwProjectionRenderer _renderer = new();

    public DtwProjectionObject()
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
        IsUnmatched = false;
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(
                Points[i].Time + timeDelta,
                Points[i].Price + priceDelta
            );
        }

        if (ProjectedPath != null)
        {
            var newPath = new List<StockAnalyzer.Core.Models.Point>();
            foreach (var p in ProjectedPath)
            {
                newPath.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            ProjectedPath = newPath;
        }

        if (UpperBandPath != null && UpperBandPath.Count > 0)
        {
            var newUpper = new List<StockAnalyzer.Core.Models.Point>(UpperBandPath.Count);
            foreach (var p in UpperBandPath)
            {
                newUpper.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            UpperBandPath = newUpper;
        }

        if (LowerBandPath != null && LowerBandPath.Count > 0)
        {
            var newLower = new List<StockAnalyzer.Core.Models.Point>(LowerBandPath.Count);
            foreach (var p in LowerBandPath)
            {
                newLower.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            LowerBandPath = newLower;
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        // ChartViewModel and ChartInteractionController clear ProjectedPath IN PLACE (drag, new search), which no setter sees:
        // an empty path means no rows whatever the cache holds (O(1), no allocation).
        if (!HasMatch) return Array.Empty<DrawingCalculatedValue>();
        return _calculatedValuesCache ??= BuildCalculatedValues();
    }

    // Rows depend only on Color, IsUnmatched (HasMatch), UseDbaEnsemble, TopKMatches, the three paths (replaced, never edited in place),
    // MatchedStartTime/EndTime and BestMatchDistance/Similarity; every setter of those properties calls InvalidateCalculatedValues.
    private DrawingCalculatedValue[]? _calculatedValuesCache;

    private void InvalidateCalculatedValues() => _calculatedValuesCache = null;

    private DrawingCalculatedValue[] BuildCalculatedValues()
    {
        if (!HasMatch) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new("DTW Mode", "DTW Mode", TopKMatches, UseDbaEnsemble ? string.Create(DrawingValueFormat.Culture, $"DBA Ensemble (Top-{TopKMatches})") : "Single Best Match", color)
        };

        decimal anchor = (decimal)ProjectedPath[0].Y;
        decimal target = (decimal)ProjectedPath[^1].Y;
        decimal pctChange = anchor != 0m ? (target - anchor) / anchor * 100m : 0m;
        list.Add(new("DTW Target Price", "DTW Target Price", target, string.Create(DrawingValueFormat.Culture, $"{target:F2} ({pctChange:+0.00;-0.00;0.00}%)"), color));

        if (MatchedStartTime.HasValue && MatchedEndTime.HasValue)
        {
            list.Add(new("DTW Matched Window", "DTW Matched Window", null,
                string.Create(DrawingValueFormat.Culture, $"{MatchedStartTime.Value:yyyy/MM/dd} ~ {MatchedEndTime.Value:yyyy/MM/dd}"), color));
        }

        if (BestMatchSimilarity > 0.0)
        {
            list.Add(new("DTW Similarity", "DTW Similarity", IndicatorCalculationHelper.ToDecimalOrNull(Math.Round(BestMatchSimilarity, 1)),
                string.Create(DrawingValueFormat.Culture, $"{BestMatchSimilarity:F1}% (Dist={BestMatchDistance:F3})"), color));
        }

        if (UseDbaEnsemble && UpperBandPath.Count > 0 && LowerBandPath.Count > 0)
        {
            decimal upper = (decimal)UpperBandPath[^1].Y;
            decimal lower = (decimal)LowerBandPath[^1].Y;
            list.Add(new("DTW Band (15%-85%)", "DTW Band (15%-85%)", Math.Round(upper - lower, 2), string.Create(DrawingValueFormat.Culture, $"{lower:F2} ~ {upper:F2}"), color));
        }

        return list.ToArray();
    }
}
