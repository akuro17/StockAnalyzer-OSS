using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Analysis; // For VolumeBin
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers
{
    public class VolumeProfileRenderer
    {
        private readonly SKPaint _barPaint;
        private readonly SKPaint _pocPaint;
        private readonly SKPaint _borderPaint;

        public VolumeProfileRenderer()
        {
            _barPaint = new SKPaint 
            { 
                Style = SKPaintStyle.Fill, 
                IsAntialias = true 
            };
            
            _pocPaint = new SKPaint 
            { 
                Style = SKPaintStyle.Stroke, 
                StrokeWidth = 2,
                IsAntialias = true 
            };

             _borderPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };
        }

        internal SKPaint BarPaint => _barPaint;
        internal SKPaint PocPaint => _pocPaint;
        internal SKPaint BorderPaint => _borderPaint;

        public void Render(SKCanvas canvas, Rect chartArea, List<VolumeBin> profile, 
            decimal minPrice, decimal maxPrice, ICoordinateTransform transform, bool isRightSide, IChartRenderConfig config,
            CoreIndicatorSettings? setting = null)
        {
            if (profile == null || !profile.Any() || transform == null) return;

            if (config != null)
            {
                var theme = config.ThemeManager.CurrentTheme;
                _barPaint.Color = theme.VolumeProfileFill.ToSkColor();
                _pocPaint.Color = theme.VolumeProfilePOC.ToSkColor();
                _borderPaint.Color = theme.VolumeProfileBorder.ToSkColor();
            }

            if (setting != null)
            {
                double opacity = 0.3;
                if (setting.ParameterObject is CoreVolumeProfileParameter vpParam)
                {
                    opacity = vpParam.Opacity;
                }
                byte alpha = (byte)Math.Clamp((int)(opacity * 255), 10, 255);
                byte borderAlpha = (byte)Math.Min(255, alpha + 60);

                var c = setting.Color;
                _barPaint.Color = new SKColor(c.R, c.G, c.B, alpha);
                _borderPaint.Color = new SKColor(c.R, c.G, c.B, borderAlpha);
            }

            // Find max volume for scaling
            long maxVol = profile.Max(b => b.TotalVolume);
            if (maxVol == 0) return;

            // Define Width of the profile display (e.g., 20% of chart width?)
            // Or fixed width? Let's use 100 pixels or 15% of chart.
            float profileWidth = (float)chartArea.Width * 0.2f;
            float startX = isRightSide ? (float)chartArea.Right - profileWidth : (float)chartArea.Left;

            // Draw Bars
            foreach (var bin in profile)
            {
                // Direct conversion from Price to screen Y coordinate via ICoordinateTransform
                float yTop = (float)transform.GetYFromPrice(bin.UpperBound) + (float)chartArea.Top;
                float yBottom = (float)transform.GetYFromPrice(bin.LowerBound) + (float)chartArea.Top;
                
                // Width based on volume
                float barLength = (bin.TotalVolume / (float)maxVol) * profileWidth;
                
                float x0 = isRightSide ? (float)chartArea.Right : (float)chartArea.Left;
                float x1 = isRightSide ? (float)chartArea.Right - barLength : (float)chartArea.Left + barLength;

                // Rect: normalize both horizontal and vertical bounds to guarantee valid geometry
                var rect = new SKRect(
                    Math.Min(x0, x1), 
                    Math.Min(yTop, yBottom), 
                    Math.Max(x0, x1), 
                    Math.Max(yTop, yBottom));
                
                // Draw
                canvas.DrawRect(rect, _barPaint);
                canvas.DrawRect(rect, _borderPaint);
            }

            // Draw POC (Line) - Optional, as the indicator also returns it as a main series.
            // But drawing it here reinforces the visual.
            // Let's explicitly draw POC line across the profile if meaningful.
            // Draw POC (Line) - Removed as per user request (Histogram only)
            /*
            var pocBin = profile.FirstOrDefault(b => b.TotalVolume == maxVol);
            if (pocBin != null)
            {
                float yPoc = (float)transform.ChartToScreen(new ChartPoint(DateTime.Now, pocBin.Price)).Y;
                 canvas.DrawLine((float)chartArea.Left, yPoc, (float)chartArea.Right, yPoc, _pocPaint);
            }
            */
        }
    }
}
