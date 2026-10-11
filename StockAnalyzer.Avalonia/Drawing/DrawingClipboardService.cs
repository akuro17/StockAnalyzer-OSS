using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Services;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Cross-platform clipboard and duplication service for chart drawing objects.
/// Supports Copy (Ctrl+C), Paste (Ctrl+V with JSON payload, projection offset, and text fallback),
/// Cut (Ctrl+X), and in-place Duplicate (Ctrl+D bypassing OS clipboard).
/// All operations execute atomically via <see cref="IDrawingEditCoordinator"/>.
/// </summary>
public static class DrawingClipboardService
{
    /// <summary>
    /// Copies selected drawing objects into custom JSON format on the OS clipboard (Ctrl+C).
    /// </summary>
    public static async Task CopyAsync(
        IReadOnlyList<IChartObject> objects,
        ChartDrawingContextType context,
        string ticker,
        TimeframeType timeframe,
        ChartObjectManager manager,
        IClipboardService? clipboardService = null)
    {
        if (objects == null || objects.Count == 0 || manager == null) return;

        DateTime minTime = DateTime.MaxValue;
        DateTime maxTime = DateTime.MinValue;
        decimal minPrice = decimal.MaxValue;
        decimal maxPrice = decimal.MinValue;
        bool hasBounds = false;

        foreach (var obj in objects)
        {
            if (DrawingAlignHelper.TryGetObjectBounds(obj, out var tMin, out var tMax, out var pMin, out var pMax))
            {
                hasBounds = true;
                if (tMin < minTime) minTime = tMin;
                if (tMax > maxTime) maxTime = tMax;
                if (pMin < minPrice) minPrice = pMin;
                if (pMax > maxPrice) maxPrice = pMax;
            }
        }

        ChartPoint refAnchor = hasBounds
            ? new ChartPoint(minTime + TimeSpan.FromTicks((maxTime - minTime).Ticks / 2), minPrice + (maxPrice - minPrice) / 2m)
            : (objects[0].Points.Count > 0 ? objects[0].Points[0] : new ChartPoint(DateTime.UtcNow, 0m));

        var objectRecords = new List<DrawingObjectRecord>(objects.Count);
        var coordKind = DrawingDocumentCodec.GetCoordinateKind(context);

        foreach (var obj in objects)
        {
            var points = new List<DrawingStoredPoint>(obj.Points.Count);
            foreach (var pt in obj.Points)
            {
                points.Add(DrawingDocumentCodec.ConvertChartPointToStored(pt, coordKind));
            }

            var panel = obj.PanelIndex == -1 ? PanelKey.Main : PanelKey.OverlayGroup(obj.PanelIndex);
            var parameters = DrawingDocumentCodec.ExtractParameters(obj);

            var record = new DrawingObjectRecord(
                obj.Id,
                obj.GetType().Name,
                panel,
                coordKind,
                points,
                parameters
            );
            objectRecords.Add(record);
        }

        var selectedIdSet = new HashSet<Guid>(objects.Select(o => o.Id));
        var contextLinkGroups = manager.GetLinkGroupRecords(context);
        var relevantLinkGroups = new List<DrawingLinkGroupRecord>();

        foreach (var g in contextLinkGroups)
        {
            if (selectedIdSet.Contains(g.ParentObjectId))
            {
                var includedChildren = g.ChildObjectIds.Where(c => selectedIdSet.Contains(c)).ToList();
                if (includedChildren.Count > 0)
                {
                    relevantLinkGroups.Add(new DrawingLinkGroupRecord(g.ParentObjectId, includedChildren));
                }
            }
        }

        var payload = new DrawingClipboardPayload(
            ticker ?? string.Empty,
            timeframe,
            context,
            refAnchor,
            objectRecords,
            relevantLinkGroups
        );

        string json = JsonSerializer.Serialize(payload, DrawingDocumentCodec.SerializerOptions);

        if (clipboardService != null)
        {
            await clipboardService.SetTextAsync(json);
        }
        else
        {
            var service = new StockAnalyzer.Avalonia.Services.ClipboardService();
            await service.SetTextAsync(json);
        }
    }

