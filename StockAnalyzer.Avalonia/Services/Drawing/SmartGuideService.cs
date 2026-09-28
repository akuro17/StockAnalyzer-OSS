using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Default Zero-Allocation implementation of <see cref="ISmartGuideService"/>.
/// </summary>
public class SmartGuideService : ISmartGuideService
{
    private readonly struct TargetAabb
    {
        public readonly bool HasX;
        public readonly bool HasY;
        public readonly double MinX;
        public readonly double MidX;
        public readonly double MaxX;
        public readonly double MinY;
        public readonly double MidY;
        public readonly double MaxY;
        public readonly Guid ObjectId;

        public TargetAabb(Guid objectId, bool hasX, bool hasY, double minX, double midX, double maxX, double minY, double midY, double maxY)
        {
            ObjectId = objectId;
            HasX = hasX;
            HasY = hasY;
            MinX = minX;
            MidX = midX;
            MaxX = maxX;
            MinY = minY;
            MidY = midY;
            MaxY = maxY;
        }
    }

    public SmartSnapResult SnapObjectMove(
        IChartObject draggedObject,
        Rect proposedBoundsScreen,
        IReadOnlyList<IChartObject> allObjects,
        ICoordinateTransform transform,
        Rect chartArea,
        double snapThreshold,
        List<SmartGuideLine> outGuideLines)
    {
        outGuideLines?.Clear();

        if (draggedObject == null || allObjects == null || allObjects.Count <= 1 || transform == null || snapThreshold <= 0)
        {
            var centerScreen = new global::Avalonia.Point((proposedBoundsScreen.Left + proposedBoundsScreen.Right) / 2.0, (proposedBoundsScreen.Top + proposedBoundsScreen.Bottom) / 2.0);
            return new SmartSnapResult(centerScreen, transform?.ScreenToChart(centerScreen) ?? new ChartPoint(DateTime.MinValue, 0), 0, 0, false, false);
        }

        double srcMinX = proposedBoundsScreen.Left;
        double srcMidX = (proposedBoundsScreen.Left + proposedBoundsScreen.Right) / 2.0;
        double srcMaxX = proposedBoundsScreen.Right;

        double srcMinY = proposedBoundsScreen.Top;
        double srcMidY = (proposedBoundsScreen.Top + proposedBoundsScreen.Bottom) / 2.0;
        double srcMaxY = proposedBoundsScreen.Bottom;

        Span<double> srcXArr = stackalloc double[3] { srcMinX, srcMidX, srcMaxX };
        Span<double> srcYArr = stackalloc double[3] { srcMinY, srcMidY, srcMaxY };

        double bestDistX = snapThreshold + 1.0;
        double bestCorrectionX = 0;
        double bestTargetX = 0;
        Guid bestTargetIdX = Guid.Empty;
        bool foundX = false;

        double bestDistY = snapThreshold + 1.0;
        double bestCorrectionY = 0;
        double bestTargetY = 0;
        Guid bestTargetIdY = Guid.Empty;
        bool foundY = false;

        int count = allObjects.Count;
        for (int i = 0; i < count; i++)
        {
            var target = allObjects[i];
            if (target == null || target.Id == draggedObject.Id || !target.IsVisible || target.IsLocked)
                continue;

            var aabb = ComputeTargetAabb(target, transform);

            // Evaluate X-Axis snapping
            if (aabb.HasX)
            {
                Span<double> targetXArr = stackalloc double[3] { aabb.MinX, aabb.MidX, aabb.MaxX };
                for (int s = 0; s < 3; s++)
                {
                    double sx = srcXArr[s];
                    for (int t = 0; t < 3; t++)
                    {
                        double tx = targetXArr[t];
                        double dist = Math.Abs(sx - tx);
                        if (dist <= snapThreshold && dist < bestDistX)
                        {
                            bestDistX = dist;
                            bestCorrectionX = tx - sx;
                            bestTargetX = tx;
                            bestTargetIdX = target.Id;
                            foundX = true;
                        }
                    }
                }
            }

            // Evaluate Y-Axis snapping
            if (aabb.HasY)
            {
                Span<double> targetYArr = stackalloc double[3] { aabb.MinY, aabb.MidY, aabb.MaxY };
                for (int s = 0; s < 3; s++)
                {
                    double sy = srcYArr[s];
                    for (int t = 0; t < 3; t++)
                    {
                        double ty = targetYArr[t];
                        double dist = Math.Abs(sy - ty);
                        if (dist <= snapThreshold && dist < bestDistY)
                        {
                            bestDistY = dist;
                            bestCorrectionY = ty - sy;
                            bestTargetY = ty;
                            bestTargetIdY = target.Id;
                            foundY = true;
                        }
                    }
                }
            }
        }

        if (outGuideLines != null)
        {
            if (foundX)
            {
                outGuideLines.Add(new SmartGuideLine(
                    SmartGuideAxis.Vertical,
                    (float)bestTargetX,
                    (float)chartArea.Top,
                    (float)chartArea.Bottom,
                    bestTargetIdX));
            }

            if (foundY)
            {
                outGuideLines.Add(new SmartGuideLine(
                    SmartGuideAxis.Horizontal,
                    (float)bestTargetY,
                    (float)chartArea.Left,
                    (float)chartArea.Right,
                    bestTargetIdY));
            }
        }

        var finalCenterScreen = new global::Avalonia.Point(srcMidX + bestCorrectionX, srcMidY + bestCorrectionY);
        var finalChart = transform.ScreenToChart(finalCenterScreen);

        return new SmartSnapResult(
            finalCenterScreen,
            finalChart,
            bestCorrectionX,
            bestCorrectionY,
            foundX,
            foundY);
    }

