using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Mathematical engine for vector eraser stroke cutting.
/// Accurately intersects freehand stroke line segments with a circular eraser cursor
/// in screen coordinates, partitioning the stroke into contiguous sub-chains.
/// Sub-chains with fewer than 2 points are discarded per Section 1.2.5.
/// </summary>
public static class StrokeCutterEngine
{
    /// <summary>
    /// Cuts a <see cref="FreehandObject"/> using an eraser circle at <paramref name="eraserScreenCenter"/>
    /// with radius <paramref name="eraserRadiusPx"/>.
    /// </summary>
    /// <param name="stroke">Target freehand stroke to evaluate and split.</param>
    /// <param name="eraserScreenCenter">Eraser center position in screen coordinates.</param>
    /// <param name="eraserRadiusPx">Eraser radius in screen pixels.</param>
    /// <param name="transform">Active coordinate transform.</param>
    /// <param name="resultStrokes">Resulting sub-strokes outside the eraser circle (empty if completely erased).</param>
    /// <returns>True if the stroke intersected the eraser and was cut or eliminated; false if untouched.</returns>
    public static bool CutStroke(
        FreehandObject stroke,
        Point eraserScreenCenter,
        double eraserRadiusPx,
        ICoordinateTransform transform,
        out IReadOnlyList<FreehandObject> resultStrokes)
    {
        if (stroke == null || stroke.Points.Count == 0 || eraserRadiusPx <= 0 || transform == null)
        {
            resultStrokes = stroke != null ? new[] { stroke } : Array.Empty<FreehandObject>();
            return false;
        }

        var r2 = eraserRadiusPx * eraserRadiusPx;
        var c = eraserScreenCenter;

        if (stroke.Points.Count == 1)
        {
            var s0 = transform.ChartToScreen(stroke.Points[0]);
            var dx = s0.X - c.X;
            var dy = s0.Y - c.Y;
            if (dx * dx + dy * dy <= r2)
            {
                resultStrokes = Array.Empty<FreehandObject>();
                return true;
            }
            resultStrokes = new[] { stroke };
            return false;
        }

        int n = stroke.Points.Count;
        var screenPts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            screenPts[i] = transform.ChartToScreen(stroke.Points[i]);
        }

        bool anyIntersection = false;
        var subChains = new List<List<ChartPoint>>();
        List<ChartPoint>? currentChain = null;

        var d0x = screenPts[0].X - c.X;
        var d0y = screenPts[0].Y - c.Y;
        bool start0Inside = (d0x * d0x + d0y * d0y) <= r2;
        if (!start0Inside)
        {
            currentChain = new List<ChartPoint> { stroke.Points[0] };
            subChains.Add(currentChain);
        }

        for (int i = 0; i < n - 1; i++)
        {
            var p1 = screenPts[i];
            var p2 = screenPts[i + 1];
            var cp1 = stroke.Points[i];
            var cp2 = stroke.Points[i + 1];

            var vx = p2.X - p1.X;
            var vy = p2.Y - p1.Y;
            var wx = p1.X - c.X;
            var wy = p1.Y - c.Y;

            var a = vx * vx + vy * vy;
            if (a < 1e-12)
            {
                // Degenerate segment
                if ((wx * wx + wy * wy) <= r2)
                {
                    anyIntersection = true;
                    currentChain = null;
                }
                continue;
            }

            var b = 2.0 * (wx * vx + wy * vy);
            var cq = (wx * wx + wy * wy) - r2;
            var d = b * b - 4.0 * a * cq;

            if (d <= 0)
            {
                // No intersection with interior of circle; entire segment is outside
                if (currentChain == null)
                {
                    currentChain = new List<ChartPoint> { cp1 };
                    subChains.Add(currentChain);
                }
                currentChain.Add(cp2);
            }
            else
            {
                var sqrtD = Math.Sqrt(d);
                var t1 = (-b - sqrtD) / (2.0 * a);
                var t2 = (-b + sqrtD) / (2.0 * a);

                var tEnter = Math.Max(0.0, t1);
                var tExit = Math.Min(1.0, t2);

                if (tEnter < tExit)
                {
                    anyIntersection = true;

                    if (tEnter > 1e-7)
                    {
                        if (currentChain == null)
                        {
                            currentChain = new List<ChartPoint> { cp1 };
                            subChains.Add(currentChain);
                        }
                        var qEnterScreen = new Point(p1.X + vx * tEnter, p1.Y + vy * tEnter);
                        var qEnterChart = transform.ScreenToChart(qEnterScreen);
                        currentChain.Add(qEnterChart);
                        currentChain = null;
                    }
                    else
                    {
                        currentChain = null;
                    }

                    if (tExit < 1.0 - 1e-7)
                    {
                        var qExitScreen = new Point(p1.X + vx * tExit, p1.Y + vy * tExit);
                        var qExitChart = transform.ScreenToChart(qExitScreen);
                        currentChain = new List<ChartPoint> { qExitChart, cp2 };
                        subChains.Add(currentChain);
                    }
                    else
                    {
                        currentChain = null;
                    }
                }
                else
                {
                    // Intersected outside [0, 1] interval; segment is outside
                    if (currentChain == null)
                    {
                        currentChain = new List<ChartPoint> { cp1 };
                        subChains.Add(currentChain);
                    }
                    currentChain.Add(cp2);
                }
            }
        }

