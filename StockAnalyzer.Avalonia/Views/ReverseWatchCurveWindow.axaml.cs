using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views;

/// <summary>
/// Avalonia Window for Reverse Watch Curve chart.
/// Uses a dedicated ChartPanel control for SkiaSharp rendering
/// to ensure correct coordinate space alignment.
/// </summary>
public partial class ReverseWatchCurveWindow : Window
{
    private ReverseWatchCurveData? _data;
    private ReverseWatchCurvePoint? _hoveredPoint;
    private global::Avalonia.Point _mousePosition;
    private ScaleContext? _lastBuildScale;
    private DateTime _lastHoverUpdate = DateTime.MinValue;
    private readonly ChartPanel _chartPanel;

    // Constants
    private const float MARGIN = 60f;
    private const float TIME_AXIS_HEIGHT = 25f;
    private const float POINT_RADIUS = 4f;
    private const float HOVER_POINT_RADIUS = 6f;
    private const float HOVER_THRESHOLD_DISTANCE = 15f;
    private const int HOVER_THROTTLE_MS = 16;

    public ReverseWatchCurveWindow()
    {
        InitializeComponent();
        
        // Create a dedicated rendering control and insert at index 0
        // so it renders behind the header text elements
        _chartPanel = new ChartPanel(this);
        MainGrid.Children.Insert(0, _chartPanel);
        
        _chartPanel.PointerMoved += OnPointerMoved;
        _chartPanel.PointerExited += OnPointerExited;
    }

    /// <summary>
    /// Sets the chart data and triggers a redraw.
    /// </summary>
    public void SetData(ReverseWatchCurveData data)
    {
        _data = data;
        _lastBuildScale = null;
        _hoveredPoint = null;

        PeriodLabel.Text = $"Period: {data.Period} days";
        
        // Initial Header Update (Show Latest Data)
        if (_data.Points.Count > 0)
        {
            UpdateHeaderInfo(_data.Points[^1]);
        }
        
        _chartPanel.InvalidateVisual();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_data == null) return;

        var now = DateTime.Now;
        if ((now - _lastHoverUpdate).TotalMilliseconds < HOVER_THROTTLE_MS) return;
        _lastHoverUpdate = now;

        // Get position relative to the chart panel (same coordinate space as rendering)
        _mousePosition = e.GetPosition(_chartPanel);
        
        var chartWidth = (float)_chartPanel.Bounds.Width - 2 * MARGIN;
        var chartHeight = (float)_chartPanel.Bounds.Height - 2 * MARGIN - TIME_AXIS_HEIGHT;
        
        var scale = CalculateScaleContext(chartWidth, chartHeight);
        if (!scale.IsValid) return;
        
        _lastBuildScale = scale;
        var nearestPoint = LinearSearchNearestPoint(_mousePosition, scale);

        if (nearestPoint != _hoveredPoint)
        {
            _hoveredPoint = nearestPoint;
            UpdateHoverInfo(_mousePosition);
            _chartPanel.InvalidateVisual();
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        _hoveredPoint = null;
        
        // Reset Header to Latest on Exit
        if (_data != null && _data.Points.Count > 0)
        {
            UpdateHeaderInfo(_data.Points[^1]);
        }
        
        _chartPanel.InvalidateVisual();
    }

    private ScaleContext CalculateScaleContext(float chartWidth, float chartHeight)
    {
        if (_data == null) return ScaleContext.Invalid;

        var volumeRange = _data.Bounds.MaxVolume - _data.Bounds.MinVolume;
        var priceRange = _data.Bounds.MaxPrice - _data.Bounds.MinPrice;

        if (volumeRange <= 0m || priceRange <= 0m)
            return ScaleContext.Invalid;

        return new ScaleContext
        {
            ChartWidth = chartWidth,
            ChartHeight = chartHeight,
            VolumeRange = volumeRange,
            PriceRange = priceRange,
            MinVolume = _data.Bounds.MinVolume,
            MinPrice = _data.Bounds.MinPrice,
            MaxVolume = _data.Bounds.MaxVolume,
            MaxPrice = _data.Bounds.MaxPrice,
            IsValid = true
        };
    }

    private ReverseWatchCurvePoint? LinearSearchNearestPoint(global::Avalonia.Point mousePos, ScaleContext scale)
    {
        if (_data == null) return null;

        ReverseWatchCurvePoint? nearestPoint = null;
        float minDistance = HOVER_THRESHOLD_DISTANCE;

        foreach (var point in _data.Points)
        {
            var coords = ToScreenCoordinates(point, scale);
            var dx = (float)mousePos.X - coords.X;
            var dy = (float)mousePos.Y - coords.Y;
            var distance = (float)Math.Sqrt(dx * dx + dy * dy);

            // Output debug for nearest point testing
            // System.Diagnostics.Debug.WriteLine($"[ReverseWatch Check Hover] dist: {distance}, pos: {coords.X}, {coords.Y}");
            if (distance < minDistance)
            {
                nearestPoint = point;
                minDistance = distance;
            }
        }
        return nearestPoint;
    }