    public SmartSnapResult SnapHandleMove(
        IChartObject draggedObject,
        int handleIndex,
        global::Avalonia.Point proposedHandleScreen,
        IReadOnlyList<IChartObject> allObjects,
        ICoordinateTransform transform,
        Rect chartArea,
        double snapThreshold,
        List<SmartGuideLine> outGuideLines)
    {
        outGuideLines?.Clear();

        if (draggedObject == null || allObjects == null || allObjects.Count <= 1 || transform == null || snapThreshold <= 0)
        {
            return new SmartSnapResult(proposedHandleScreen, transform?.ScreenToChart(proposedHandleScreen) ?? new ChartPoint(DateTime.MinValue, 0), 0, 0, false, false);
        }

        double srcX = proposedHandleScreen.X;
        double srcY = proposedHandleScreen.Y;

        double bestDistX = snapThreshold + 1.0;
        double bestCorrectionX = 0;
        double bestTargetX = 0;
        Guid bestTargetIdX = Guid.Empty;
        bool foundX = false;

        double bestDistY = snapThreshold + 1.0;
        double bestCorrectionY = 0;
        double bestTargetY = 0;
        Guid bestTargetIdY = Guid.Empty;
        bool foundY = false;

        int count = allObjects.Count;
        for (int i = 0; i < count; i++)
        {
            var target = allObjects[i];
            if (target == null || target.Id == draggedObject.Id || !target.IsVisible || target.IsLocked)
                continue;

            var aabb = ComputeTargetAabb(target, transform);

            // Evaluate X-Axis snapping
            if (aabb.HasX)
            {
                Span<double> targetXArr = stackalloc double[3] { aabb.MinX, aabb.MidX, aabb.MaxX };
                for (int t = 0; t < 3; t++)
                {
                    double tx = targetXArr[t];
                    double dist = Math.Abs(srcX - tx);
                    if (dist <= snapThreshold && dist < bestDistX)
                    {
                        bestDistX = dist;
                        bestCorrectionX = tx - srcX;
                        bestTargetX = tx;
                        bestTargetIdX = target.Id;
                        foundX = true;
                    }
                }
            }

            // Evaluate Y-Axis snapping
            if (aabb.HasY)
            {
                Span<double> targetYArr = stackalloc double[3] { aabb.MinY, aabb.MidY, aabb.MaxY };
                for (int t = 0; t < 3; t++)
                {
                    double ty = targetYArr[t];
                    double dist = Math.Abs(srcY - ty);
                    if (dist <= snapThreshold && dist < bestDistY)
                    {
                        bestDistY = dist;
                        bestCorrectionY = ty - srcY;
                        bestTargetY = ty;
                        bestTargetIdY = target.Id;
                        foundY = true;
                    }
                }
            }
        }

        if (outGuideLines != null)
        {
            if (foundX)
            {
                outGuideLines.Add(new SmartGuideLine(
                    SmartGuideAxis.Vertical,
                    (float)bestTargetX,
                    (float)chartArea.Top,
                    (float)chartArea.Bottom,
                    bestTargetIdX));
            }

            if (foundY)
            {
                outGuideLines.Add(new SmartGuideLine(
                    SmartGuideAxis.Horizontal,
                    (float)bestTargetY,
                    (float)chartArea.Left,
                    (float)chartArea.Right,
                    bestTargetIdY));
            }
        }

        var snappedScreen = new global::Avalonia.Point(srcX + bestCorrectionX, srcY + bestCorrectionY);
        var snappedChart = transform.ScreenToChart(snappedScreen);

        return new SmartSnapResult(
            snappedScreen,
            snappedChart,
            bestCorrectionX,
            bestCorrectionY,
            foundX,
            foundY);
    }

