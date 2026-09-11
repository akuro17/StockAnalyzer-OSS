using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Constants;
using System.Linq;
using StockAnalyzer.Avalonia.Utilities;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

public sealed class PointAndFigureRenderer : IChartRenderer, IDisposable
{
    private readonly SKPaint _xPaint;
    private readonly SKPaint _oPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _signalPaint;
    private readonly SKPaint _dashPaint;
    private readonly SKPaint _bgPaint;
    private readonly SKPath _markerPath;

    public PointAndFigureRenderer()
    {
        _xPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = LayoutConstants.ThickStrokeWidth, IsAntialias = true, StrokeCap = SKStrokeCap.Square }; // Green X
        _oPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = LayoutConstants.ThickStrokeWidth, IsAntialias = true }; // Red O
        _textPaint = new SKPaint 
        { 
            IsAntialias = true, 
            TextSize = LayoutConstants.LabelFontSize, 
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) 
        };
        _signalPaint = new SKPaint
        {
            Style = SKPaintStyle.StrokeAndFill,
            IsAntialias = true,
            TextSize = LayoutConstants.LabelFontSize * 0.8f,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
        _dashPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
        };
        _bgPaint = new SKPaint { Style = SKPaintStyle.Fill };
        _markerPath = new SKPath();
    }

    public void Render(SKCanvas canvas, Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig baseConfig)
    {
        var config = (IPnfRenderConfig)baseConfig;

        if (snapshot.Candles.Count == 0 || config.PnfBoxSize <= 0) return;
        
        // Context Unpacking
        decimal boxSize = config.PnfBoxSize;
        int reversalAmount = config.PnfReversalAmount;

        // Safety check to avoid infinite loops if boxSize is messed up
        if (boxSize <= 0) boxSize = 1;

        if (config.Transform == null) return;
        var t = config.Transform;

        // Calculate Pixel Height for 1:1 Aspect Ratio (Needed for Clipping logic)
        float sampleY1 = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, snapshot.MinPrice)).Y + (float)chartArea.Top;
        float sampleY2 = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, snapshot.MinPrice + boxSize)).Y + (float)chartArea.Top;
        float boxPixelHeight = Math.Abs(sampleY1 - sampleY2);
        if (boxPixelHeight <= 0.1f) boxPixelHeight = 1f; // Safety

        // Clip canvas to chart area, but extend TOP and BOTTOM by one box height to prevent
        // clipping the highest/lowest box (works together with PnfPriceRangeCalculator's
        // snapped High+1box / Low margin so the full box is always visible).
        canvas.Save();
        canvas.ClipRect(new SKRect((float)chartArea.Left, (float)chartArea.Top - boxPixelHeight, (float)chartArea.Right, (float)chartArea.Bottom + boxPixelHeight));

        // Column width from the X-axis transform (matches the vertical grid spacing)
        float adjustedColumnWidth = (float)t.ScaleX;

        // Symbol size follows column width
        float symbolWidth = adjustedColumnWidth;

        var theme = config.ThemeManager.CurrentTheme;
        
        // Update paints with context colors
        _xPaint.Color = config.PnfBullishColor.ToSkColor();
        _oPaint.Color = config.PnfBearishColor.ToSkColor();
        _textPaint.Color = theme.AxisText.ToSkColor();

        bool drawAsLines = adjustedColumnWidth <= 2f;
        bool drawTrendCount = adjustedColumnWidth >= 15f;
        float lineStrokeWidth = Math.Max(1f, adjustedColumnWidth);

        if (drawAsLines)
        {
            _xPaint.Style = SKPaintStyle.Stroke;
            _xPaint.StrokeWidth = lineStrokeWidth;
            _oPaint.Style = SKPaintStyle.Stroke;
            _oPaint.StrokeWidth = lineStrokeWidth;
        }

        for (int i = 0; i < snapshot.Candles.Count; i++)
        {
            var col = snapshot.Candles[i];
            int absoluteIndex = snapshot.StartIndex + i;
            
            // Generate X based on absolute column index
            // ChartToScreen ALREADY returns the CENTER of the mapped index
            float centerX = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteIndex)).X + (float)chartArea.Left;

            // Column Range
            decimal high = col.High;
            decimal low = col.Low;
            
            // Determine if X or O column
            bool isUp = col.Close >= col.Open;
            
            float yTopMost = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, high + boxSize)).Y + (float)chartArea.Top;
            float yBottomMost = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, low)).Y + (float)chartArea.Top;

            if (drawAsLines)
            {
                // Draw as a vertical line segment for extreme zoom out
                SKPaint paint = isUp ? _xPaint : _oPaint;
                canvas.DrawLine(centerX, yTopMost, centerX, yBottomMost, paint);
            }
            else
            {
                // Snap low and high to the nearest box size multiple to eliminate rounding errors
                decimal snapLow = Math.Round(low / boxSize) * boxSize;
                decimal snapHigh = Math.Round(high / boxSize) * boxSize;

                // Calculate number of boxes strictly via snapped prices
                int boxCount = Math.Max(1, (int)Math.Round((snapHigh - snapLow) / boxSize) + 1);
                
                // X-radius from scaleX (column width), Y-radius from boxHeight (price scale)
                // This ensures marks align with both vertical and horizontal grid lines
                float columnHalfWidth = (float)t.ScaleX / 2f;
                
                for (int j = 0; j < boxCount; j++)
                {
                    decimal price = snapLow + j * boxSize;
                    
                    // Get absolute Y coordinate for the exact center of the box
                    float centerY = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, price + boxSize / 2m)).Y + (float)chartArea.Top;

                    float yTop = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, price + boxSize)).Y + (float)chartArea.Top;
                    float yBottom = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, price)).Y + (float)chartArea.Top;
                    float boxHeight = Math.Abs(yTop - yBottom);
                    
                    float radiusY = Math.Max(0.5f, boxHeight / 2f);
                    float radiusX = Math.Max(0.5f, columnHalfWidth);

                    // Dynamic stroke width based on smaller dimension
                    float minDim = Math.Min(boxHeight, (float)t.ScaleX);
                    float strokeWidth = Math.Clamp(minDim / 10f, 1f, 5f);
                    
                    _xPaint.StrokeWidth = strokeWidth;
                    _oPaint.StrokeWidth = strokeWidth;

                    if (isUp)
                    {
                        // Draw X - Use separate X/Y radii for proper grid alignment
                        canvas.DrawLine(centerX - radiusX, centerY - radiusY, centerX + radiusX, centerY + radiusY, _xPaint);
                        canvas.DrawLine(centerX + radiusX, centerY - radiusY, centerX - radiusX, centerY + radiusY, _xPaint);
                    }
                    else
                    {
                        // Draw O (Oval) - Expand slightly so tangent ovals eliminate white gaps
                        float oRadiusX = radiusX + (strokeWidth / 4f);
                        float oRadiusY = radiusY + (strokeWidth / 4f);
                        canvas.DrawOval(centerX, centerY, oRadiusX, oRadiusY, _oPaint);
                    }
                }
            }
        }

        // --- STEP 4: Advanced Pattern Engine Integration ---
        if (snapshot.PnfAnalysis.HasValue)
        {
            var analysis = snapshot.PnfAnalysis.Value;
            var signals = analysis.Signals;
            var activeLines = analysis.ActiveLines;

            DrawBreakoutSignals(canvas, chartArea, snapshot, config, t, boxSize, boxPixelHeight);

             DrawActiveTrendlines(canvas, chartArea, snapshot, config, t, boxSize);
         }

         if (snapshot.Candles.Count > 0 && drawTrendCount)
         {
             for (int i = 0; i < snapshot.Candles.Count; i++)
             {
                 var col = snapshot.Candles[i];
                 bool isUp = col.Close >= col.Open;
                 int trendCount = Math.Max(1, (int)Math.Round((double)((col.High - col.Low) / boxSize)) + 1);
                 int absoluteIndex = snapshot.StartIndex + i;
                 float centerX = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteIndex)).X + (float)chartArea.Left;

                 float columnWidth = (float)Math.Abs(t.ChartToScreen(new ChartPoint(DateTime.MinValue, 1)).X - t.ChartToScreen(new ChartPoint(DateTime.MinValue, 0)).X);
                 bool skipCountLabel = columnWidth < _textPaint.TextSize * 0.8f;

                 if (trendCount > 0 && !skipCountLabel)
                 {
                     float textY;
                     if (isUp)
                     {
                         textY = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, col.High + 0.5m * boxSize)).Y + (float)chartArea.Top;
                         textY -= 4; 
                     }
                     else
                     {
                         textY = (float)t.ChartToScreen(new ChartPoint(DateTime.MinValue, col.Low - 0.5m * boxSize)).Y + (float)chartArea.Top;
                         textY += _textPaint.TextSize;
                     }
                     
                     _textPaint.Color = isUp ? config.PnfBullishColor.ToSkColor() : config.PnfBearishColor.ToSkColor();
                     _textPaint.TextAlign = SKTextAlign.Center;
                     
                     string text = $"{trendCount}";
                     float textWidth = _textPaint.MeasureText(text);
                     float textHeight = _textPaint.TextSize;
                     
                     var bgColor = theme.ChartBackground.ToSkColor();
                     _bgPaint.Color = new SKColor(bgColor.Red, bgColor.Green, bgColor.Blue, 200);
                     canvas.DrawRect(centerX - textWidth / 2f - 2, textY - textHeight, textWidth + 4, textHeight + 2, _bgPaint);
                     canvas.DrawText(text, centerX, textY, _textPaint);
                 }
                 _textPaint.TextAlign = SKTextAlign.Left;
             }
         }

         // --- HEADER INFO & LABELS (Top-Left HUD) ---
         float hudX = (float)chartArea.Left + 10;
         float hudY = (float)chartArea.Top + 20;

         if (snapshot.Candles.Count > 0)
         {
             var lastCol = snapshot.Candles.Last();
             bool isLastUp = lastCol.Close >= lastCol.Open;
             
             decimal reversalPrice = isLastUp
                 ? lastCol.High - (reversalAmount * boxSize) // Reversal Trigger
                 : lastCol.Low + (reversalAmount * boxSize);
                 
             decimal nextBoxPrice = isLastUp
                 ? lastCol.High + boxSize
                 : lastCol.Low - boxSize;
                 
             decimal currentClose = (decimal)config.CurrentPrice; 


         }
         
          canvas.Restore();
      }

    private void DrawBreakoutSignals(
        SKCanvas canvas,
        Rect chartArea,
        ChartDataSnapshot snapshot,
        IPnfRenderConfig config,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform t,
        decimal boxSize,
        float boxPixelHeight)
    {
        if (!snapshot.PnfAnalysis.HasValue) return;

        var signals = snapshot.PnfAnalysis.Value.Signals;
        foreach (var signal in signals)
        {
            bool shouldDraw = signal.PatternType switch
            {
                PnfPatternType.DoubleTopBreakout or PnfPatternType.DoubleBottomBreakout => config.PnfShowDoubleBreakout,
                PnfPatternType.TripleTopBreakout or PnfPatternType.TripleBottomBreakout => config.PnfShowTripleBreakout,
                PnfPatternType.BullishSupportLineBreakout or PnfPatternType.BearishResistanceLineBreakout => config.PnfShowTrendlineBreakout,
                PnfPatternType.BullishTriangleBreakout or PnfPatternType.BearishTriangleBreakout => config.PnfShowTriangleBreakout,
                PnfPatternType.BullishCatapult or PnfPatternType.BearishCatapult => config.PnfShowCatapultBreakout,
                _ => false
            };

            if (!shouldDraw) continue;

            _signalPaint.Color = signal.IsBullish ? config.PnfBullishColor.ToSkColor() : config.PnfBearishColor.ToSkColor();

            // Find the index of the column that triggered the signal within the VISIBLE range
            int triggerIndex = -1;
            for (int j = 0; j < snapshot.Candles.Count; j++)
            {
                if (snapshot.Candles[j].Timestamp == signal.TriggerTimestamp)
                {
                    triggerIndex = j;
                    break;
                }
            }

            if (triggerIndex == -1) continue; // Signal's trigger column is not visible — skip

            int absoluteIndex = snapshot.StartIndex + triggerIndex;

            // X position is slightly to the right of the column
            float xBase = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteIndex)).X + (float)chartArea.Left;
            float centerX = xBase;
            float markerX = centerX + (float)t.ScaleX / 2f; // Offset to the right

            // Determine line start and trajectory
            bool drawHorizontal = false;
            bool drawSlanted = false;
            int colsBack = signal.AnchorColOffset > 0 ? signal.AnchorColOffset : 2; // fallback to 2 if 0

            decimal lineYPrice = signal.SignalLevel;

            if (signal.PatternType == PnfPatternType.DoubleTopBreakout ||
                signal.PatternType == PnfPatternType.DoubleBottomBreakout ||
                signal.PatternType == PnfPatternType.TripleTopBreakout ||
                signal.PatternType == PnfPatternType.TripleBottomBreakout ||
                signal.PatternType == PnfPatternType.BullishCatapult ||
                signal.PatternType == PnfPatternType.BearishCatapult)
            {
                drawHorizontal = true;

                // WebAI Fix: Snap horizontal lines to the EXACT cell boundaries (Top for Bullish, Bottom for Bearish)
                // Resistance (X) is at High + BoxSize, Support (O) is at Low.
                if (signal.IsBullish)
                {
                    lineYPrice = signal.SignalLevel + boxSize; 
                }
                else
                {
                    lineYPrice = signal.SignalLevel;
                }
            }
            else if (signal.PatternType == PnfPatternType.BullishSupportLineBreakout ||
                     signal.PatternType == PnfPatternType.BearishResistanceLineBreakout ||
                     signal.PatternType == PnfPatternType.BullishTriangleBreakout ||
                     signal.PatternType == PnfPatternType.BearishTriangleBreakout)
            {
                drawSlanted = true;
            }

            // Calculate markerY based on the snapped price using absoluteIndex to ensure coordinate consistency
            float markerY = (float)t.GetYFromPrice(lineYPrice) + (float)chartArea.Top;

            if (drawHorizontal || drawSlanted)
            {
                _dashPaint.Color = (signal.IsBullish ? config.PnfBreakoutBullishColor : config.PnfBreakoutBearishColor).ToSkColor().WithAlpha(150);

                if (signal.PatternType == PnfPatternType.BullishSupportLineBreakout)
                {
                    _dashPaint.Color = config.PnfBreakoutBullishColor.ToSkColor().WithAlpha(150);
                }
                else if (signal.PatternType == PnfPatternType.BearishResistanceLineBreakout)
                {
                    _dashPaint.Color = config.PnfBreakoutBearishColor.ToSkColor().WithAlpha(150);
                }

                if (drawHorizontal)
                {
                    // Use ChartToScreen for stable X positioning during scroll and clamp to minimum 0 to prevent drawing before column 0
                    float startLineX = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(Math.Max(0, absoluteIndex - colsBack))).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                    float endLineX = markerX;
                    canvas.DrawLine(startLineX, markerY, endLineX, markerY, _dashPaint);
                }
                else if (drawSlanted)
                {
                    // Use ChartToScreen for stable X positioning during scroll and support negative indices
                    int absAnchorCol = absoluteIndex - colsBack;
                    float startX = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absAnchorCol)).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                    float endX = centerX + ((float)t.ScaleX / 2f);

                    decimal startPrice = signal.SignalLevel;
                    decimal endYPrice = signal.SignalLevel;
                    
                    if (signal.PatternType == PnfPatternType.BearishResistanceLineBreakout)
                    {
                        // Forward Projection: Extend 5 columns into the future
                        float projectedEndX = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteIndex + 5)).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                        decimal projectedEndPrice = endYPrice - (5 * boxSize);

                        startPrice = signal.SignalLevel + (colsBack * boxSize);
                        
                        float endY = (float)t.GetYFromPrice(projectedEndPrice + boxSize) + (float)chartArea.Top;
                        float startY = (float)t.GetYFromPrice(startPrice + boxSize) + (float)chartArea.Top;
                        canvas.DrawLine(startX, startY, projectedEndX, endY, _dashPaint);
                    }
                    else if (signal.PatternType == PnfPatternType.BullishSupportLineBreakout)
                    {
                        // Forward Projection: Extend 5 columns into the future
                        float projectedEndX = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteIndex + 5)).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                        decimal projectedEndPrice = endYPrice + (5 * boxSize);

                        startPrice = signal.SignalLevel - (colsBack * boxSize);
                        
                        // Corner Snapping: Snap to BOTTOM of the box (Low)
                        float endY = (float)t.GetYFromPrice(projectedEndPrice) + (float)chartArea.Top;
                        float startY = (float)t.GetYFromPrice(startPrice) + (float)chartArea.Top;
                        canvas.DrawLine(startX, startY, projectedEndX, endY, _dashPaint);
                    }
                    else if (signal.PatternType == PnfPatternType.BullishTriangleBreakout || signal.PatternType == PnfPatternType.BearishTriangleBreakout)
                    {
                         // Triangle: Draw two converging lines
                         // Anchor positions (from PatternEngine): 5-column sequence ending at 'i' (absolute index)
                         // We use snapshot.AllCandles to fetch anchor prices even if they are off-screen.
                         var allCandles = snapshot.AllCandles ?? snapshot.Candles;
                         int absoluteTriggerIndex = absoluteIndex;

                         bool hasAll = snapshot.AllCandles != null;
                         bool isWithinBounds = hasAll 
                             ? (absoluteTriggerIndex >= 4 && absoluteTriggerIndex < allCandles.Count)
                             : ((absoluteTriggerIndex - snapshot.StartIndex) >= 4);

                         if (isWithinBounds)
                         {
                             int absR1, absR2; // Resistance anchors (absolute indices)
                             int absS1, absS2; // Support anchors (absolute indices)

                             if (signal.IsBullish)
                             {
                                 absR1 = absoluteTriggerIndex - 4; absR2 = absoluteTriggerIndex - 2;
                                 absS1 = absoluteTriggerIndex - 3; absS2 = absoluteTriggerIndex - 1;
                             }
                             else
                             {
                                 absR1 = absoluteTriggerIndex - 3; absR2 = absoluteTriggerIndex - 1;
                                 absS1 = absoluteTriggerIndex - 4; absS2 = absoluteTriggerIndex - 2;
                             }

                             int idxR1 = hasAll ? absR1 : absR1 - snapshot.StartIndex;
                             int idxR2 = hasAll ? absR2 : absR2 - snapshot.StartIndex;
                             int idxS1 = hasAll ? absS1 : absS1 - snapshot.StartIndex;
                             int idxS2 = hasAll ? absS2 : absS2 - snapshot.StartIndex;

                             if (idxR1 >= 0 && idxR1 < allCandles.Count &&
                                 idxR2 >= 0 && idxR2 < allCandles.Count &&
                                 idxS1 >= 0 && idxS1 < allCandles.Count &&
                                 idxS2 >= 0 && idxS2 < allCandles.Count)
                             {
                                 var cR1 = allCandles[idxR1]; var cR2 = allCandles[idxR2];
                                 var cS1 = allCandles[idxS1]; var cS2 = allCandles[idxS2];

                                 // Corner Snapping: Use the RIGHT edge of the column (center + half width)
                                 float rx1 = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absR1)).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                                 float rx2 = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absR2)).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                                 float sx1 = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absS1)).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                                 float sx2 = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absS2)).X + (float)chartArea.Left + (float)t.ScaleX / 2f;
                                 float rxBreak = xBase + (float)t.ScaleX / 2f; // Breakout column right edge

                                 // Resistance Line (Top: Bearish Color - connects Highs)
                                 _dashPaint.Color = config.PnfBreakoutBearishColor.ToSkColor().WithAlpha(150);
                                 float ry1 = (float)t.GetYFromPrice(cR1.High + boxSize) + (float)chartArea.Top;
                                 float ry2 = (float)t.GetYFromPrice(cR2.High + boxSize) + (float)chartArea.Top;
                                 float slopeR = (ry2 - ry1) / (rx2 - rx1);
                                 float ryBreak = ry2 + slopeR * (rxBreak - rx2);
                                 canvas.DrawLine(rx1, ry1, rxBreak, ryBreak, _dashPaint);

                                 // Support Line (Bottom: Bullish Color - connects Lows)
                                 _dashPaint.Color = config.PnfBreakoutBullishColor.ToSkColor().WithAlpha(150);
                                 float sy1 = (float)t.GetYFromPrice(cS1.Low) + (float)chartArea.Top;
                                 float sy2 = (float)t.GetYFromPrice(cS2.Low) + (float)chartArea.Top;
                                 float slopeS = (sy2 - sy1) / (sx2 - sx1);
                                 float syBreak = sy2 + slopeS * (rxBreak - sx2);
                                 canvas.DrawLine(sx1, sy1, rxBreak, syBreak, _dashPaint);
                             }
                         }
                    }
                }
             }

             // Draw a marker (triangle pointing to the level)
             _markerPath.Reset();
             float arrowSize = Math.Max(5f, boxPixelHeight * 0.3f);
             float verticalOffset = boxPixelHeight * 0.8f; 
             
             if (signal.IsBullish)
             {
                 float topOfColumnY = markerY - verticalOffset;
                 _markerPath.MoveTo(markerX, topOfColumnY - arrowSize * 1.5f);
                 _markerPath.LineTo(markerX - arrowSize, topOfColumnY);
                 _markerPath.LineTo(markerX + arrowSize, topOfColumnY);
             }
             else
             {
                 float bottomOfColumnY = markerY + verticalOffset;
                 _markerPath.MoveTo(markerX, bottomOfColumnY + arrowSize * 1.5f);
                 _markerPath.LineTo(markerX - arrowSize, bottomOfColumnY);
                 _markerPath.LineTo(markerX + arrowSize, bottomOfColumnY);
             }
             _markerPath.Close();
             
             _signalPaint.Style = SKPaintStyle.Fill;
             _signalPaint.Color = _signalPaint.Color.WithAlpha(180);
             canvas.DrawPath(_markerPath, _signalPaint);
         }
    }

    private void DrawActiveTrendlines(
        SKCanvas canvas,
        Rect chartArea,
        ChartDataSnapshot snapshot,
        IPnfRenderConfig config,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform t,
        decimal boxSize)
    {
        if (!snapshot.PnfAnalysis.HasValue) return;

        var activeLines = snapshot.PnfAnalysis.Value.ActiveLines;
        if (config.PnfShowTrendlineBreakout)
        {
            _dashPaint.PathEffect = null; // Solid for trendlines
            _dashPaint.Style = SKPaintStyle.Stroke;
            _dashPaint.StrokeWidth = 2f;
            _dashPaint.IsAntialias = true;

            foreach (var line in activeLines)
            {
                _dashPaint.Color = line.IsBullish
                    ? config.PnfBreakoutBullishColor.ToSkColor().WithAlpha(200)
                    : config.PnfBreakoutBearishColor.ToSkColor().WithAlpha(200);

                int absoluteAnchorIndex = line.AnchorIndex;
                int currentRelativeIndex = snapshot.Candles.Count - 1;
                int absoluteCurrentIndex = snapshot.StartIndex + currentRelativeIndex;
                
                int projectionCols = 15; 
                int absoluteEndIndex = absoluteCurrentIndex + projectionCols;
                
                float xAnchor = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteAnchorIndex)).X + (float)chartArea.Left;
                float xEnd = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteEndIndex)).X + (float)chartArea.Left;
                
                float startX = xAnchor + (float)t.ScaleX / 2f; // Offset to right edge
                float endX = xEnd + (float)t.ScaleX / 2f; // Offset to right edge

                int totalColsForward = (absoluteEndIndex - absoluteAnchorIndex);
                decimal startPrice = line.AnchorPrice;
                decimal endPrice;

                if (line.IsBullish)
                {
                    endPrice = startPrice + (totalColsForward * boxSize);
                    float startY = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteAnchorIndex, startPrice)).Y + (float)chartArea.Top;
                    float endY = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteEndIndex, endPrice)).Y + (float)chartArea.Top;
                    canvas.DrawLine(startX, startY, endX, endY, _dashPaint);
                }
                else
                {
                    endPrice = startPrice - (totalColsForward * boxSize);
                    float startY = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteAnchorIndex, startPrice + boxSize)).Y + (float)chartArea.Top;
                    float endY = (float)t.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(absoluteEndIndex, endPrice + boxSize)).Y + (float)chartArea.Top;
                    canvas.DrawLine(startX, startY, endX, endY, _dashPaint);
                }
            }
        }
    }

    public void Dispose()
    {
        _xPaint?.Dispose();
        _oPaint?.Dispose();
        _textPaint?.Dispose();
        _signalPaint?.Dispose();
        _dashPaint?.Dispose();
        _bgPaint?.Dispose();
        _markerPath?.Dispose();
    }
}
