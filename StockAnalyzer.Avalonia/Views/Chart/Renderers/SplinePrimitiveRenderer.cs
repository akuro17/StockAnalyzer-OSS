using System;
using System.Buffers;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders an X-monotonic cubic spline passing exactly through original data points
/// by delegating to <see cref="BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath"/>.
/// </summary>
internal sealed class SplinePrimitiveRenderer : ISeriesPrimitiveRenderer
{
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPath _path = new();

    public void Render(in SeriesPrimitiveRenderContext context)
    {
        var desc = context.Descriptor;
        var values = context.Result.GetSeries(desc.PrimarySeriesName);
        int n = context.VisibleCandles;
        if (values == null || values.Count == 0 || n <= 0) return;

        double tension = double.IsFinite(desc.SplineTension)
            ? Math.Clamp(desc.SplineTension, 0.0, 1.0)
            : BezierSplineMath.DefaultTension;

        _paint.StrokeWidth = MathF.Max(0.5f, (float)context.Setting.Thickness);
        _paint.PathEffect = desc.LineStyle.ToSkPathEffect();
        bool twoColors = context.Setting.UseUpDownColors;
        SKColor upColor = context.Setting.UpColor.ToSkColor();
        SKColor downColor = context.Setting.DownColor.ToSkColor();
        _paint.Color = context.SeriesColor;

        SKPoint[]? rented = n > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<SKPoint>.Shared.Rent(n)
            : null;
        decimal[]? rentedVals = twoColors && n > DrawingBufferLimits.StackAllocElementThreshold
            ? ArrayPool<decimal>.Shared.Rent(n)
            : null;
        try
        {
            Span<SKPoint> run = rented != null ? rented.AsSpan(0, n) : stackalloc SKPoint[n];
            Span<decimal> runVals = twoColors
                ? (rentedVals != null ? rentedVals.AsSpan(0, n) : stackalloc decimal[n])
                : Span<decimal>.Empty;
            int runLen = 0;

            for (int i = 0; i < n; i++)
            {
                int valIdx = i - context.Offset;
                if (valIdx < 0 || valIdx >= values.Count)
                {
                    if (desc.NullPolicy == NullGapPolicy.BreakPath)
                    {
                        DrawRun(context.Canvas, run.Slice(0, runLen), twoColors ? runVals.Slice(0, runLen) : Span<decimal>.Empty, tension, twoColors, upColor, downColor);
                        runLen = 0;
                    }
                    continue;
                }

                decimal? val = values[valIdx];
                if (!ChartPrimitiveMath.TryProjectY(val, context.ChartArea, context.Transform, context.Snapshot.PriceScale, out float rawY))
                {
                    if (desc.NullPolicy == NullGapPolicy.BreakPath)
                    {
                        DrawRun(context.Canvas, run.Slice(0, runLen), twoColors ? runVals.Slice(0, runLen) : Span<decimal>.Empty, tension, twoColors, upColor, downColor);
                        runLen = 0;
                    }
                    continue;
                }

                float rawX = ChartPrimitiveMath.ComputeBarCenterX(
                    i, context.ColumnWidth, context.ChartArea, context.StartIndex,
                    context.Candles, context.Interval, context.Transform,
                    context.Snapshot, context.Config, context.XOffset);
                if (!float.IsFinite(rawX))
                {
                    DrawRun(context.Canvas, run.Slice(0, runLen), twoColors ? runVals.Slice(0, runLen) : Span<decimal>.Empty, tension, twoColors, upColor, downColor);
                    runLen = 0;
                    continue;
                }

                run[runLen] = new SKPoint(rawX, rawY);
                if (twoColors)
                {
                    runVals[runLen] = val!.Value;
                }
                runLen++;
            }

            DrawRun(context.Canvas, run.Slice(0, runLen), twoColors ? runVals.Slice(0, runLen) : Span<decimal>.Empty, tension, twoColors, upColor, downColor);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
            if (rentedVals != null)
            {
                ArrayPool<decimal>.Shared.Return(rentedVals);
            }
        }
    }

    private void DrawRun(
        SKCanvas canvas,
        ReadOnlySpan<SKPoint> run,
        ReadOnlySpan<decimal> runVals,
        double tension,
        bool twoColors,
        SKColor upColor,
        SKColor downColor)
    {
        if (run.Length < 2) return;

        if (!twoColors)
        {
            _path.Reset();
            BezierSplineMath.BuildTimeDecoupledCatmullRomSplinePath(_path, run, tension);
            canvas.DrawPath(_path, _paint);
            return;
        }

        if (double.IsNaN(tension) || double.IsInfinity(tension))
        {
            return;
        }

        for (int i = 0; i < run.Length; i++)
        {
            if (!float.IsFinite(run[i].X) || !float.IsFinite(run[i].Y))
            {
                return;
            }
        }

        if (run.Length == 2)
        {
            _path.Reset();
            _paint.Color = runVals[1] >= runVals[0] ? upColor : downColor;
            _path.MoveTo(run[0]);
            _path.LineTo(run[1]);
            canvas.DrawPath(_path, _paint);
            return;
        }

        _path.Reset();
        bool hasSegment = false;
        SKColor pathColor = upColor;

        for (int i = 0; i < run.Length - 1; i++)
        {
            SKPoint pCurr = run[i];
            SKPoint pNext = run[i + 1];
            float dx = pNext.X - pCurr.X;
            float dy = pNext.Y - pCurr.Y;
            if (dx * dx + dy * dy < BezierSplineMath.EpsilonSquared)
            {
                continue;
            }

            SKPoint pPrev = i == 0
                ? new SKPoint(2f * run[0].X - run[1].X, 2f * run[0].Y - run[1].Y)
                : run[i - 1];

            SKPoint pNextNext = i == run.Length - 2
                ? new SKPoint(2f * run[^1].X - run[^2].X, 2f * run[^1].Y - run[^2].Y)
                : run[i + 2];

            BezierSplineMath.CalculateTimeDecoupledControlPoints(pPrev, pCurr, pNext, pNextNext, tension, out var c1, out var c2);

            SKColor segColor = runVals[i + 1] >= runVals[i] ? upColor : downColor;
            if (hasSegment && segColor != pathColor)
            {
                _paint.Color = pathColor;
                canvas.DrawPath(_path, _paint);
                _path.Reset();
                hasSegment = false;
            }

            if (!hasSegment)
            {
                _path.MoveTo(pCurr);
                pathColor = segColor;
                hasSegment = true;
            }

            _path.CubicTo(c1, c2, pNext);
        }

        if (hasSegment)
        {
            _paint.Color = pathColor;
            canvas.DrawPath(_path, _paint);
            _path.Reset();
        }
    }

    public void Dispose()
    {
        _paint.Dispose();
        _path.Dispose();
    }
}
