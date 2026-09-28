using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

public enum CyclicLinesCustomMode
{
    AnchorForwardDays = 0,
    TwoPointRange = 1
}

/// <summary>
/// Cyclic Lines Tool
/// Draws vertical lines at regular time intervals.
/// </summary>
public class CyclicLinesObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.CyclicLines;

    public List<ChartPoint> Points { get; private set; }
    private double GetDistance(global::Avalonia.Point a, global::Avalonia.Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;
    public const string DefaultCustomRangeText = "1, 4, 9, 16, 25, 36, 49, 64, 81, 100, 121, 144, 169, 196, 225, 256, 289, 324, 361, 400";
    public bool IsCustomRangeEnabled { get; set; } = false;

    private string _customRangeText = DefaultCustomRangeText;
    private IReadOnlyList<double> _cachedMultipliers = DefaultSquares;

    public string CustomRangeText
    {
        get => _customRangeText;
        set
        {
            _customRangeText = value ?? string.Empty;
            _cachedMultipliers = ParseCustomIntervalMultipliers(_customRangeText);
        }
    }

    public CyclicLinesCustomMode CustomMode { get; set; } = CyclicLinesCustomMode.AnchorForwardDays;

    private static readonly float[] DashPattern = { 5, 5 };
    private static readonly SKPathEffect DashEffect = SKPathEffect.CreateDash(DashPattern, 0);

    public CyclicLinesObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        using var paint = new SKPaint
        {
            Color = SkiaColor,
            StrokeWidth = (float)Thickness,
            IsAntialias = true,
            PathEffect = DashEffect,
            Style = SKPaintStyle.Stroke
        };

        // Calculate Pixel Interval
        float startX = (float)p1.X;
        float intervalX = Math.Abs((float)(p2.X - p1.X));

        var bounds = canvas.LocalClipBounds;

        if (IsCustomRangeEnabled)
        {
            var multipliers = GetCustomIntervalMultipliers();

            using var textPaint = new SKPaint
            {
                Color = SkiaColor,
                IsAntialias = true,
                TextSize = DrawingThemeContext.DrawingFontSize
            };

            if (CustomMode == CyclicLinesCustomMode.AnchorForwardDays)
            {
                // Draw anchor line (offset = 0)
                if (startX >= bounds.Left && startX <= bounds.Right)
                {
                    canvas.DrawLine(startX, bounds.Top, startX, bounds.Bottom, paint);
                }

                foreach (var d in multipliers)
                {
                    float x = (float)DrawingMath.GetScreenXFromOffsetDays(Points[0], d, transform);
                    if (x >= bounds.Left && x <= bounds.Right)
                    {
                        canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
                        canvas.DrawText($"{d}", x + 2, bounds.Bottom - 5, textPaint);
                    }
                }
            }
            else // TwoPointRange
            {
                float step = (float)(p2.X - p1.X);
                if (Math.Abs(step) < 1f) return;

                // Draw anchor line
                if (startX >= bounds.Left && startX <= bounds.Right)
                {
                    canvas.DrawLine(startX, bounds.Top, startX, bounds.Bottom, paint);
                }

                foreach (var d in multipliers)
                {
                    float x = startX + (float)(d * step);
                    if (x >= bounds.Left && x <= bounds.Right)
                    {
                        canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
                        canvas.DrawText($"{d}", x + 2, bounds.Bottom - 5, textPaint);
                    }
                }
            }
        }
        else
        {
            if (intervalX < 5f) return;

            // Draw Forward
            float x = startX;
            int count = 0;
            while (count < 1000) // Safety break
            {
                if (x >= bounds.Left && x <= bounds.Right)
                {
                    canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
                }
                else if (x > bounds.Right)
                {
                    break;
                }
                x += intervalX;
                count++;
            }

            // Draw Backward
            x = startX - intervalX;
            count = 0;
            while (count < 1000)
            {
                if (x >= bounds.Left && x <= bounds.Right)
                {
                    canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, paint);
                }
                else if (x < bounds.Left)
                {
                    break;
                }
                x -= intervalX;
                count++;
            }
        }

        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    public static readonly double[] DefaultSquares = { 1, 4, 9, 16, 25, 36, 49, 64, 81, 100, 121, 144, 169, 196, 225, 256, 289, 324, 361, 400 };
    public static readonly double[] DefaultPrimes = DefaultSquares;

    public IReadOnlyList<double> GetCustomIntervalMultipliers() => _cachedMultipliers;

    private static IReadOnlyList<double> ParseCustomIntervalMultipliers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DefaultSquares;

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<double>(parts.Length);
        foreach (var part in parts)
        {
            if (double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                result.Add(val);
            }
        }
        return result.Count > 0 ? result.ToArray() : DefaultSquares;
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 1 || transform == null) return false;

        if (IsCustomRangeEnabled)
        {
            if (CustomMode == CyclicLinesCustomMode.AnchorForwardDays)
            {
                float startX = (float)DrawingMath.GetScreenXFromOffsetDays(Points[0], 0, transform);
                if (Math.Abs(screenPoint.X - startX) <= tolerance) return true;

                var multipliers = GetCustomIntervalMultipliers();
                foreach (var d in multipliers)
                {
                    float x = (float)DrawingMath.GetScreenXFromOffsetDays(Points[0], d, transform);
                    if (Math.Abs(screenPoint.X - x) <= tolerance) return true;
                }
                return false;
            }
            else // TwoPointRange
            {
                if (Points.Count < 2) return false;
                var pt1 = transform.ChartToScreen(Points[0]);
                var pt2 = transform.ChartToScreen(Points[1]);
                float startX = (float)pt1.X;
                float step = (float)(pt2.X - pt1.X);
                if (Math.Abs(step) < 1f) return false;

                if (Math.Abs(screenPoint.X - startX) <= tolerance) return true;

                var multipliers = GetCustomIntervalMultipliers();
                foreach (var d in multipliers)
                {
                    float x = startX + (float)(d * step);
                    if (Math.Abs(screenPoint.X - x) <= tolerance) return true;
                }
                return false;
            }
        }

        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        float p1X = (float)p1.X;
        float intervalX = Math.Abs((float)(p2.X - p1.X));

        if (intervalX < 1f) return false;

        // Check if screenPoint.X is close to any p1X + k * intervalX
        float diff = (float)screenPoint.X - p1X;
        double roundK = Math.Round(diff / intervalX);
        float nearestX = p1X + (float)roundK * intervalX;

        return Math.Abs(screenPoint.X - nearestX) <= tolerance;
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
        if (Points.Count < 2) return Array.Empty<DrawingCalculatedValue>();

        var p1 = Points[0];
        var p2 = Points[1];
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        if (IsCustomRangeEnabled)
        {
            var multipliers = GetCustomIntervalMultipliers();
            double elapsedSeconds = (timestamp - p1.Time).TotalSeconds;
            double elapsedDays = elapsedSeconds / 86400.0;

            if (CustomMode == CyclicLinesCustomMode.AnchorForwardDays)
            {
                double nextMultiplier = -1;
                DateTime nextTargetTime = DateTime.MinValue;

                foreach (var d in multipliers)
                {
                    DateTime targetTime = DrawingMath.SafeAddSeconds(p1.Time, d * 86400.0);
                    if (targetTime >= timestamp)
                    {
                        nextMultiplier = d;
                        nextTargetTime = targetTime;
                        break;
                    }
                }

                var list = new List<DrawingCalculatedValue>
                {
                    new DrawingCalculatedValue("OriginDate", "Origin Date", null, p1.Time.ToString("yyyy-MM-dd"), color),
                    new DrawingCalculatedValue("Elapsed", "Elapsed", (decimal)Math.Round(elapsedDays, 1), $"{elapsedDays:F1} days", IndicatorColor.Gray)
                };

                if (nextMultiplier >= 0)
                {
                    list.Add(new DrawingCalculatedValue("NextTarget", $"Next Day (+{nextMultiplier})", null, nextTargetTime.ToString("yyyy-MM-dd"), color));
                }

                return list;
            }
            else // TwoPointRange
            {
                double intervalSeconds = Math.Abs((p2.Time - p1.Time).TotalSeconds);
                double intervalDays = intervalSeconds / 86400.0;
                double nextMultiplier = -1;
                DateTime nextTargetTime = DateTime.MinValue;

                foreach (var d in multipliers)
                {
                    DateTime targetTime = DrawingMath.SafeAddSeconds(p1.Time, d * intervalSeconds);
                    if (targetTime >= timestamp)
                    {
                        nextMultiplier = d;
                        nextTargetTime = targetTime;
                        break;
                    }
                }

                var list = new List<DrawingCalculatedValue>
                {
                    new DrawingCalculatedValue("Interval", "Base Interval", (decimal)Math.Round(intervalDays, 1), $"{intervalDays:F1} days", color),
                    new DrawingCalculatedValue("Elapsed", "Elapsed", (decimal)Math.Round(elapsedDays, 1), $"{elapsedDays:F1} days", IndicatorColor.Gray)
                };

                if (nextMultiplier >= 0)
                {
                    list.Add(new DrawingCalculatedValue("NextTarget", $"Next Multiple (x{nextMultiplier})", null, nextTargetTime.ToString("yyyy-MM-dd HH:mm"), color));
                }

                return list;
            }
        }

        double stdIntervalSeconds = Math.Abs((p2.Time - p1.Time).TotalSeconds);
        if (stdIntervalSeconds < 1.0) return Array.Empty<DrawingCalculatedValue>();

        double stdIntervalDays = stdIntervalSeconds / 86400.0;
        double stdElapsedSeconds = (timestamp - p1.Time).TotalSeconds;
        double cycleIndex = stdElapsedSeconds / stdIntervalSeconds;
        long nextCycleK = (long)Math.Ceiling(cycleIndex);
        long nearestCycleK = (long)Math.Round(cycleIndex);

        DateTime nextCycleTime = DrawingMath.SafeAddSeconds(p1.Time, nextCycleK * stdIntervalSeconds);
        DateTime nearestCycleTime = DrawingMath.SafeAddSeconds(p1.Time, nearestCycleK * stdIntervalSeconds);

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("Period", "Cycle Period", (decimal)Math.Round(stdIntervalDays, 1), $"{stdIntervalDays:F1} days", color),
            new DrawingCalculatedValue("CycleCount", "Cycle Count", (decimal)Math.Round(cycleIndex, 2), $"{cycleIndex:+0.00;-0.00;0.00}", IndicatorColor.Gray),
            new DrawingCalculatedValue("NextCycle", "Next Cycle", null, nextCycleTime.ToString("yyyy-MM-dd HH:mm"), color),
            new DrawingCalculatedValue("NearestCycle", "Nearest Cycle", null, nearestCycleTime.ToString("yyyy-MM-dd HH:mm"), IndicatorColor.Gray)
        };
    }
}