    private static ScreenCoordinates ToScreenCoordinates(ReverseWatchCurvePoint point, ScaleContext scale)
    {
        var volumeRatio = ClampDecimal((point.VolumeAverage - scale.MinVolume) / scale.VolumeRange);
        var priceRatio = ClampDecimal((point.PriceAverage - scale.MinPrice) / scale.PriceRange);

        return new ScreenCoordinates
        {
            X = (float)((decimal)MARGIN + volumeRatio * (decimal)scale.ChartWidth),
            Y = (float)((decimal)(scale.ChartHeight + MARGIN) - priceRatio * (decimal)scale.ChartHeight)
        };
    }

    private static decimal ClampDecimal(decimal value, decimal min = 0m, decimal max = 1m)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private void UpdateHoverInfo(global::Avalonia.Point position)
    {
        // No display needed as per user request
    }

    private void UpdateHeaderInfo(ReverseWatchCurvePoint p)
    {
        // No display needed as per user request
    }

    // Inner Types
    public readonly struct ScaleContext
    {
        public float ChartWidth { get; init; }
        public float ChartHeight { get; init; }
        public decimal VolumeRange { get; init; }
        public decimal PriceRange { get; init; }
        public decimal MinVolume { get; init; }
        public decimal MinPrice { get; init; }
        public decimal MaxVolume { get; init; }
        public decimal MaxPrice { get; init; }
        public bool IsValid { get; init; }
        public static ScaleContext Invalid => new() { IsValid = false };
    }

    private readonly struct ScreenCoordinates
    {
        public float X { get; init; }
        public float Y { get; init; }
    }

    /// <summary>
    /// Dedicated control for SkiaSharp chart rendering.
    /// Using a child Control ensures Bounds and pointer coordinates
    /// are in the same local coordinate space (fixes DPI/offset issues).
    /// </summary>
    private sealed class ChartPanel : Control
    {
        private readonly ReverseWatchCurveWindow _owner;

