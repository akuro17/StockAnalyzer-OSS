using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Skia;
using Avalonia.Rendering.SceneGraph;
using SkiaSharp;
using System;
using System.Linq;

namespace StockAnalyzer.Avalonia.Views.Spikes;

public class SkiaGraphControl : Control
{
    public static readonly StyledProperty<SkiaSharpPrototypeViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<SkiaGraphControl, SkiaSharpPrototypeViewModel?>(nameof(ViewModel));

    public SkiaSharpPrototypeViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (ViewModel == null || !ViewModel.Candles.Any())
        {
            return;
        }

        context.Custom(new ChartDrawOperation(Bounds, ViewModel));
    }

    private class ChartDrawOperation : ICustomDrawOperation
    {
        private readonly global::Avalonia.Rect _bounds;
        private readonly SkiaSharpPrototypeViewModel _viewModel;

        public ChartDrawOperation(global::Avalonia.Rect bounds, SkiaSharpPrototypeViewModel viewModel)
        {
            _bounds = bounds;
            _viewModel = viewModel;
        }

        public global::Avalonia.Rect Bounds => _bounds;

        public void Dispose()
        {
            // No resources to dispose
        }

        public bool Equals(ICustomDrawOperation? other)
        {
            return other is ChartDrawOperation op && op._bounds == _bounds && op._viewModel == _viewModel;
        }

        public override bool Equals(object? obj) => Equals(obj as ICustomDrawOperation);
        public override int GetHashCode() => HashCode.Combine(_bounds, _viewModel);

        public bool HitTest(global::Avalonia.Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            
            if (feature == null)
            {
                return;
            }

            using (var lease = feature.Lease())
            {
                var canvas = lease.SkCanvas;
                var width = (float)_bounds.Width;
                var height = (float)_bounds.Height;

                canvas.Save();
                // Move to the control's position if necessary, but typically with CustomDrawOperation 
                // we might need to handle offset if not using standard transforms.
                // However, Avalonia should set up the matrix.
                // NOTE: ImmediateDrawingContext might imply we are drawing in global coordinates or local?
                // Usually it's local.

                DrawChart(canvas, width, height, _viewModel);

                canvas.Restore();
            }
        }

        private void DrawChart(SKCanvas canvas, float width, float height, SkiaSharpPrototypeViewModel viewModel)
        {
            float margin = 20;
            float candleWidth = (width - 2 * margin) / viewModel.Candles.Count;
            decimal maxPrice = viewModel.Candles.Max(c => c.High);
            decimal minPrice = viewModel.Candles.Min(c => c.Low);
            decimal priceRange = maxPrice - minPrice;
            
            if (priceRange == 0) priceRange = 1;

            using var greenPaint = new SKPaint { Color = SKColors.Green, Style = SKPaintStyle.Fill };
            using var redPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            using var wickPaint = new SKPaint { Color = SKColors.Black, StrokeWidth = 1 };

            float GetY(decimal price)
            {
                return height - margin - (float)((price - minPrice) / priceRange) * (height - 2 * margin);
            }

            for (int i = 0; i < viewModel.Candles.Count; i++)
            {
                var candle = viewModel.Candles[i];
                float x = margin + i * candleWidth;
                float centerX = x + candleWidth / 2;

                float yOpen = GetY(candle.Open);
                float yClose = GetY(candle.Close);
                float yHigh = GetY(candle.High);
                float yLow = GetY(candle.Low);

                // Draw Wick
                canvas.DrawLine(centerX, yHigh, centerX, yLow, wickPaint);

                // Draw Body
                var bodyPaint = candle.Close >= candle.Open ? greenPaint : redPaint;
                float top = Math.Min(yOpen, yClose);
                float bottom = Math.Max(yOpen, yClose);
                float bodyHeight = Math.Max(1, bottom - top);

                canvas.DrawRect(x + 1, top, candleWidth - 2, bodyHeight, bodyPaint);
            }
        }
    }
}
