using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Represents a drawn Geometric Pattern (channel, flag, pennant, etc.).
/// Holds the detected formations and renders them on the chart using the coordinate transform.
/// </summary>
public class GeometricPatternObject : IChartObject, IDrawingCalculatedValuesProvider
{
    /// <summary>Maximum number of individual "Formation #rank" rows listed in the Data Window.</summary>
    public const int MaxListedFormations = 5;

    private const string DetectedFormationsKey = "Detected Formations";

    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.GeometricPattern;
    public List<ChartPoint> Points { get; private set; }
    private Color _color = DrawingThemeContext.DefaultColor;
    public Color Color { get => _color; set { _color = value; InvalidateCalculatedValues(); } }
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>
    /// The detected geometric formations to render.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<DetectedFormation>? Formations { get => _formations; set { _formations = value; InvalidateCalculatedValues(); } }
    private IReadOnlyList<DetectedFormation>? _formations;

    // Display Filters
    private bool _showChannels = true;
    private bool _showWedges = true;
    private bool _showTriangles = true;
    private bool _showPennantsAndFlags = true;
    private bool _showMegaphone = true;
    public bool ShowChannels { get => _showChannels; set { _showChannels = value; InvalidateCalculatedValues(); } }
    public bool ShowWedges { get => _showWedges; set { _showWedges = value; InvalidateCalculatedValues(); } }
    public bool ShowTriangles { get => _showTriangles; set { _showTriangles = value; InvalidateCalculatedValues(); } }
    public bool ShowPennantsAndFlags { get => _showPennantsAndFlags; set { _showPennantsAndFlags = value; InvalidateCalculatedValues(); } }
    public bool ShowMegaphone { get => _showMegaphone; set { _showMegaphone = value; InvalidateCalculatedValues(); } }
    
    // Detection Settings
    /// <summary>
    /// If set to a value, overrides multi-scale detection and uses this fixed threshold.
    /// If null, uses automated multi-scale detection.
    /// </summary>
    public decimal? ZigZagThreshold { get; set; } = null;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public GeometricPatternObject()
    {
        Points = new List<ChartPoint>();
        var themeColor = AppTheme.GeometricResistanceLine;
        Color = Color.FromArgb(themeColor.Alpha, themeColor.Red, themeColor.Green, themeColor.Blue);
    }

