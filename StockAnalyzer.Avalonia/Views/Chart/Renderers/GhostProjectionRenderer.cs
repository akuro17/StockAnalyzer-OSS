using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

public sealed class GhostProjectionRenderer : System.IDisposable
{
    private readonly SKPaint _bullishProjectionPaint;
    private readonly SKPaint _bearishProjectionPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _markerPaint;
    private readonly SKPaint _markerShadowPaint;
    private readonly SKPaint _dashedProjectionPaint;
    private readonly SKPaint _bgPaint;
    private readonly SKPath _renderPath = new();
    private readonly SKPath _markerPath = new();

    public GhostProjectionRenderer()
    {
        _bullishProjectionPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        _bearishProjectionPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        _dashedProjectionPaint = new SKPaint 
        { 
            Style = SKPaintStyle.Stroke, 
            IsAntialias = true, 
            StrokeWidth = 1f,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4f, 4f }, 0)
        };
        _textPaint = new SKPaint 
        { 
            Color = SKColors.White, 
            IsAntialias = true,
            TextSize = 11,
            Typeface = SKTypeface.FromFamilyName("Arial")
        };
        _markerPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        _markerShadowPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.5f };
        _bgPaint = new SKPaint { Color = SKColors.Black.WithAlpha(180), Style = SKPaintStyle.Fill };
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
    {
        if (snapshot.MultiWaveSignals == null || snapshot.MultiWaveSignals.Count == 0 || config.Transform == null)
            return;

        _textPaint.TextSize = config.GhostProjectionFontSize;

        var theme = config.ThemeManager?.CurrentTheme ?? new Core.Theme.ThemeColors();
        var baseBullish = theme.Bullish;
        var baseBearish = theme.Bearish;
        
        var t = config.Transform;
        double x0 = t.ChartToScreen(new ChartPoint(new System.DateTime(0), 0)).X;
        double x1 = t.ChartToScreen(new ChartPoint(new System.DateTime(1), 0)).X;
        float columnWidth = (float)(x1 - x0);
        
        float mouseX = (float)config.MousePosition.X;
        float mouseY = (float)config.MousePosition.Y;
        
        var signals = snapshot.MultiWaveSignals;
        for (int s = 0; s < signals.Count; s++)
        {
            var signal = signals[s];
            // 1. Invalidation Check (Optimized O(1))
            if (signal.IsInvalidated) continue; // Do not project dead signals

            // 2. Viewport Optimization
            const int projectionLength = 15;
            int endIdx = signal.TriggerIndex + projectionLength;
            if (endIdx < snapshot.StartIndex) continue;
            if (signal.TriggerIndex > snapshot.StartIndex + snapshot.Candles.Count) continue;

            // 3. Dynamic Alpha based on Confidence
            byte alpha = (byte)(15 + (signal.ConfidenceScore * 40));
            var paint = signal.IsBullish ? _bullishProjectionPaint : _bearishProjectionPaint;
            var baseColor = signal.IsBullish ? baseBullish : baseBearish;
            paint.Color = baseColor.ToSkColor().WithAlpha(alpha);

            // 4. Calculate Common Coordinates
            // Anchor to the breakout occurrence position (TriggerIndex) as per user request.
            int displayIdx = signal.TriggerIndex;

            // WebAI Fix: Special handling for Kagi coordinate alignment (Multiple segments per column)
            double logicalTriggerIdx = config.ChartType == ChartType.Kagi ? snapshot.GetLogicalXIndex(signal.TriggerIndex, config) : signal.TriggerIndex;
            double logicalDisplayIdx = config.ChartType == ChartType.Kagi ? snapshot.GetLogicalXIndex(displayIdx, config) : displayIdx;

            float startX = GetXPosition(t, logicalDisplayIdx, config.ChartType, columnWidth, chartArea);

            decimal effectiveTriggerPrice = signal.TriggerPrice;
            decimal effectiveTargetMinPrice = signal.TargetPriceMin;
            decimal effectiveTargetMaxPrice = signal.TargetPriceMax;

            if (config.ChartType == ChartType.PointAndFigure || config.ChartType == ChartType.Renko || config.ChartType == ChartType.ThreeLineBreak)
            {
                // For block-based charts, prices should be centered vertically
                effectiveTriggerPrice += snapshot.MinBrickSize / 2m;
                effectiveTargetMinPrice += snapshot.MinBrickSize / 2m;
                effectiveTargetMaxPrice += snapshot.MinBrickSize / 2m;
            }

            float triggerY = (float)t.NumericToScreen(0, (double)effectiveTriggerPrice).Y + (float)chartArea.Top;
            float targetMinY = (float)t.NumericToScreen(0, (double)effectiveTargetMinPrice).Y + (float)chartArea.Top;
            float targetMaxY = (float)t.NumericToScreen(0, (double)effectiveTargetMaxPrice).Y + (float)chartArea.Top;

            // 5. Draw Breakout Marker at source
            float sourceX = GetXPosition(t, logicalTriggerIdx, config.ChartType, columnWidth, chartArea);
            float sourceY = triggerY;
            DrawMarker(canvas, sourceX, sourceY, signal.IsBullish, baseColor.ToSkColor());

            // 6. Draw Shape based on ChartType
            _renderPath.Reset();
            float labelX = startX;
            switch (config.ChartType)
            {
                case ChartType.PointAndFigure:
                    DrawPnfBox(_renderPath, startX, columnWidth, targetMinY, targetMaxY);
                    labelX = startX + columnWidth * 2f;
                    break;
                case ChartType.Kagi:
                    DrawKagiZone(_renderPath, startX, columnWidth, targetMinY, targetMaxY);
                    labelX = startX + columnWidth * 2.5f;
                    break;
                case ChartType.ThreeLineBreak:
                    DrawTlbBlock(_renderPath, startX, columnWidth, targetMinY, targetMaxY);
                    labelX = startX + columnWidth * 1.5f;
                    break;
                case ChartType.Renko:
                default:
                    float futureX = GetXPosition(t, displayIdx + projectionLength, config.ChartType, columnWidth, chartArea);
                    DrawRenkoCone(_renderPath, startX, futureX, triggerY, targetMinY, targetMaxY);
                    labelX = (startX + futureX) / 2f;
                    break;
            }

            canvas.DrawPath(_renderPath, paint);

            // 6.5 Draw Dashed Outline
            _dashedProjectionPaint.Color = baseColor.ToSkColor().WithAlpha((byte)(alpha + 40));
            canvas.DrawPath(_renderPath, _dashedProjectionPaint);

            // 7. Hit Testing for Hover
            bool isMouseOver = false;
            float markerSize = _textPaint.TextSize * 1.2f;
            if (_renderPath.Contains(mouseX, mouseY))
            {
                isMouseOver = true;
            }
            else
            {
                float dx = mouseX - sourceX;
                float dy = mouseY - sourceY;
                if (dx * dx + dy * dy <= markerSize * markerSize) isMouseOver = true;
            }

            // 8. Draw Purpose Label (Target Range)
            bool shouldShowLabel = !config.ShowGhostLabelsOnHoverOnly || isMouseOver;
            if (shouldShowLabel)
            {
                DrawTargetLabel(canvas, labelX, targetMinY, targetMaxY, signal, config.ChartType);
            }
        }
    }

    private float GetXPosition(ICoordinateTransform t, double idx, ChartType type, float columnWidth, Rect chartArea)
    {
        // WebAI Nuclear Fix: Use NumericToScreen for consistency
        float x = (float)t.NumericToScreen(idx, 0).X + (float)chartArea.Left;
        switch (type)
        {
            case ChartType.Kagi:
            case ChartType.PointAndFigure:
                // These renderers use exact coordinate as center/line
                return x;
            case ChartType.Renko:
            case ChartType.ThreeLineBreak:
            default:
                // These renderers add half-column offset for centering (bin-centric)
                return x + columnWidth / 2f;
        }
    }



    private void DrawMarker(SKCanvas canvas, float x, float y, bool isBullish, SKColor color)
    {
        _markerPaint.Color = color;
        _markerShadowPaint.Color = SKColors.Black.WithAlpha(128);
        
        float size = _textPaint.TextSize * 0.8f;
        _markerPath.Reset();
        if (isBullish)
        {
            // Points UP to the breakout price (Anchor at tip)
            _markerPath.MoveTo(x, y);
            _markerPath.LineTo(x + size * 0.6f, y + size);
            _markerPath.LineTo(x - size * 0.6f, y + size);
        }
        else
        {
            // Points DOWN to the breakout price (Anchor at tip)
            _markerPath.MoveTo(x, y);
            _markerPath.LineTo(x + size * 0.6f, y - size);
            _markerPath.LineTo(x - size * 0.6f, y - size);
        }
        _markerPath.Close();
        canvas.DrawPath(_markerPath, _markerPaint);
        canvas.DrawPath(_markerPath, _markerShadowPaint);
    }

    private void DrawTargetLabel(SKCanvas canvas, float x, float minY, float maxY, MultiWaveSignal signal, ChartType type)
    {
        string patternName = signal.DisplayPatternName ?? "Unknown Pattern";
        string priceText = signal.DisplayPriceRange ?? "N/A";
        
        float centerY = (minY + maxY) / 2f;
        float w1 = _textPaint.MeasureText(patternName);
        float w2 = _textPaint.MeasureText(priceText);
        float maxWidth = System.Math.Max(w1, w2);
        
        float textSize = _textPaint.TextSize;
        float textHeight = textSize * 2 + 2; // 2 lines + 2px gap
        var textBounds = new SKRect(x - maxWidth / 2f - 6, centerY - textHeight / 2f - 4, x + maxWidth / 2f + 6, centerY + textHeight / 2f + 4);
        
        canvas.DrawRoundRect(textBounds, 4, 4, _bgPaint);
        
        float startY = centerY - textHeight / 2f + textSize;
        canvas.DrawText(patternName, x - w1 / 2f, startY, _textPaint);
        canvas.DrawText(priceText, x - w2 / 2f, startY + textSize + 2, _textPaint);
    }

    private static void DrawRenkoCone(SKPath path, float startX, float futureX, float triggerY, float targetMinY, float targetMaxY)
    {
        path.MoveTo(startX, triggerY);
        path.LineTo(futureX, targetMinY);
        path.LineTo(futureX, targetMaxY);
        path.Close();
    }

    private static void DrawPnfBox(SKPath path, float startX, float columnWidth, float targetMinY, float targetMaxY)
    {
        // Flush with the RIGHT edge of the trigger column and extend 4 columns
        float left = startX + columnWidth / 2f;
        float right = left + columnWidth * 4f;
        path.AddRect(new SKRect(left, Math.Min(targetMinY, targetMaxY), right, Math.Max(targetMinY, targetMaxY)));
    }

    private static void DrawKagiZone(SKPath path, float startX, float columnWidth, float targetMinY, float targetMaxY)
    {
        // Flush with the Kagi line and extend 5 columns for future projection
        // Kagi startX is already at the line
        float left = startX;
        float right = startX + columnWidth * 5f;
        path.AddRect(new SKRect(left, Math.Min(targetMinY, targetMaxY), right, Math.Max(targetMinY, targetMaxY)));
    }

    private static void DrawTlbBlock(SKPath path, float startX, float columnWidth, float targetMinY, float targetMaxY)
    {
        // Flush with the RIGHT edge of the trigger column and extend 3 columns
        float left = startX + columnWidth / 2f;
        float right = left + columnWidth * 3f;
        path.AddRect(new SKRect(left, Math.Min(targetMinY, targetMaxY), right, Math.Max(targetMinY, targetMaxY)));
    }

    public void Dispose()
    {
        _bullishProjectionPaint.Dispose();
        _bearishProjectionPaint.Dispose();
        _textPaint.Dispose();
        _markerPaint.Dispose();
        _markerShadowPaint.Dispose();
        _dashedProjectionPaint.Dispose();
        _bgPaint.Dispose();
        _renderPath.Dispose();
        _markerPath.Dispose();
    }
}
