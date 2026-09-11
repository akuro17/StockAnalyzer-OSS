using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Resolves the <see cref="IDrawingSettingsPanelDefinition"/> for a drawing object's concrete type.
/// Mirrors the ITabRegistry/IPanelTabFactory pattern already used for dynamic workspace tab
/// resolution (see SA_ARCHITECTURE_RULES.md, "Dynamic UI Tab Management").
/// </summary>
public interface IDrawingSettingsPanelRegistry
{
    void Register(IDrawingSettingsPanelDefinition definition);

    /// <summary>Returns the first registered definition that can handle the drawing, or null if
    /// none is registered for its type (the dialog then shows only the generic Color/Thickness
    /// panel, with no tool-specific fields).</summary>
    IDrawingSettingsPanelDefinition? Resolve(IChartObject drawing);
}

public sealed class DrawingSettingsPanelRegistry : IDrawingSettingsPanelRegistry
{
    private readonly List<IDrawingSettingsPanelDefinition> _definitions = new();
    private readonly object _gate = new();

    public void Register(IDrawingSettingsPanelDefinition definition)
    {
        lock (_gate)
        {
            _definitions.Add(definition);
        }
    }

    public IDrawingSettingsPanelDefinition? Resolve(IChartObject drawing)
    {
        lock (_gate)
        {
            foreach (var definition in _definitions)
            {
                if (definition.CanHandle(drawing))
                {
                    return definition;
                }
            }
        }
        return null;
    }
}
