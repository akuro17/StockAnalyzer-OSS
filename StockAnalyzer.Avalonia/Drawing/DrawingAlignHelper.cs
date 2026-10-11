using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Helper providing precision alignment, distribution, and grouping operations for drawing objects.
/// Enforces strict decimal arithmetic for all financial price values (Invariant I-01)
/// and encapsulates mutations within atomic <see cref="IDrawingEditCoordinator"/> transactions.
/// </summary>
public static class DrawingAlignHelper
{
    private readonly record struct ObjectBounds(
        IChartObject Object,
        DateTime MinTime,
        DateTime MaxTime,
        decimal MinPrice,
        decimal MaxPrice)
    {
        public DateTime CenterTime => MinTime + TimeSpan.FromTicks((MaxTime - MinTime).Ticks / 2);
        public decimal CenterPrice => MinPrice + (MaxPrice - MinPrice) / 2m;
        public TimeSpan Width => MaxTime - MinTime;
        public decimal Height => MaxPrice - MinPrice;
    }

    /// <summary>
    /// Computes the bounding box of an <see cref="IChartObject"/> in chart coordinates.
    /// </summary>
    public static bool TryGetObjectBounds(
        IChartObject obj,
        out DateTime minTime,
        out DateTime maxTime,
        out decimal minPrice,
        out decimal maxPrice)
    {
        if (obj == null)
        {
            minTime = maxTime = DateTime.MinValue;
            minPrice = maxPrice = 0m;
            return false;
        }

        if (obj is IAabbDeformableObject aabb)
        {
            aabb.GetDataBounds(out minTime, out maxTime, out minPrice, out maxPrice);
            return true;
        }

        if (obj.Points != null && obj.Points.Count > 0)
        {
            minTime = maxTime = obj.Points[0].Time;
            minPrice = maxPrice = obj.Points[0].Price;

            for (int i = 1; i < obj.Points.Count; i++)
            {
                var pt = obj.Points[i];
                if (pt.Time < minTime) minTime = pt.Time;
                if (pt.Time > maxTime) maxTime = pt.Time;
                if (pt.Price < minPrice) minPrice = pt.Price;
                if (pt.Price > maxPrice) maxPrice = pt.Price;
            }
            return true;
        }

        minTime = maxTime = DateTime.MinValue;
        minPrice = maxPrice = 0m;
        return false;
    }