    /// <summary>
    /// Attempts to paste clipboard content onto the chart (Ctrl+V).
    /// Resolves priorities:
    /// Branch A: Custom Drawing Payload (re-maps Guids, applies screen/mouse/cross-chart offsets, restores link groups).
    /// Branch B: Plain Text Annotation (creates TextObject).
    /// </summary>
    public static async Task<bool> TryPasteAsync(
        ChartViewModel viewModel,
        Point? mouseScreenPos,
        ICoordinateTransform transform,
        IClipboardService? clipboardService = null)
    {
        if (viewModel?.ObjectManager == null || transform == null) return false;

        string? text = null;
        if (clipboardService != null)
        {
            text = await clipboardService.GetTextAsync();
        }
        else
        {
            var service = new StockAnalyzer.Avalonia.Services.ClipboardService();
            text = await service.GetTextAsync();
        }

        if (string.IsNullOrWhiteSpace(text)) return false;

        // Branch A: Custom Drawing Payload or Valid JSON
        DrawingClipboardPayload? payload = null;
        try
        {
            if (text.Contains("\"objects\"") || text.Contains("\"sourceTicker\"") || text.Contains(DrawingClipboardPayload.SchemaVersion))
            {
                payload = JsonSerializer.Deserialize<DrawingClipboardPayload>(text, DrawingDocumentCodec.SerializerOptions);
            }
        }
        catch
        {
            payload = null;
        }

        if (payload != null && payload.Objects.Count > 0)
        {
            TimeSpan timeOffset;
            decimal priceOffset;

            if (mouseScreenPos.HasValue)
            {
                var targetAnchor = transform.ScreenToChart(mouseScreenPos.Value);
                timeOffset = targetAnchor.Time - payload.ReferenceAnchor.Time;
                priceOffset = targetAnchor.Price - payload.ReferenceAnchor.Price;
            }
            else
            {
                // Default +20px, +20px screen projection per Section 1.2.6
                var s0 = transform.ChartToScreen(payload.ReferenceAnchor);
                var s1 = new Point(s0.X + 20.0, s0.Y + 20.0);
                var p1 = transform.ScreenToChart(s1);
                timeOffset = p1.Time - payload.ReferenceAnchor.Time;
                priceOffset = p1.Price - payload.ReferenceAnchor.Price;
            }

            // Cross-Chart Centering Projection:
            if (!string.IsNullOrEmpty(payload.SourceTicker) && !string.Equals(payload.SourceTicker, viewModel.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                var centerScreen = new Point(transform.CanvasWidth / 2.0, transform.CanvasHeight / 2.0);
                var centerChart = transform.ScreenToChart(centerScreen);
                timeOffset = centerChart.Time - payload.ReferenceAnchor.Time;
                priceOffset = centerChart.Price - payload.ReferenceAnchor.Price;
            }

            var idMap = new Dictionary<Guid, Guid>();
            var newObjects = new List<IChartObject>();

            foreach (var record in payload.Objects)
            {
                var concreteType = ChartObjectTypeRegistry.Resolve(record.TypeName);
                if (concreteType == null) continue;

                var instance = ChartObjectInstanceFactory.Create(concreteType);
                instance.Points.Clear();

                foreach (var storedPt in record.Points)
                {
                    var pt = DrawingDocumentCodec.ConvertStoredToChartPoint(storedPt, record.CoordinateKind);
                    instance.Points.Add(new ChartPoint(pt.Time + timeOffset, pt.Price + priceOffset));
                }

                DrawingDocumentCodec.ApplyParameters(instance, concreteType, record.StyleAndParameters);

                instance.IsVisible = true;
                instance.IsLocked = false;
                instance.PanelIndex = record.Panel.Kind == PanelKind.Main ? -1 : (record.Panel.OverlayPanelId ?? -1);

                idMap[record.ObjectId] = instance.Id;
                newObjects.Add(instance);
            }

            if (newObjects.Count == 0) return false;

            void PerformPaste()
            {
                for (int i = 0; i < newObjects.Count; i++)
                {
                    viewModel.ObjectManager.AddObject(newObjects[i]);
                }

                foreach (var g in payload.LinkGroups)
                {
                    if (idMap.TryGetValue(g.ParentObjectId, out var newParent))
                    {
                        var newChildren = g.ChildObjectIds
                            .Where(c => idMap.ContainsKey(c))
                            .Select(c => idMap[c])
                            .ToList();
                        if (newChildren.Count > 0)
                        {
                            viewModel.ObjectManager.LinkObjects(newParent, newChildren);
                        }
                    }
                }

                viewModel.ObjectManager.SelectObjects(newObjects.Select(o => o.Id), clearExisting: true);
            }

            if (viewModel.DrawingEditCoordinator != null)
            {
                viewModel.DrawingEditCoordinator.ExecuteEdit(DrawingOperationKind.Add, PerformPaste);
            }
            else
            {
                PerformPaste();
            }

            return true;
        }

        // Branch B: Plain Text Annotation
        var cleanText = text.Trim();
        if (!string.IsNullOrEmpty(cleanText))
        {
            var spawnPoint = mouseScreenPos.HasValue
                ? transform.ScreenToChart(mouseScreenPos.Value)
                : transform.ScreenToChart(new Point(transform.CanvasWidth / 2.0, transform.CanvasHeight / 2.0));

            var textObj = new TextObject(spawnPoint, cleanText);

            void PerformTextPaste()
            {
                viewModel.ObjectManager.AddObject(textObj);
                if (viewModel.LayerService != null)
                {
                    viewModel.LayerService.AddObjectToLayer(textObj.Id, PanelKey.Main);
                }
                viewModel.ObjectManager.SelectObject(textObj.Id);
            }

            if (viewModel.DrawingEditCoordinator != null)
            {
                viewModel.DrawingEditCoordinator.ExecuteEdit(DrawingOperationKind.Add, PerformTextPaste);
            }
            else
            {
                PerformTextPaste();
            }

            viewModel.RequestRender(RenderReason.DataChanged);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Copies selected objects to clipboard, then deletes them from the chart under a single transaction (Ctrl+X).
    /// Respects locked object protection.
    /// </summary>
    public static async Task CutAsync(
        ChartViewModel viewModel,
        ICoordinateTransform? transform = null,
        IClipboardService? clipboardService = null)
    {
        if (viewModel?.ObjectManager == null) return;

        var selected = viewModel.ObjectManager.SelectedObjects.ToList();
        if (selected.Count == 0) return;

        await CopyAsync(
            selected,
            viewModel.ObjectManager.CurrentContext,
            viewModel.Symbol,
            viewModel.SelectedTimeFrame,
            viewModel.ObjectManager,
            clipboardService);

        var toDelete = selected.Where(o => !o.IsLocked && !viewModel.ObjectManager.IsLocked(o.Id)).ToList();
        if (toDelete.Count == 0) return;

        void PerformCut()
        {
            for (int i = 0; i < toDelete.Count; i++)
            {
                var id = toDelete[i].Id;
                viewModel.ObjectManager.RemoveObject(id);
                viewModel.LayerService?.RemoveObjectFromLayer(id);
            }
            viewModel.ObjectManager.DeselectAll();
        }

        if (viewModel.DrawingEditCoordinator != null)
        {
            viewModel.DrawingEditCoordinator.ExecuteEdit(DrawingOperationKind.Delete, PerformCut);
        }
        else
        {
            PerformCut();
        }
    }

    /// <summary>
    /// Duplicates currently selected objects in-place with a (+20px, +20px) offset (Ctrl+D).
    /// Bypasses OS clipboard completely, preserving its current contents.
    /// </summary>
    public static void DuplicateSelected(
        ChartViewModel viewModel,
        ICoordinateTransform? transform = null)
    {
        if (viewModel?.ObjectManager == null) return;

        var selected = viewModel.ObjectManager.SelectedObjects.ToList();
        if (selected.Count == 0) return;

        TimeSpan deltaT = TimeSpan.FromMinutes(20);
        decimal deltaP = 0m;

        if (transform != null && selected[0].Points.Count > 0)
        {
            var p0 = selected[0].Points[0];
            var s0 = transform.ChartToScreen(p0);
            var s1 = new Point(s0.X + 20.0, s0.Y + 20.0);
            var p1 = transform.ScreenToChart(s1);
            deltaT = p1.Time - p0.Time;
            deltaP = p1.Price - p0.Price;
        }
        else if (selected[0].Points.Count > 0)
        {
            deltaP = selected[0].Points[0].Price * 0.01m;
        }

        var selectedIds = selected.Select(o => o.Id).ToList();

        void PerformDuplicate()
        {
            viewModel.ObjectManager.DuplicateObjects(selectedIds, deltaT, deltaP);
        }

        if (viewModel.DrawingEditCoordinator != null)
        {
            viewModel.DrawingEditCoordinator.ExecuteEdit(DrawingOperationKind.Duplicate, PerformDuplicate);
        }
        else
        {
            PerformDuplicate();
        }
    }
}
