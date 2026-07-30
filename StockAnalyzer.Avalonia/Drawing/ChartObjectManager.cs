using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Defines the context group for drawing objects.
/// Objects are isolated within these groups.
/// </summary>
public enum ChartDrawingContextType
{
    Standard,       // Candle, Bar, HeikinAshi
    Linear,         // Line, Area
    Kagi,
    Renko,
    PointAndFigure,
    ThreeLineBreak,
    ReverseWatch
}

/// <summary>
/// Chart object manager service (manages all drawable objects)
/// capable of switching contexts based on chart type.
/// </summary>
public class ChartObjectManager
{
    // Context Storage
    private readonly Dictionary<ChartDrawingContextType, List<IChartObject>> _contextStore 
        = new Dictionary<ChartDrawingContextType, List<IChartObject>>();

    // Current Context State
    private List<IChartObject> _currentObjects = new List<IChartObject>();
    private ChartDrawingContextType _currentContext = ChartDrawingContextType.Standard;

    private readonly HashSet<Guid> _selectedIds = new HashSet<Guid>();

    public ChartObjectManager()
    {
        // Initialize store
        foreach (ChartDrawingContextType type in Enum.GetValues(typeof(ChartDrawingContextType)))
        {
            _contextStore[type] = new List<IChartObject>();
        }
        _currentObjects = _contextStore[_currentContext];
    }

    /// <summary>All managed objects in current context (read-only)</summary>
    public IReadOnlyList<IChartObject> Objects => _currentObjects.AsReadOnly();

    /// <summary>Currently selected object (first one found in current context)</summary>
    public IChartObject? SelectedObject => _currentObjects.FirstOrDefault(o => o.IsSelected);
    
    /// <summary>active context type</summary>
    public ChartDrawingContextType CurrentContext => _currentContext;

    /// <summary>
    /// Switches the active drawing context.
    /// Persists objects of the previous context and loads objects of the new context.
    /// </summary>
    public void SwitchContext(ChartDrawingContextType newContext)
    {
        if (_currentContext == newContext) return;

        // Deselect all before switching to avoid phantom selections
        DeselectAll();

        _currentContext = newContext;
        
        if (!_contextStore.TryGetValue(newContext, out var objects))
        {
            objects = new List<IChartObject>();
            _contextStore[newContext] = objects;
        }
        
        _currentObjects = objects;
    }

    /// <summary>Add object to manager (Current Context)</summary>
    public void AddObject(IChartObject chartObject)
    {
        if (GetObject(chartObject.Id) != null) return; // Prevent duplicates in current context
        _currentObjects.Add(chartObject);
    }

    /// <summary>Remove object by ID (Current Context)</summary>
    public void RemoveObject(Guid id)
    {
        _currentObjects.RemoveAll(o => o.Id == id);
        _selectedIds.Remove(id);
    }

    /// <summary>Get object by ID (Current Context)</summary>
    public IChartObject? GetObject(Guid id)
    {
        return _currentObjects.FirstOrDefault(o => o.Id == id);
    }

    /// <summary>Hit test at screen coordinate (Z-order: top to bottom) (Current Context)</summary>
    public IChartObject? GetObjectAt(global::Avalonia.Point screenPoint, ICoordinateTransform transform)
    {
        // Filter visible and sort by ZIndex descending (top-most first)
        var sortedObjects = _currentObjects
            .Where(o => o.IsVisible)
            .OrderByDescending(o => o.ZIndex)
            .ToList();

        foreach (var obj in sortedObjects)
        {
            if (obj.HitTest(screenPoint, transform))
            {
                return obj;
            }
        }
        return null;
    }

    /// <summary>Select object by ID</summary>
    public void SelectObject(Guid id)
    {
        _selectedIds.Clear();
        _selectedIds.Add(id);
        UpdateSelectionState();
    }
    
    /// <summary>Deselect all objects</summary>
    public void DeselectAll()
    {
        _selectedIds.Clear();
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        foreach (var obj in _currentObjects)
        {
            obj.IsSelected = _selectedIds.Contains(obj.Id);
        }
    }

    /// <summary>Render all visible objects in current context sorted by ZIndex</summary>
    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        // Filter visible objects and sort by ZIndex (lower draws first = behind)
        var visibleObjects = _currentObjects
            .Where(o => o.IsVisible)
            .OrderBy(o => o.ZIndex)
            .ToList();
        
        foreach (var obj in visibleObjects)
        {
            obj.Render(canvas, transform);
        }
    }

    /// <summary>Clear all objects in ALL contexts (e.g. on new data load)</summary>
    public void Clear()
    {
        foreach (var contextList in _contextStore.Values)
        {
            contextList.Clear();
        }
        _currentObjects.Clear(); // Just in case reference drifts? No, they point to same list. 
        _selectedIds.Clear();
    }

    /// <summary>Get objects by type (Current Context)</summary>
    public IEnumerable<IChartObject> GetObjectsByType(ChartObjectType type)
    {
        return _currentObjects.Where(o => o.Type == type);
    }

    /// <summary>Get object count (Current Context)</summary>
    public int Count => _currentObjects.Count;
}

