using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class DtwProjectionObject : IChartObject
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
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public Color UnmatchedColor { get; set; } = global::Avalonia.Media.Color.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDtwUnmatchedColor);
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
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

    public bool IsUnmatched { get; set; } = false;
    public bool HasMatch => ProjectedPath != null && ProjectedPath.Count > 0 && !IsUnmatched;

    // Projected path data populated by the ML service
    // Point: X = timestamp (ticks), Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Matched pattern location in the historical data (populated by ML search)
    // Stored as timestamps for coordinate-transform compatibility in the renderer.
    public DateTime? MatchedStartTime { get; set; }
    public DateTime? MatchedEndTime { get; set; }

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
    }
}