        public ChartPanel(ReverseWatchCurveWindow owner)
        {
            _owner = owner;
            // Fill the entire parent area
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
            // Enable hit testing for pointer events
            IsHitTestVisible = true;
            // PREVENT Overflow rendering
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_owner._data != null && _owner._data.Points.Count > 0)
            {
                var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                var themeManager = App.Current.Services?.GetService<IThemeManager>();
                var themeColors = themeManager?.CurrentTheme ?? ThemeColors.Dark;
                // Bounds are in logical pixels. Using Size to ignore any Control X/Y layout offsets
                // since Skia canvas (Inner operation) is strictly Local Coordinates.
                var logicalBounds = new global::Avalonia.Rect(new global::Avalonia.Point(0, 0), Bounds.Size);
                
                context.Custom(new ChartDrawOperation(
                    logicalBounds, _owner._data, _owner._hoveredPoint, _owner._mousePosition, themeColors, renderScaling));
            }
        }
    }

    /// <summary>
    /// Custom draw operation for SkiaSharp rendering
    /// </summary>
    private class ChartDrawOperation : ICustomDrawOperation
    {
        private readonly global::Avalonia.Rect _bounds;
        private readonly ReverseWatchCurveData _data;
        private readonly ReverseWatchCurvePoint? _hoveredPoint;
        private readonly global::Avalonia.Point _mousePosition;
        private readonly ThemeColors _themeColors;
        private readonly double _renderScaling;

        private const float MARGIN = 60f;
        private const float TIME_AXIS_HEIGHT = 25f;
        private const float POINT_RADIUS = 4f;
        private const float HOVER_POINT_RADIUS = 6f;

        public ChartDrawOperation(global::Avalonia.Rect bounds, ReverseWatchCurveData data, 
            ReverseWatchCurvePoint? hoveredPoint, global::Avalonia.Point mousePosition, 
            ThemeColors themeColors, double renderScaling = 1.0)
        {
            _bounds = bounds;
            _data = data;
            _hoveredPoint = hoveredPoint;
            _mousePosition = mousePosition;
            _themeColors = themeColors;
            _renderScaling = renderScaling;
        }

        public global::Avalonia.Rect Bounds => _bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public override bool Equals(object? obj) => Equals(obj as ICustomDrawOperation);
        public override int GetHashCode() => _bounds.GetHashCode();
        
        // Hover pointer coordinates (p) come from Avalonia in logical pixels.
        // Compare directly with logical _bounds.
        public bool HitTest(global::Avalonia.Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (feature == null) return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            
            canvas.Save();
            
            // Reverted manual `canvas.Scale(scaleF, scaleF)` as Avalonia 11 provides a pre-scaled
            // canvas lease on Windows/WPF hosts. Applying it manually causes double-scaling.
            
            // Width and Height for our drawing logic must be the LOGICAL bounds,
            // because our calculations use the Avalonia logical sizes from layout.
            var logicalWidth = (float)_bounds.Width;
            var logicalHeight = (float)_bounds.Height;

            using (var bgPaint = new SKPaint { Color = _themeColors.RwChartBackground.ToSkColor(), Style = SKPaintStyle.Fill })
            {
                canvas.DrawRect(0, 0, logicalWidth, logicalHeight, bgPaint);
            }
            
            var chartWidth = logicalWidth - 2 * MARGIN;
            var chartHeight = logicalHeight - 2 * MARGIN - TIME_AXIS_HEIGHT;

            var scale = CalculateScale(chartWidth, chartHeight);
            if (!scale.IsValid) {
                canvas.Restore();
                return;
            }

            DrawGrid(canvas, logicalWidth, logicalHeight);
            DrawAxes(canvas, logicalWidth, logicalHeight);
            DrawTimeAxis(canvas, logicalWidth, logicalHeight);
            DrawCurve(canvas, scale);

            if (_hoveredPoint != null)
                DrawHoverPoint(canvas, scale);
                
            canvas.Restore();
        }

        private ScaleContext CalculateScale(float chartWidth, float chartHeight)
        {
            var volumeRange = _data.Bounds.MaxVolume - _data.Bounds.MinVolume;
            var priceRange = _data.Bounds.MaxPrice - _data.Bounds.MinPrice;

            if (volumeRange <= 0m || priceRange <= 0m)
                return ScaleContext.Invalid;

            return new ScaleContext
            {
                ChartWidth = chartWidth,
                ChartHeight = chartHeight,
                VolumeRange = volumeRange,
                PriceRange = priceRange,
                MinVolume = _data.Bounds.MinVolume,
                MinPrice = _data.Bounds.MinPrice,
                MaxVolume = _data.Bounds.MaxVolume,
                MaxPrice = _data.Bounds.MaxPrice,
                IsValid = true
            };
        }

        private void DrawGrid(SKCanvas canvas, float width, float height)
        {
            using var gridPaint = new SKPaint
            {
                Color = _themeColors.RwGridLine.ToSkColor(),
                StrokeWidth = 0.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };

            var chartWidth = width - 2 * MARGIN;
            var chartHeight = height - 2 * MARGIN - TIME_AXIS_HEIGHT;

            for (int i = 1; i < 5; i++)
            {
                var x = MARGIN + chartWidth * i / 5;
                canvas.DrawLine(x, MARGIN, x, height - MARGIN - TIME_AXIS_HEIGHT, gridPaint);
            }

            for (int i = 1; i < 5; i++)
            {
                var y = MARGIN + chartHeight * i / 5;
                canvas.DrawLine(MARGIN, y, width - MARGIN, y, gridPaint);
            }
        }

        private void DrawAxes(SKCanvas canvas, float width, float height)
        {
            using var axisPaint = new SKPaint
            {
                Color = _themeColors.RwAxisLine.ToSkColor(),
                StrokeWidth = 1,
                IsAntialias = true
            };
            using var textPaint = new SKPaint
            {
                Color = _themeColors.RwAxisText.ToSkColor(),
                IsAntialias = true
            };
            using var textFont = new SKFont(SKTypeface.Default, 12);

            var axisY = height - MARGIN - TIME_AXIS_HEIGHT;
            canvas.DrawLine(MARGIN, axisY, width - MARGIN, axisY, axisPaint);
            canvas.DrawLine(MARGIN, MARGIN, MARGIN, axisY, axisPaint);

            // X axis label
            var xLabel = "Volume Average";
            var xLabelWidth = textPaint.MeasureText(xLabel);
            canvas.DrawText(xLabel, (width - xLabelWidth) / 2, height - 5, textFont, textPaint);

            // Y axis label (rotated)
            canvas.Save();
            canvas.RotateDegrees(-90, 15, height / 2);
            var yLabel = "Price Average";
            var yLabelWidth = textPaint.MeasureText(yLabel);
            canvas.DrawText(yLabel, 15, height / 2 + yLabelWidth / 2, textFont, textPaint);
            canvas.Restore();

            // Price scale labels
            var minPriceText = _data.Bounds.MinPrice.ToString("N0");
            var maxPriceText = _data.Bounds.MaxPrice.ToString("N0");
            canvas.DrawText(minPriceText, MARGIN + 5, axisY - 5, textFont, textPaint);
            canvas.DrawText(maxPriceText, MARGIN + 5, MARGIN + 15, textFont, textPaint);

            DrawColorLegend(canvas, width, textFont, textPaint);
        }

        private void DrawTimeAxis(SKCanvas canvas, float width, float height)
        {
            using var textPaint = new SKPaint
            {
                Color = _themeColors.RwAxisText.ToSkColor(),
                IsAntialias = true
            };
            using var textFont = new SKFont(SKTypeface.Default, 12);

            var axisY = height - MARGIN - TIME_AXIS_HEIGHT;
            
            var minVolText = _data.Bounds.MinVolume.ToString("N0");
            var maxVolText = _data.Bounds.MaxVolume.ToString("N0");
            canvas.DrawText(minVolText, MARGIN, axisY + 14, textFont, textPaint);
            var maxVolWidth = textPaint.MeasureText(maxVolText);
            canvas.DrawText(maxVolText, width - MARGIN - maxVolWidth, axisY + 14, textFont, textPaint);

            var dateY = height - MARGIN + 10;
            var startDate = _data.Points[0].Date.ToString("MM/dd");
            var endDate = _data.Points[^1].Date.ToString("MM/dd");

            canvas.DrawText($"Start: {startDate}", MARGIN + 5, dateY, textFont, textPaint);
            var endDateText = $"End: {endDate}";
            var endDateWidth = textPaint.MeasureText(endDateText);
            canvas.DrawText(endDateText, width - MARGIN - endDateWidth - 5, dateY, textFont, textPaint);
        }

        private void DrawColorLegend(SKCanvas canvas, float width, SKFont textFont, SKPaint textPaint)
        {
            using var pointPaint = new SKPaint { IsAntialias = true };

            var legendX = width - 120;
            var legendY = 50f;

            canvas.DrawText("Old", legendX, legendY, textFont, textPaint);
            canvas.DrawText("New", legendX + 95, legendY, textFont, textPaint);

            var colors = new[] { 
                SKColor.FromHsv(240, 80, 80),
                SKColor.FromHsv(180, 80, 80),
                SKColor.FromHsv(120, 80, 80),
                SKColor.FromHsv(60, 80, 80),
                SKColor.FromHsv(0, 80, 80)
            };

            for (int i = 0; i < colors.Length; i++)
            {
                pointPaint.Color = colors[i];
                canvas.DrawCircle(legendX + 20 + i * 16, legendY - 4, 5, pointPaint);
            }
        }

        private void DrawCurve(SKCanvas canvas, ScaleContext scale)
        {
            using var pointPaint = new SKPaint { IsAntialias = true };
            using var pathPaint = new SKPaint
            {
                Color = _themeColors.RwCurvePath.ToSkColor(),
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };

            using var path = new SKPath();
            var pointCount = _data.Points.Count;
            var maxIndex = Math.Max(pointCount - 1, 1);

            for (int i = 0; i < pointCount; i++)
            {
                var point = _data.Points[i];
                var coords = ToScreenCoordinates(point, scale);

                if (i == 0) path.MoveTo(coords.X, coords.Y);
                else path.LineTo(coords.X, coords.Y);

                var colorRatio = (float)i / maxIndex;
                pointPaint.Color = SKColor.FromHsv(240 * (1 - colorRatio), 80, 80);
                canvas.DrawCircle(coords.X, coords.Y, POINT_RADIUS, pointPaint);
            }
            canvas.DrawPath(path, pathPaint);
        }

        private void DrawHoverPoint(SKCanvas canvas, ScaleContext scale)
        {
            if (_hoveredPoint == null) return;
            
            using var hoverPaint = new SKPaint
            {
                Color = _themeColors.RwHoverPoint.ToSkColor(),
                StrokeWidth = 2,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            
            var coords = ToScreenCoordinates(_hoveredPoint, scale);
            canvas.DrawCircle(coords.X, coords.Y, HOVER_POINT_RADIUS, hoverPaint);
        }

        private static ScreenCoordinates ToScreenCoordinates(ReverseWatchCurvePoint point, ScaleContext scale)
        {
            float volumeRatio = (float)ClampDecimal((point.VolumeAverage - scale.MinVolume) / scale.VolumeRange);
            float priceRatio = (float)ClampDecimal((point.PriceAverage - scale.MinPrice) / scale.PriceRange);

            // X = MARGIN + (Ratio * ChartWidth)
            // Y = (ChartHeight + MARGIN) - (Ratio * ChartHeight) # Y is inverted (0 is top)
            return new ScreenCoordinates
            {
                X = MARGIN + (volumeRatio * scale.ChartWidth),
                Y = (scale.ChartHeight + MARGIN) - (priceRatio * scale.ChartHeight)
            };
        }

        private static decimal ClampDecimal(decimal value, decimal min = 0m, decimal max = 1m)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