    private static TargetAabb ComputeTargetAabb(IChartObject target, ICoordinateTransform transform)
    {
        if (target is HorizontalLineObject hLine && hLine.Points.Count > 0)
        {
            var p0 = transform.ChartToScreen(hLine.Points[0]);
            return new TargetAabb(target.Id, false, true, 0, 0, 0, p0.Y, p0.Y, p0.Y);
        }

        if (target is VerticalLineObject vLine && vLine.Points.Count > 0)
        {
            var p0 = transform.ChartToScreen(vLine.Points[0]);
            return new TargetAabb(target.Id, true, false, p0.X, p0.X, p0.X, 0, 0, 0);
        }

        if (target is LongShortPositionObject ls && ls.Points.Count >= 3)
        {
            var entryPt = transform.ChartToScreen(ls.Points[0]);
            var stopPt = transform.ChartToScreen(ls.Points[1]);
            var targetPt = transform.ChartToScreen(ls.Points[2]);

            double minX = entryPt.X;
            double maxX = entryPt.X + ls.BoxWidth;
            double midX = (minX + maxX) / 2.0;

            double minY = Math.Min(entryPt.Y, Math.Min(stopPt.Y, targetPt.Y));
            double maxY = Math.Max(entryPt.Y, Math.Max(stopPt.Y, targetPt.Y));
            double midY = (minY + maxY) / 2.0;

            return new TargetAabb(target.Id, true, true, minX, midX, maxX, minY, midY, maxY);
        }

        var pts = target.Points;
        if (pts == null || pts.Count == 0)
        {
            return new TargetAabb(target.Id, false, false, 0, 0, 0, 0, 0, 0);
        }

        double gMinX = double.MaxValue;
        double gMaxX = double.MinValue;
        double gMinY = double.MaxValue;
        double gMaxY = double.MinValue;

        int ptCount = pts.Count;
        for (int p = 0; p < ptCount; p++)
        {
            var sp = transform.ChartToScreen(pts[p]);
            if (double.IsNaN(sp.X) || double.IsInfinity(sp.X) || double.IsNaN(sp.Y) || double.IsInfinity(sp.Y))
                continue;

            if (sp.X < gMinX) gMinX = sp.X;
            if (sp.X > gMaxX) gMaxX = sp.X;
            if (sp.Y < gMinY) gMinY = sp.Y;
            if (sp.Y > gMaxY) gMaxY = sp.Y;
        }

        if (gMinX == double.MaxValue)
        {
            return new TargetAabb(target.Id, false, false, 0, 0, 0, 0, 0, 0);
        }

        return new TargetAabb(
            target.Id,
            true,
            true,
            gMinX,
            (gMinX + gMaxX) / 2.0,
            gMaxX,
            gMinY,
            (gMinY + gMaxY) / 2.0,
            gMaxY);
    }
}
