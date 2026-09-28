using System;
using System.Collections.Generic;
using SkiaSharp;
using Avalonia;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders indicator lines specifically for index-based charts (Renko, P&F, Kagi, TLB) 
/// using index-based X-coordinates.
/// Complies with ZeroAllocation and NaN-safe drawing requirements.
/// </summary>
public sealed class IndexIndicatorRenderer : IDisposable
{
    private readonly SKPath _path;
    private readonly SKPaint _paint;

    public IndexIndicatorRenderer()
    {
        _path = new SKPath();
        _paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
    }

    /// <summary>
    /// Renders the indicator values onto the canvas.
    /// </summary>
    /// <param name="canvas">The Target SKCanvas.</param>
    /// <param name="chartArea">The visible chart area rectangle.</param>
    /// <param name="values">The indicator values to render (mapped to chart blocks/columns).</param>
    /// <param name="setting">The indicator settings (color, thickness, etc.).</param>
    /// <param name="config">The renderer configuration including transform.</param>
    /// <param name="snapshot">The chart data snapshot for indexing context.</param>
    public void Render(
        SKCanvas canvas, 
        Rect chartArea, 
        IReadOnlyList<decimal?> values, 
        CoreIndicatorSettings setting, 
        IChartRenderConfig config,
        ChartDataSnapshot snapshot)
    {
        if (values == null || values.Count == 0 || config.Transform == null) return;
        
        // Safety: Snapshot and Values count must align for index-based rendering
        int count = Math.Min(values.Count, snapshot.Candles.Count);
        if (count == 0) return;

        var t = config.Transform;
        
        // Configure Paint
        _paint.Color = new SKColor(setting.Color.R, setting.Color.G, setting.Color.B, setting.Color.A);
        _paint.StrokeWidth = (float)setting.Thickness;

        // Reset Path for ZeroAllocation re-use
        _path.Reset();

        bool isFirstPoint = true;

        // RESET: In Step 3-3, the column width is directly provided by the transform's ScaleX.
        float columnWidth = (float)t.ScaleX;

        for (int i = 0; i < count; i++)
        {
            var val = values[i];
            if (val == null || !val.HasValue)
            {
                // Break the line on NaN/Null
                isFirstPoint = true;
                continue;
            }

            // X-Coordinate: Use the logical index mapping which handles Kagi column grouping
            double logicalIndex = snapshot.GetLogicalXIndex(i, config);

            // Approach A: For Kagi charts, if there are multiple segments mapped to the same ColumnIndex (logicalIndex),
            // we only draw the last segment's Close in that column. This prevents vertical segments in SMA(1)
            // and ensures SMA(1) directly and continuously connects the actual extreme high/low (shoulder/waist) vertices.
            if (snapshot.ChartType == ChartType.Kagi && i < count - 1)
            {
                double nextLogicalIndex = snapshot.GetLogicalXIndex(i + 1, config);
                if (logicalIndex == nextLogicalIndex)
                {
                    // Skip this intermediate split segment to avoid drawing a vertical line on the same column
                    continue;
                }
            }

            float x = (float)t.GetXFromIndex(logicalIndex) + (float)chartArea.Left;
            float centerX;
            if (snapshot.ChartType == ChartType.PointAndFigure)
            {
                centerX = MathF.Floor(x) + 0.5f;
            }
            else
            {
                centerX = MathF.Floor(x + columnWidth / 2f) + 0.5f;
            }

            // Y-Coordinate: Use the direct price-based mapping (No ChartPoint Hack)
            decimal priceValue = val.Value;
            if (snapshot.ChartType == ChartType.PointAndFigure)
            {
                // Align to the center of the P&F cell (box)
                priceValue += snapshot.MinBrickSize / 2m;
            }
            float y = MathF.Floor((float)t.GetYFromPrice(priceValue) + (float)chartArea.Top) + 0.5f;

            if (isFirstPoint)
            {
                _path.MoveTo(centerX, y);
                isFirstPoint = false;
            }
            else
            {
                _path.LineTo(centerX, y);
            }
        }

        if (!_path.IsEmpty)
        {
            canvas.DrawPath(_path, _paint);
        }
    }

    public void Dispose()
    {
        _path.Dispose();
        _paint.Dispose();
    }
}
