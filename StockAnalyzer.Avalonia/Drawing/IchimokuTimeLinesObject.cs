using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Ichimoku Time Theory Lines Tool
/// Draws vertical lines according to Ichimoku Kinko Hyo time theory from a single origin/anchor point.
/// Automatically calculates basic (9, 17, 26) and composite numbers (33, 42, 51, 65, 76, 83, 97, 101, 129, 172, 226, 676).
/// Follows the Japanese inclusive counting principle (ryodan-ire) where the starting bar is Day 1 (offset 0),
/// projecting forward by adding (N - 1) days/bars from the anchor point.
/// </summary>
public class IchimokuTimeLinesObject : IChartObject, IDrawingCalculatedValuesProvider
{
    // Canonical sequence derived by composite formulas in Y:\Temp\webai.txt:
    // Basic: 9 (Section 1), 17 (Section 2: 9+9-1), 26 (Period 1: 17+9)
    // Composite: 33 (17+17-1), 42 (26+17-1), 51 (26+26-1), 65 (33+33-1), 76 (26*3-2: Cycle 1 / Jun),
    // 83 (42+42-1), 97 (33*3-1), 101 (26*4-3), 129 (65+65-1), 172 (33+65+76-2), 226 (76*3-2: Ring 1 / Kan), 676 (226*3-2: Grand Cycle 1 / Jun-kan)
    public static readonly int[] BaseNumbers = { 9, 17, 26, 33, 42, 51, 65, 76, 83, 97, 101, 129, 172, 226, 676 };
    public static readonly int[] AllCalculatedNumbers = BaseNumbers;

    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.IchimokuTimeLines;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>Maximum cycle limit for automatic calculation (optional, defaults to 676).</summary>
    public int MaxCycleLimit { get; set; } = 676;
    public bool ShowLabels { get; set; } = true;

    public IchimokuTimeLinesObject(ChartPoint start)
    {
        Points = new List<ChartPoint> { start };
    }

    public IchimokuTimeLinesObject(ChartPoint start, ChartPoint end)
    {
        // Keep single anchor point as SSoT while maintaining deserializer/factory compatibility
        Points = new List<ChartPoint> { start };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    /// <summary>
    /// Generates infinite sequence of Ichimoku time numbers according to composite formula T = A + B - 1 in Y:\Temp\webai.txt.
    /// Consecutive Grand Cycle (676) periods overlap by 1 day (offset increment = 675).
    /// </summary>
    public static IEnumerable<int> EnumerateNumbers()
    {
        int cycleBase = 0;
        while (true)
        {
            foreach (var n in BaseNumbers)
            {
                yield return cycleBase + n;
            }
            // Compound cycle rule according to webai.txt: T = A + B - 1
            // Adding a 676-period cycle subtracts 1 overlapping boundary day: (676 - 1) = 675
            cycleBase += 676 - 1;
        }
    }

    public static IReadOnlyList<int> GetNumbersUpTo(int maxDay)
    {
        var list = new List<int>();
        foreach (var n in EnumerateNumbers())
        {
            if (n > maxDay) break;
            list.Add(n);
        }
        return list;
    }

    public IReadOnlyList<int> GetNumbers()
    {
        return BaseNumbers;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 1) return;

        var p1 = Points[0];
        var bounds = canvas.LocalClipBounds;

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0),
            Style = SKPaintStyle.Stroke
        };

        using var textPaint = new SKPaint
        {
            Color = SkiaColor,
            IsAntialias = true,
            TextSize = DrawingThemeContext.DrawingFontSize
        };

        // Origin Bar (Day 1: inclusive start, offset = 0)
        float startX = (float)DrawingMath.GetScreenXFromOffsetDays(p1, 0, transform);
        if (startX >= bounds.Left && startX <= bounds.Right)
        {
            canvas.DrawLine(startX, bounds.Top, startX, bounds.Bottom, paint);
            if (ShowLabels)
            {
                canvas.DrawText("1", startX + 2, bounds.Bottom - 5, textPaint);
            }
        }

        // Project future bars by calculating numbers automatically beyond 676
        int count = 0;
        foreach (var n in EnumerateNumbers())
        {
            if (++count > 5000) break; // Safety limit
            int offsetDays = n - 1;
            float x = (float)DrawingMath.GetScreenXFromOffsetDays(p1, offsetDays, transform);

            if (x >= bounds.Left && x <= bounds.Right)
            {
                canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
                if (ShowLabels)
                {
                    canvas.DrawText($"{n}", x + 2, bounds.Bottom - 5, textPaint);
                }
            }
            else if (x > bounds.Right)
            {
                // Screen coordinates go left to right; break once beyond right boundary
                break;
            }
        }

        if (IsSelected && Points.Count > 0)
        {
            var p1Screen = transform.ChartToScreen(Points[0]);
            SelectionHandleRenderer.Draw(canvas, p1Screen, DrawingThemeContext.AnchorPointColor, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 1 || transform == null) return false;

        var p1 = Points[0];
        float startX = (float)DrawingMath.GetScreenXFromOffsetDays(p1, 0, transform);
        if (Math.Abs(screenPoint.X - startX) <= tolerance) return true;

        int count = 0;
        foreach (var n in EnumerateNumbers())
        {
            if (++count > 5000) break;
            int offsetDays = n - 1;
            float x = (float)DrawingMath.GetScreenXFromOffsetDays(p1, offsetDays, transform);
            if (Math.Abs(screenPoint.X - x) <= tolerance) return true;
            if (x > screenPoint.X + tolerance) break;
        }

        return false;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < 1) return Array.Empty<DrawingCalculatedValue>();

        var p1 = Points[0];
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        double elapsedSeconds = (timestamp - p1.Time).TotalSeconds;
        double elapsedDays = elapsedSeconds / 86400.0;

        int nextNumber = -1;
        DateTime nextTime = DateTime.MinValue;

        int count = 0;
        foreach (var n in EnumerateNumbers())
        {
            if (++count > 5000) break;
            DateTime t = DrawingMath.SafeAddSeconds(p1.Time, (n - 1) * 86400.0);
            if (t >= timestamp)
            {
                nextNumber = n;
                nextTime = t;
                break;
            }
        }

        var results = new List<DrawingCalculatedValue>
        {
            new DrawingCalculatedValue("OriginDate", "Origin Date", null, p1.Time.ToString("yyyy-MM-dd"), color),
            new DrawingCalculatedValue("Elapsed", "Elapsed", (decimal)Math.Round(elapsedDays, 1), $"{elapsedDays:F1} days", IndicatorColor.Gray)
        };

        if (nextNumber > 0)
        {
            results.Add(new DrawingCalculatedValue("NextCycle", $"Next Target ({nextNumber})", null, nextTime.ToString("yyyy-MM-dd"), color));
        }

        return results;
    }
}