    private static List<ObjectBounds> ExtractBounds(IReadOnlyList<IChartObject> objects)
    {
        var list = new List<ObjectBounds>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (TryGetObjectBounds(obj, out var minT, out var maxT, out var minP, out var maxP))
            {
                list.Add(new ObjectBounds(obj, minT, maxT, minP, maxP));
            }
        }
        return list;
    }

    private static bool ExecuteCompositeMove(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager,
        IDrawingEditCoordinator? coordinator,
        Action moveAction)
    {
        if (coordinator != null)
        {
            var res = coordinator.ExecuteEdit(DrawingOperationKind.Move, moveAction);
            return res.IsSuccess;
        }

        moveAction();
        if (manager != null)
        {
            for (int i = 0; i < objects.Count; i++)
            {
                manager.NotifyObjectChanged(objects[i].Id);
            }
        }
        return true;
    }

    #region Alignment Operations

    public static bool AlignLeft(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => AlignLeft(manager.SelectedObjects, manager, coordinator);

    public static bool AlignLeft(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 2) return false;

        var targetMinTime = boundsList.Min(b => b.MinTime);

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            for (int i = 0; i < boundsList.Count; i++)
            {
                var b = boundsList[i];
                if (b.Object.IsLocked || (manager != null && manager.IsLocked(b.Object.Id))) continue;

                var deltaT = targetMinTime - b.MinTime;
                if (deltaT != TimeSpan.Zero)
                {
                    b.Object.Translate(deltaT, 0m);
                    manager?.NotifyObjectChanged(b.Object.Id);
                }
            }
        });
    }

    public static bool AlignRight(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => AlignRight(manager.SelectedObjects, manager, coordinator);

    public static bool AlignRight(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 2) return false;

        var targetMaxTime = boundsList.Max(b => b.MaxTime);

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            for (int i = 0; i < boundsList.Count; i++)
            {
                var b = boundsList[i];
                if (b.Object.IsLocked || (manager != null && manager.IsLocked(b.Object.Id))) continue;

                var deltaT = targetMaxTime - b.MaxTime;
                if (deltaT != TimeSpan.Zero)
                {
                    b.Object.Translate(deltaT, 0m);
                    manager?.NotifyObjectChanged(b.Object.Id);
                }
            }
        });
    }

    public static bool AlignCenterHorizontal(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => AlignCenterHorizontal(manager.SelectedObjects, manager, coordinator);

    public static bool AlignCenterHorizontal(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 2) return false;

        var minTime = boundsList.Min(b => b.MinTime);
        var maxTime = boundsList.Max(b => b.MaxTime);
        var targetCenter = minTime + TimeSpan.FromTicks((maxTime - minTime).Ticks / 2);

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            for (int i = 0; i < boundsList.Count; i++)
            {
                var b = boundsList[i];
                if (b.Object.IsLocked || (manager != null && manager.IsLocked(b.Object.Id))) continue;

                var deltaT = targetCenter - b.CenterTime;
                if (deltaT != TimeSpan.Zero)
                {
                    b.Object.Translate(deltaT, 0m);
                    manager?.NotifyObjectChanged(b.Object.Id);
                }
            }
        });
    }

    public static bool AlignTop(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => AlignTop(manager.SelectedObjects, manager, coordinator);

    public static bool AlignTop(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 2) return false;

        var targetMaxPrice = boundsList.Max(b => b.MaxPrice);

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            for (int i = 0; i < boundsList.Count; i++)
            {
                var b = boundsList[i];
                if (b.Object.IsLocked || (manager != null && manager.IsLocked(b.Object.Id))) continue;

                var deltaP = targetMaxPrice - b.MaxPrice;
                if (deltaP != 0m)
                {
                    b.Object.Translate(TimeSpan.Zero, deltaP);
                    manager?.NotifyObjectChanged(b.Object.Id);
                }
            }
        });
    }

    public static bool AlignBottom(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => AlignBottom(manager.SelectedObjects, manager, coordinator);

    public static bool AlignBottom(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 2) return false;

        var targetMinPrice = boundsList.Min(b => b.MinPrice);

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            for (int i = 0; i < boundsList.Count; i++)
            {
                var b = boundsList[i];
                if (b.Object.IsLocked || (manager != null && manager.IsLocked(b.Object.Id))) continue;

                var deltaP = targetMinPrice - b.MinPrice;
                if (deltaP != 0m)
                {
                    b.Object.Translate(TimeSpan.Zero, deltaP);
                    manager?.NotifyObjectChanged(b.Object.Id);
                }
            }
        });
    }

    public static bool AlignCenterVertical(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => AlignCenterVertical(manager.SelectedObjects, manager, coordinator);

    public static bool AlignCenterVertical(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 2) return false;

        var minPrice = boundsList.Min(b => b.MinPrice);
        var maxPrice = boundsList.Max(b => b.MaxPrice);
        var targetCenter = minPrice + (maxPrice - minPrice) / 2m;

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            for (int i = 0; i < boundsList.Count; i++)
            {
                var b = boundsList[i];
                if (b.Object.IsLocked || (manager != null && manager.IsLocked(b.Object.Id))) continue;

                var deltaP = targetCenter - b.CenterPrice;
                if (deltaP != 0m)
                {
                    b.Object.Translate(TimeSpan.Zero, deltaP);
                    manager?.NotifyObjectChanged(b.Object.Id);
                }
            }
        });
    }

    #endregion

    #region Distribution Operations

    public static bool DistributeHorizontal(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => DistributeHorizontal(manager.SelectedObjects, manager, coordinator);

    public static bool DistributeHorizontal(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 3) return false;

        boundsList.Sort((a, b) => a.MinTime.CompareTo(b.MinTime));

        int m = boundsList.Count;
        var first = boundsList[0];
        var last = boundsList[m - 1];

        long totalWidthTicks = 0;
        for (int i = 0; i < m; i++)
        {
            totalWidthTicks += boundsList[i].Width.Ticks;
        }

        long totalSpanTicks = (last.MaxTime - first.MinTime).Ticks;
        long totalGapTicks = totalSpanTicks - totalWidthTicks;

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            if (totalGapTicks > 0)
            {
                long gapTicks = totalGapTicks / (m - 1);
                var currentT = first.MaxTime;

                for (int i = 1; i < m - 1; i++)
                {
                    var b = boundsList[i];
                    var targetMin = currentT + TimeSpan.FromTicks(gapTicks);
                    if (!b.Object.IsLocked && (manager == null || !manager.IsLocked(b.Object.Id)))
                    {
                        var deltaT = targetMin - b.MinTime;
                        if (deltaT != TimeSpan.Zero)
                        {
                            b.Object.Translate(deltaT, 0m);
                            manager?.NotifyObjectChanged(b.Object.Id);
                        }
                    }
                    currentT = targetMin + b.Width;
                }
            }
            else
            {
                // Overlapping or negative gaps: distribute centers uniformly
                long centerSpanTicks = (last.CenterTime - first.CenterTime).Ticks;
                long stepTicks = centerSpanTicks / (m - 1);

                for (int i = 1; i < m - 1; i++)
                {
                    var b = boundsList[i];
                    var targetCenter = first.CenterTime + TimeSpan.FromTicks(i * stepTicks);
                    if (!b.Object.IsLocked && (manager == null || !manager.IsLocked(b.Object.Id)))
                    {
                        var deltaT = targetCenter - b.CenterTime;
                        if (deltaT != TimeSpan.Zero)
                        {
                            b.Object.Translate(deltaT, 0m);
                            manager?.NotifyObjectChanged(b.Object.Id);
                        }
                    }
                }
            }
        });
    }

    public static bool DistributeVertical(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
        => DistributeVertical(manager.SelectedObjects, manager, coordinator);

    public static bool DistributeVertical(
        IReadOnlyList<IChartObject> objects,
        ChartObjectManager? manager = null,
        IDrawingEditCoordinator? coordinator = null)
    {
        var boundsList = ExtractBounds(objects);
        if (boundsList.Count < 3) return false;

        boundsList.Sort((a, b) => a.MinPrice.CompareTo(b.MinPrice));

        int m = boundsList.Count;
        var first = boundsList[0];
        var last = boundsList[m - 1];

        decimal totalHeight = 0m;
        for (int i = 0; i < m; i++)
        {
            totalHeight += boundsList[i].Height;
        }

        decimal totalSpan = last.MaxPrice - first.MinPrice;
        decimal totalGap = totalSpan - totalHeight;

        return ExecuteCompositeMove(objects, manager, coordinator, () =>
        {
            if (totalGap > 0m)
            {
                decimal gap = totalGap / (decimal)(m - 1);
                decimal currentP = first.MaxPrice;

                for (int i = 1; i < m - 1; i++)
                {
                    var b = boundsList[i];
                    decimal targetMin = currentP + gap;
                    if (!b.Object.IsLocked && (manager == null || !manager.IsLocked(b.Object.Id)))
                    {
                        decimal deltaP = targetMin - b.MinPrice;
                        if (deltaP != 0m)
                        {
                            b.Object.Translate(TimeSpan.Zero, deltaP);
                            manager?.NotifyObjectChanged(b.Object.Id);
                        }
                    }
                    currentP = targetMin + b.Height;
                }
            }
            else
            {
                // Overlapping or negative gaps: distribute centers uniformly
                decimal centerSpan = last.CenterPrice - first.CenterPrice;
                decimal step = centerSpan / (decimal)(m - 1);

                for (int i = 1; i < m - 1; i++)
                {
                    var b = boundsList[i];
                    decimal targetCenter = first.CenterPrice + (decimal)i * step;
                    if (!b.Object.IsLocked && (manager == null || !manager.IsLocked(b.Object.Id)))
                    {
                        decimal deltaP = targetCenter - b.CenterPrice;
                        if (deltaP != 0m)
                        {
                            b.Object.Translate(TimeSpan.Zero, deltaP);
                            manager?.NotifyObjectChanged(b.Object.Id);
                        }
                    }
                }
            }
        });
    }

    #endregion

    #region Grouping Operations

    /// <summary>
    /// Links currently selected objects in <paramref name="manager"/> as a link group (Ctrl+G).
    /// Elects the first selected object as parent, and remaining objects as children.
    /// Commits under <see cref="DrawingOperationKind.LinkObjects"/> transaction.
    /// </summary>
    public static bool GroupSelected(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
    {
        if (manager == null) return false;
        var selected = manager.SelectedObjects;
        if (selected.Count < 2) return false;

        var parentId = selected[0].Id;
        var childIds = selected.Skip(1).Select(o => o.Id).ToList();

        void PerformLink()
        {
            manager.LinkObjects(parentId, childIds);
        }

        if (coordinator != null)
        {
            var res = coordinator.ExecuteEdit(DrawingOperationKind.LinkObjects, PerformLink);
            return res.IsSuccess;
        }

        PerformLink();
        return true;
    }

    /// <summary>
    /// Dissolves link groups containing any currently selected objects in <paramref name="manager"/> (Ctrl+Shift+G).
    /// Commits under <see cref="DrawingOperationKind.UnlinkObjects"/> transaction.
    /// </summary>
    public static bool UngroupSelected(ChartObjectManager manager, IDrawingEditCoordinator? coordinator = null)
    {
        if (manager == null) return false;
        var selected = manager.SelectedObjects;
        if (selected.Count == 0) return false;

        var selectedIds = selected.Select(o => o.Id).ToList();

        void PerformUnlink()
        {
            manager.UnlinkObjects(selectedIds);
        }

        if (coordinator != null)
        {
            var res = coordinator.ExecuteEdit(DrawingOperationKind.UnlinkObjects, PerformUnlink);
            return res.IsSuccess;
        }

        PerformUnlink();
        return true;
    }

    #endregion
}
