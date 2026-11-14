using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Singleton service managing per-tool prototype presets and property transfer for chart drawing tools.
/// </summary>
public sealed class DrawingToolPresetService : IDrawingToolPresetService
{
    public static readonly DrawingToolPresetService Shared = new();

    private readonly ConcurrentDictionary<DrawingTool, IChartObject> _presetCache = new();

    private static readonly HashSet<string> ExcludedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "Points",
        "IsSelected",
        "SkiaColor",
        "IsVisible",
        "IsLocked",
        "ZIndex",
        "Name",
        "ParentPanelId",
        "LinkGroupId",
        "IsLinkParent",
        "AnchorPointIndex",
        "PanelIndex"
    };

    public event Action<DrawingTool, IChartObject>? PresetChanged;

    public IChartObject? GetOrCreatePresetObject(DrawingTool tool)
    {
        if (tool is DrawingTool.Pointer or DrawingTool.Eraser or DrawingTool.Ruler or DrawingTool.Eyedropper)
        {
            return null;
        }

        return _presetCache.GetOrAdd(tool, CreatePrototypeForTool);
    }

    public void ApplyPresetToNewObject(DrawingTool tool, IChartObject target)
    {
        if (target == null) return;

        var preset = GetOrCreatePresetObject(tool);
        if (preset == null || ReferenceEquals(preset, target)) return;

        // Common base styles
        target.Color = preset.Color;

        if (double.IsFinite(preset.Thickness))
        {
            target.Thickness = Math.Clamp(preset.Thickness, 0.5, 50.0);
        }

        target.MoveAxisMode = preset.MoveAxisMode;

        if (preset is RelativeGeometricRenderer presetRenderer && target is RelativeGeometricRenderer targetRenderer)
        {
            targetRenderer.StrokeStyle = presetRenderer.StrokeStyle;
        }

        // Only transfer tool-specific properties if the types match
        if (preset.GetType() != target.GetType()) return;

        var properties = preset.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                continue;

            string propName = prop.Name;
            if (ExcludedPropertyNames.Contains(propName) || DrawingParameterTags.IgnoredPropertyNames.Contains(propName))
                continue;

            if (propName.Equals(nameof(IChartObject.Color), StringComparison.OrdinalIgnoreCase) ||
                propName.Equals(nameof(IChartObject.Thickness), StringComparison.OrdinalIgnoreCase) ||
                propName.Equals(nameof(IChartObject.MoveAxisMode), StringComparison.OrdinalIgnoreCase) ||
                propName.Equals(nameof(RelativeGeometricRenderer.StrokeStyle), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var val = prop.GetValue(preset);
                prop.SetValue(target, val);
            }
            catch
            {
                // Preserve robustness against properties throwing on get/set
            }
        }
    }

    public void NotifyPresetModified(DrawingTool tool)
    {
        if (_presetCache.TryGetValue(tool, out var preset))
        {
            PresetChanged?.Invoke(tool, preset);
        }
    }

    private static IChartObject CreatePrototypeForTool(DrawingTool tool)
    {
        switch (tool)
        {
            case DrawingTool.Freehand:
                return new FreehandObject
                {
                    Color = DrawingThemeContext.DefaultColor,
                    Thickness = DrawingThemeContext.DefaultStrokeThickness
                };

            case DrawingTool.Highlighter:
                return new HighlighterObject
                {
                    Color = DrawingThemeContext.DefaultColor,
                    Thickness = 12.0
                };

            case DrawingTool.Text:
                return new TextObject(default, "Text");

            case DrawingTool.Information:
                return new InformationObject
                {
                    Color = DrawingThemeContext.DefaultColor,
                    Thickness = DrawingThemeContext.DefaultStrokeThickness
                };

            case DrawingTool.ExtendedLine:
                // Same object as the Trend Line tool; only the preset's initial ends differ (a user-edited preset wins).
                var extendedLine = CreateFromBehavior(tool);
                if (extendedLine is TrendLineObject extendedTrend)
                {
                    extendedTrend.ExtendMode = TrendLineObject.ExtendedLineToolDefaultMode;
                }
                return extendedLine;

            default:
                return CreateFromBehavior(tool);
        }
    }

    private static IChartObject CreateFromBehavior(DrawingTool tool)
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(tool);
        var obj = behavior?.CreateObject(default, null);
        if (obj == null)
        {
            // Fallback to minimal FreehandObject if behavior is not found
            return new FreehandObject
            {
                Color = DrawingThemeContext.DefaultColor,
                Thickness = DrawingThemeContext.DefaultStrokeThickness
            };
        }

        if (obj.Color.A == 0 && obj.Color.R == 0 && obj.Color.G == 0 && obj.Color.B == 0)
        {
            obj.Color = DrawingThemeContext.DefaultColor;
        }
        if (obj.Thickness <= 0)
        {
            obj.Thickness = DrawingThemeContext.DefaultStrokeThickness;
        }
        return obj;
    }
}