    public GeometricPatternObject(ChartPoint anchor)
    {
        Points = new List<ChartPoint> { anchor };
        var themeColor = AppTheme.GeometricResistanceLine;
        Color = Color.FromArgb(themeColor.Alpha, themeColor.Red, themeColor.Green, themeColor.Blue);
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (transform == null) return;

        // Render each formation
        if (Formations != null && Formations.Count > 0)
        {
            foreach (var formation in Formations)
            {
                if (!ShouldRender(formation.Type)) continue;
                if (!formation.IsBrokenOut) continue;
                
                RenderFormation(canvas, transform, formation);
            }
        }

        // Draw a selection handle at the anchor point if selected
        if (IsSelected && Points.Count > 0)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
        }
    }

    private void RenderFormation(SKCanvas canvas, ICoordinateTransform transform, DetectedFormation formation)
    {
        // 1. Calculate screen coordinates for the bounded lines
        var resStartScreen = transform.ChartToScreen(new ChartPoint(formation.StartTime, (decimal)formation.UpperPriceAt(formation.StartIndex)));
        var resEndScreen = transform.ChartToScreen(new ChartPoint(formation.EndTime, (decimal)formation.UpperPriceAt(formation.EndIndex)));
        
        var supStartScreen = transform.ChartToScreen(new ChartPoint(formation.StartTime, (decimal)formation.LowerPriceAt(formation.StartIndex)));
        var supEndScreen = transform.ChartToScreen(new ChartPoint(formation.EndTime, (decimal)formation.LowerPriceAt(formation.EndIndex)));

        // 2. Draw Fill Area (Polygon between the 4 points)
        using (var fillPaint = new SKPaint
        {
            Color = AppTheme.GeometricFormationFill,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            using var path = new SKPath();
            path.MoveTo((float)resStartScreen.X, (float)resStartScreen.Y);
            path.LineTo((float)resEndScreen.X, (float)resEndScreen.Y);
            path.LineTo((float)supEndScreen.X, (float)supEndScreen.Y);
            path.LineTo((float)supStartScreen.X, (float)supStartScreen.Y);
            path.Close();
            canvas.DrawPath(path, fillPaint);
        }

        // 3. Draw Resistance Line (Dashed Red)
        using (var resPaint = new SKPaint
        {
            Color = AppTheme.GeometricResistanceLine,
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
        })
        {
            canvas.DrawLine((float)resStartScreen.X, (float)resStartScreen.Y, (float)resEndScreen.X, (float)resEndScreen.Y, resPaint);
        }

        // 4. Draw Support Line (Dashed Green)
        using (var supPaint = new SKPaint
        {
            Color = AppTheme.GeometricSupportLine,
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
        })
        {
            canvas.DrawLine((float)supStartScreen.X, (float)supStartScreen.Y, (float)supEndScreen.X, (float)supEndScreen.Y, supPaint);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        // The "red dot" (Anchor point) hit test
        if (Points.Count > 0)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            if (Math.Abs(p1.X - screenPoint.X) <= tolerance && Math.Abs(p1.Y - screenPoint.Y) <= tolerance)
            {
                return true;
            }
        }
        
        if (Formations == null || Formations.Count == 0) return false;

        // A full polygon hit-test or distance to line segments could be implemented here.
        // For simplicity, we just return true if it's within the bounding box of any formation.
        foreach(var formation in Formations)
        {
            if (!ShouldRender(formation.Type)) continue;
            
            var resStart = transform.ChartToScreen(new ChartPoint(formation.StartTime, (decimal)formation.UpperPriceAt(formation.StartIndex)));
            var supEnd = transform.ChartToScreen(new ChartPoint(formation.EndTime, (decimal)formation.LowerPriceAt(formation.EndIndex)));
            
            double minX = Math.Min(resStart.X, supEnd.X);
            double maxX = Math.Max(resStart.X, supEnd.X);
            double minY = Math.Min(resStart.Y, supEnd.Y);
            double maxY = Math.Max(resStart.Y, supEnd.Y);
            
            if (screenPoint.X >= minX - tolerance && screenPoint.X <= maxX + tolerance &&
                screenPoint.Y >= minY - tolerance && screenPoint.Y <= maxY + tolerance)
            {
                return true;
            }
        }

        return false;
    }

    public void Recalculate(IEnumerable<StockAnalyzer.Core.Models.CandleData> candles)
    {
        if (Points.Count == 0 || candles == null) return;
        
        try
        {
            var candleDataList = candles
                .Where(c => c.Timestamp >= Points[0].Time)
                .ToList();
                
            var thresholds = ZigZagThreshold.HasValue ? new[] { ZigZagThreshold.Value } : null;
            var formations = StockAnalyzer.Core.Models.GeometricPattern.GeometricPatternDetector.Detect(candleDataList, thresholds);
            Formations = formations;
        }
        catch
        {
            // Ignore detection errors, Formations might be empty.
            if (Formations == null)
            {
                Formations = Array.Empty<StockAnalyzer.Core.Models.GeometricPattern.DetectedFormation>();
            }
        }
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        // Translate the anchor point
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
        
        // Re-detection happens via Recalculate called by the controller
    }
    
    /// <summary>
    /// Data Window rows for the formations that are drawn (<see cref="DetectedFormation.IsBrokenOut"/> and enabled by the display
    /// filters): the count plus the latest <see cref="MaxListedFormations"/> formations (newest end time first, then highest confidence).
    /// Confidence is a 0..1 R-squared fit score shown as a percentage; slopes are price units per bar. The formation type carries no
    /// breakout direction, so every row uses the object colour.
    /// </summary>
    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
        => _calculatedValuesCache ??= BuildCalculatedValues();

    // Rows depend only on Formations, the five Show* filters and Color; every setter of those properties calls InvalidateCalculatedValues.
    private DrawingCalculatedValue[]? _calculatedValuesCache;

    private void InvalidateCalculatedValues() => _calculatedValuesCache = null;

    private DrawingCalculatedValue[] BuildCalculatedValues()
    {
        var formations = Formations;
        if (formations == null || formations.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var visible = formations
            .Where(f => f.IsBrokenOut && ShouldRender(f.Type))
            .OrderByDescending(f => f.EndTime)
            .ThenByDescending(f => f.ConfidenceScore)
            .ToList();
        if (visible.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        int listed = Math.Min(visible.Count, MaxListedFormations);
        var values = new List<DrawingCalculatedValue>(listed + 1)
        {
            new DrawingCalculatedValue(
                DetectedFormationsKey,
                DetectedFormationsKey,
                visible.Count,
                string.Create(DrawingValueFormat.Culture, $"{visible.Count}"),
                color)
        };

        for (int i = 0; i < listed; i++)
        {
            var f = visible[i];
            double confidencePercent = f.ConfidenceScore * IndicatorDefaultConstants.PercentScale;
            string label = string.Create(DrawingValueFormat.Culture, $"Formation #{i + 1} ({f.Type})");
            values.Add(new DrawingCalculatedValue(
                label,
                label,
                (decimal)Math.Round(confidencePercent, 1),
                string.Create(DrawingValueFormat.Culture, $"Conf={confidencePercent:F0}%, U-Slope={f.UpperSlope:F3}, L-Slope={f.LowerSlope:F3}"),
                color));
        }

        return values.ToArray();
    }

    private bool ShouldRender(GeometricFormationType type)
    {
        switch (type)
        {
            case GeometricFormationType.HorizontalBox:
            case GeometricFormationType.AscendingChannel:
            case GeometricFormationType.DescendingChannel:
                return ShowChannels;
                
            case GeometricFormationType.SymmetricalTriangle:
            case GeometricFormationType.AscendingTriangle:
            case GeometricFormationType.DescendingTriangle:
                return ShowTriangles;
                
            case GeometricFormationType.Megaphone:
                return ShowMegaphone;
                
            case GeometricFormationType.BullishFlag:
            case GeometricFormationType.BearishFlag:
            case GeometricFormationType.Pennant:
                return ShowPennantsAndFlags;
                
            case GeometricFormationType.RisingWedge:
            case GeometricFormationType.FallingWedge:
                return ShowWedges;
                
            default:
                return true;
        }
    }
}