        if (!anyIntersection)
        {
            resultStrokes = new[] { stroke };
            return false;
        }

        var validStrokes = new List<FreehandObject>();
        foreach (var chain in subChains)
        {
            var deduplicated = new List<ChartPoint>(chain.Count);
            for (int k = 0; k < chain.Count; k++)
            {
                if (deduplicated.Count == 0 ||
                    deduplicated[^1].Time != chain[k].Time ||
                    deduplicated[^1].Price != chain[k].Price)
                {
                    deduplicated.Add(chain[k]);
                }
            }

            if (deduplicated.Count >= 2)
            {
                var newStroke = new FreehandObject(deduplicated)
                {
                    Color = stroke.Color,
                    Thickness = stroke.Thickness,
                    ZIndex = stroke.ZIndex,
                    StrokeStyle = stroke.StrokeStyle,
                    PanelIndex = stroke.PanelIndex,
                    CustomName = stroke.CustomName,
                    MoveAxisMode = stroke.MoveAxisMode,
                    IsMoveAxisModeExplicit = stroke.IsMoveAxisModeExplicit
                };
                validStrokes.Add(newStroke);
            }
        }

        resultStrokes = validStrokes;
        return true;
    }

    /// <summary>
    /// Executes a vector eraser cut transactionally against <paramref name="manager"/>,
    /// removing the original stroke and adding the resulting sub-strokes under a single atomic
    /// <see cref="IDrawingEditCoordinator"/> edit command.
    /// </summary>
    public static bool ExecuteCut(
        FreehandObject target,
        Point eraserScreenCenter,
        double eraserRadiusPx,
        ICoordinateTransform transform,
        ChartObjectManager manager,
        IDrawingLayerService? layerService,
        IDrawingEditCoordinator? coordinator)
    {
        if (target == null || manager == null || transform == null) return false;
        if (target.IsLocked || manager.IsLocked(target.Id)) return false;

        if (layerService != null)
        {
            var layer = layerService.GetLayerForObject(target.Id);
            if (layer?.IsEditLocked == true) return false;
        }

        if (!CutStroke(target, eraserScreenCenter, eraserRadiusPx, transform, out var resultStrokes))
        {
            return false;
        }

        void PerformCut()
        {
            var layerRecord = layerService?.GetLayerForObject(target.Id);

            manager.RemoveObject(target.Id);
            layerService?.RemoveObjectFromLayer(target.Id);

            for (int i = 0; i < resultStrokes.Count; i++)
            {
                var subStroke = resultStrokes[i];
                manager.AddObject(subStroke);
                if (layerRecord.HasValue && layerService != null)
                {
                    layerService.AddObjectToLayer(subStroke.Id, layerRecord.Value.Panel, layerRecord.Value.LayerId);
                }
            }
        }

        if (coordinator != null)
        {
            coordinator.ExecuteEdit(DrawingOperationKind.Delete, PerformCut);
        }
        else
        {
            PerformCut();
        }

        return true;
    }
}
