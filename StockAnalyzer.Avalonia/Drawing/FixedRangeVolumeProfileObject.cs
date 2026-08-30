using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Analysis; // Using the new analysis namespace

namespace StockAnalyzer.Avalonia.Drawing;

public class FixedRangeVolumeProfileObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FixedRangeVolumeProfile;

    // Defined by 2 points (Box corners) to define Time Range and Vertical Range (optional, usually full height of candles? No, Fixed Range usually means Time and Price range? 
    // "Fixed Range Volume Profile" typically means Time Range. Price range is usually determined by the High/Low of the candles in that time range.
    // However, the box allows limiting the price range too if desired. 
    // Let's stick to standard FRVP: Time Range defined by box width. Price range defined by max/min of included candles OR box height?
    // User expectation: Box defines the area. If box is drawn, maybe filters volume only within that price range too?
    // Standard implementation: All volume in time range.
    // Let's use the Box to define Time Range. Vertical range of the box can be used to constrain calculation, or just visual.
    // Let's implement specific logic: Filter candles by Time Range. Calculate Profile. Render Profile *inside* the box or extending from Left/Right of box?
    // Usually "Fixed Range" means "Over this time period".
    // I will use Time Range from Points[0].X to Points[1].X.
    // Vertical placement: I'll auto-scale to the Price Range of the candles, ignoring Box Y for calculation (unless user wants to filter price).
    // Box Y can be used for visual clipping? 
    // Let's start simplest: Time Range determines candles. Profile computed on Candle High/Low. Rendered across the time width.
    
    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = Colors.Blue; // User req: Blue
    public Color ValueAreaColor { get; set; } = Colors.Green; // User req: Green
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    public List<VolumeBin> ProfileData { get; private set; } = new List<VolumeBin>();
    public decimal VAH { get; private set; }
    public decimal VAL { get; private set; }
    
    // Config
    public bool RightToLeft { get; set; } = false; // Draw from right side of box?
    public double Opacity { get; set; } = 0.25; // User req: 25%

    public FixedRangeVolumeProfileObject(ChartPoint p1, ChartPoint p2)
    {
        Points = new List<ChartPoint> { p1, p2 };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, (byte)(255 * Opacity));
    public SKColor SkiaValueColor => new SKColor(ValueAreaColor.R, ValueAreaColor.G, ValueAreaColor.B, (byte)(255 * Opacity));

    // Reused across Render() calls (ZeroAllocation Render Loop, SA_RENDERING_PERFORMANCE.md
    // §1) instead of a `new SKPaint` per frame. Color-dependent properties are refreshed
    // from the current property values on each use since they can change between renders.
    private readonly SKPaint _previewPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _profilePaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _valueAreaPaint = new SKPaint { Style = SKPaintStyle.Fill };

    public void Dispose()
    {
        _previewPaint.Dispose();
        _profilePaint.Dispose();
        _valueAreaPaint.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        if (Points.Count < 2) return;

        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var start = t1 < t2 ? t1 : t2;
        var end = t1 < t2 ? t2 : t1;

        var range = candles.Where(c => c.Timestamp >= start && c.Timestamp <= end).ToList();
        
        // Calculate Profile
        // Row Size: Auto-calculate based on nice visual? or fixed?
        // Let's use 50 rows for decent resolution.
        ProfileData = VolumeAnalysis.CalculateProfile(range, 50);
        
        // Calculate Value Area
        var va = VolumeAnalysis.CalculateValueArea(ProfileData);
        VAH = va.VAH;
        VAL = va.VAL;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        if (ProfileData == null || !ProfileData.Any())
        {
            // Not enough candles in the [Points[0].Time, Points[1].Time] range yet to
            // compute a volume profile (e.g. right after the first click, before the
            // second point has been placed far enough away). Draw a lightweight raw
            // box-outline preview directly from Points[] so the selected range visibly
            // follows the cursor while choosing point 2, consistent with other 2-point
            // drawing tools whose raw line/shape geometry is visible immediately.
            var previewP1 = transform.ChartToScreen(Points[0]);
            var previewP2 = transform.ChartToScreen(Points[1]);
            _previewPaint.Color = SkiaColor;
            _previewPaint.StrokeWidth = (float)Thickness;
            var previewRect = new SKRect(
                (float)Math.Min(previewP1.X, previewP2.X), (float)Math.Min(previewP1.Y, previewP2.Y),
                (float)Math.Max(previewP1.X, previewP2.X), (float)Math.Max(previewP1.Y, previewP2.Y));
            canvas.DrawRect(previewRect, _previewPaint);
            SelectionHandleRenderer.Draw(canvas, previewP1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, previewP2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            return;
        }

        // Visual Bounds
        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        float left = (float)Math.Min(p1.X, p2.X);
        float right = (float)Math.Max(p1.X, p2.X);
        float width = right - left;
        
        // Draw Histogram
        _profilePaint.Color = SkiaColor;
        _valueAreaPaint.Color = SkiaValueColor;

        foreach (var bin in ProfileData)
        {
            float yTop = (float)transform.ChartToScreen(new ChartPoint(DateTime.Now, bin.UpperBound)).Y;
            float yBottom = (float)transform.ChartToScreen(new ChartPoint(DateTime.Now, bin.LowerBound)).Y;
            
            // Bar Width relative to Max Volume in this profile
            float barWidth = (float)(bin.WidthPercent * width);
            
            // Draw Direction
            float xStart = left;
            float xEnd = left + barWidth;

            var rect = new SKRect(xStart, yTop, xEnd, yBottom);
            
            // Choose color: Value Area vs Normal
            bool isValueArea = bin.Price >= VAL && bin.Price <= VAH;
            
            canvas.DrawRect(rect, isValueArea ? _valueAreaPaint : _profilePaint);
            // Optional: Draw Buy/Sell split? Complex.
        }

        // Draw Box outline
        float top = (float)Math.Min(p1.Y, p2.Y);
        float bottom = (float)Math.Max(p1.Y, p2.Y);
        
        // Actually, let's draw outline around Time Range and Candle Price Range? 
        // Or user defined box?
        // User defined box is useful for selection.
        var userRect = new SKRect(left, top, right, bottom);
        // canvas.DrawRect(userRect, new SKPaint { Color = SKColors.Gray, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0) });

        // Control-point handles are always shown (not gated by IsSelected), matching the
        // "no profile data yet" preview above: while placing point 2 during two-click
        // creation the object is never IsSelected (that only becomes true once it joins
        // ChartObjectManager on finish), so gating here made the handles vanish the
        // moment enough candles existed to compute a profile -- only the histogram bars
        // stayed visible.
        //
        // Drawn at the raw p1/p2 positions (Points[0]/Points[1] transformed to screen),
        // NOT at the axis-aligned bounding-box corners (left,top)/(right,bottom) used
        // above for the histogram/outline. ChartInteractionController's generic handle
        // hit-test checks distance to Points[i]'s own screen position, so when the box is
        // drawn "anti-diagonally" (e.g. Points[0] at the bottom-left, Points[1] at the
        // top-right -- a natural uptrend-range gesture), left/top and right/bottom are
        // synthesized corners that belong to neither point, and clicking the visible
        // handle circle would never register as a hit on either one.
        SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        // Default to user box
        double x1 = p1.X;
        double x2 = p2.X;
        double y1 = p1.Y;
        double y2 = p2.Y;

        // If we have profile data, use its price range for Y bounds
        if (ProfileData != null && ProfileData.Count > 0)
        {
            var minPrice = ProfileData.Min(b => b.LowerBound);
            var maxPrice = ProfileData.Max(b => b.UpperBound);
            
            var topPt = transform.ChartToScreen(new ChartPoint(Points[0].Time, maxPrice));
            var bottomPt = transform.ChartToScreen(new ChartPoint(Points[0].Time, minPrice));
            
            // Screen Y usually grows downwards. MaxPrice -> Lower Y value.
            y1 = topPt.Y;
            y2 = bottomPt.Y;
        }

        var rect = new global::Avalonia.Rect(
            new global::Avalonia.Point(Math.Min(x1, x2), Math.Min(y1, y2)),
            new global::Avalonia.Size(Math.Abs(x2 - x1), Math.Abs(y2 - y1)));
        return rect.Contains(screenPoint);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price); 
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (ProfileData == null || ProfileData.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var vaColor = new IndicatorColor(ValueAreaColor.A, ValueAreaColor.R, ValueAreaColor.G, ValueAreaColor.B);

        var pocBin = ProfileData.OrderByDescending(b => b.TotalVolume).FirstOrDefault();
        decimal pocPrice = pocBin?.Price ?? 0m;

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("POC", "Point of Control (POC)", pocPrice, $"{pocPrice:F2}", color),
            new DrawingCalculatedValue("VAH", "Value Area High (VAH)", VAH, $"{VAH:F2}", vaColor),
            new DrawingCalculatedValue("VAL", "Value Area Low (VAL)", VAL, $"{VAL:F2}", vaColor)
        };
    }
}

