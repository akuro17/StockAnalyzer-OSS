using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.ViewModels.Chart;

/// <summary>
/// ViewModel managing the floating contextual styling palette (FloatingStyleBar).
/// Provides instantaneous quick-styling (Color, Thickness, Opacity, Stroke Style, Z-Order, Lock, Delete)
/// anchored directly above selected drawing objects' screen AABB, committing mutations
/// atomically through <see cref="IDrawingEditCoordinator"/>.
/// </summary>
public partial class FloatingStyleBarViewModel : ObservableObject
{
    public const double DefaultBarWidth = 360.0;
    public const double DefaultBarHeight = 44.0;
    public const double SpacingMargin = 12.0;

    public static readonly IReadOnlyList<Color> PresetColors = new List<Color>
    {
        Color.Parse("#FF1744"), // Red
        Color.Parse("#2979FF"), // Blue
        Color.Parse("#00E676"), // Green
        Color.Parse("#FFEA00"), // Yellow
        Color.Parse("#FF9100"), // Orange
        Color.Parse("#D500F9"), // Purple
        Color.Parse("#FFFFFF"), // White
        Color.Parse("#37474F")  // Dark Slate
    };

    public FloatingStyleBarViewModel(ChartViewModel? chartViewModel = null)
    {
        _chartViewModel = chartViewModel;
    }

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    private double _positionX;
    public double PositionX
    {
        get => _positionX;
        set => SetProperty(ref _positionX, value);
    }

    private double _positionY;
    public double PositionY
    {
        get => _positionY;
        set => SetProperty(ref _positionY, value);
    }

    private Color _selectedColor = PresetColors[0];
    public Color SelectedColor
    {
        get => _selectedColor;
        set => SetProperty(ref _selectedColor, value);
    }

    private double _selectedThickness = 2.0;
    public double SelectedThickness
    {
        get => _selectedThickness;
        set => SetProperty(ref _selectedThickness, value);
    }

    private double _selectedOpacity = 1.0;
    public double SelectedOpacity
    {
        get => _selectedOpacity;
        set => SetProperty(ref _selectedOpacity, value);
    }

    private DrawingStrokeStyle _selectedStrokeStyle = DrawingStrokeStyle.Solid;
    public DrawingStrokeStyle SelectedStrokeStyle
    {
        get => _selectedStrokeStyle;
        set => SetProperty(ref _selectedStrokeStyle, value);
    }

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set => SetProperty(ref _isLocked, value);
    }

    private ChartViewModel? _chartViewModel;
    private IReadOnlyList<IChartObject> _currentSelectedObjects = Array.Empty<IChartObject>();
    private ICoordinateTransform? _lastTransform;

    public void Attach(ChartViewModel? chartViewModel)
    {
        _chartViewModel = chartViewModel;
    }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Recalculates floating bar position and updates bound styling controls from selected objects.
    /// Clamps position to keep the palette fully within the viewport bounds.
    /// </summary>
    public void UpdateSelection(
        IReadOnlyList<IChartObject> selectedObjects,
        ICoordinateTransform? transform,
        Rect viewportBounds)
    {
        _lastTransform = transform;
        if (!IsEnabled || selectedObjects == null || selectedObjects.Count == 0 || transform == null || viewportBounds.Width <= 0 || viewportBounds.Height <= 0)
        {
            _currentSelectedObjects = Array.Empty<IChartObject>();
            IsVisible = false;
            return;
        }

        _currentSelectedObjects = selectedObjects;

        // Calculate combined screen bounding box of selected objects
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;

        for (int i = 0; i < selectedObjects.Count; i++)
        {
            var obj = selectedObjects[i];
            if (obj.Points == null || obj.Points.Count == 0) continue;

            for (int j = 0; j < obj.Points.Count; j++)
            {
                var pt = transform.ChartToScreen(obj.Points[j]);
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        if (minX > maxX || minY > maxY)
        {
            IsVisible = false;
            return;
        }

        var bounds = new Rect(minX, minY, Math.Max(10.0, maxX - minX), Math.Max(10.0, maxY - minY));

        // Anchor above the top-center of the bounding box
        double barWidth = DefaultBarWidth;
        double barHeight = DefaultBarHeight;

        double targetX = bounds.Center.X - (barWidth / 2.0);
        double targetY = bounds.Top - barHeight - SpacingMargin;

        // If placed above the top of viewport, flip to bottom
        if (targetY < 10.0)
        {
            targetY = bounds.Bottom + SpacingMargin;
        }

        // Clamp inside viewport
        double clampedX = Math.Clamp(targetX, 10.0, Math.Max(10.0, viewportBounds.Width - barWidth - 10.0));
        double clampedY = Math.Clamp(targetY, 10.0, Math.Max(10.0, viewportBounds.Height - barHeight - 10.0));

        PositionX = clampedX;
        PositionY = clampedY;

        // Sync initial property displays from the first selected object
        var first = selectedObjects[0];
        ReadObjectProperties(first);

        IsVisible = true;
    }

    private void ReadObjectProperties(IChartObject obj)
    {
        var type = obj.GetType();

        var colorProp = type.GetProperty("Color", BindingFlags.Public | BindingFlags.Instance);
        if (colorProp?.GetValue(obj) is Color col)
        {
            SelectedColor = col;
            SelectedOpacity = Math.Round((double)col.A / 255.0, 2);
        }

        var thickProp = type.GetProperty("Thickness", BindingFlags.Public | BindingFlags.Instance);
        if (thickProp?.GetValue(obj) is double thick)
        {
            SelectedThickness = thick;
        }

        var styleProp = type.GetProperty("StrokeStyle", BindingFlags.Public | BindingFlags.Instance);
        if (styleProp?.GetValue(obj) is DrawingStrokeStyle strokeStyle)
        {
            SelectedStrokeStyle = strokeStyle;
        }

        IsLocked = _currentSelectedObjects.All(o => o.IsLocked);
    }

    [RelayCommand]
    public void SetColor(object? colorParam)
    {
        Color color;
        if (colorParam is Color c)
        {
            color = c;
        }
        else if (colorParam is ISolidColorBrush b)
        {
            color = b.Color;
        }
        else if (colorParam is string s && Color.TryParse(s, out var parsed))
        {
            color = parsed;
        }
        else
        {
            return;
        }

        SelectedColor = color;
        DrawingThemeContext.SetDefaultColor(new SKColor(color.R, color.G, color.B, color.A));

        MutateSelectedObjects(DrawingOperationKind.StyleChange, obj =>
        {
            var type = obj.GetType();
            var prop = type.GetProperty("Color", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(Color))
            {
                // Preserve current alpha when changing RGB
                byte currentA = SelectedColor.A;
                prop.SetValue(obj, Color.FromArgb(currentA, color.R, color.G, color.B));
            }
        });
    }

    [RelayCommand]
    public void SetThickness(double thickness)
    {
        SelectedThickness = thickness;

        MutateSelectedObjects(DrawingOperationKind.StyleChange, obj =>
        {
            var type = obj.GetType();
            var prop = type.GetProperty("Thickness", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(double))
            {
                prop.SetValue(obj, thickness);
            }
        });
    }

    [RelayCommand]
    public void SetOpacity(double opacity)
    {
        SelectedOpacity = opacity;
        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);

        MutateSelectedObjects(DrawingOperationKind.StyleChange, obj =>
        {
            var type = obj.GetType();

            // Check for specific Alpha property (e.g. HighlighterObject)
            var alphaProp = type.GetProperty("Alpha", BindingFlags.Public | BindingFlags.Instance);
            if (alphaProp != null && alphaProp.CanWrite && alphaProp.PropertyType == typeof(int))
            {
                alphaProp.SetValue(obj, (int)alpha);
                return;
            }

            var colorProp = type.GetProperty("Color", BindingFlags.Public | BindingFlags.Instance);
            if (colorProp != null && colorProp.CanWrite && colorProp.PropertyType == typeof(Color))
            {
                var cur = (Color)colorProp.GetValue(obj)!;
                colorProp.SetValue(obj, Color.FromArgb(alpha, cur.R, cur.G, cur.B));
            }
        });
    }

    [RelayCommand]
    public void SetStrokeStyle(DrawingStrokeStyle strokeStyle)
    {
        SelectedStrokeStyle = strokeStyle;

        MutateSelectedObjects(DrawingOperationKind.StyleChange, obj =>
        {
            var type = obj.GetType();
            var prop = type.GetProperty("StrokeStyle", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(DrawingStrokeStyle))
            {
                prop.SetValue(obj, strokeStyle);
            }
        });
    }

    [RelayCommand]
    public void BringToFront()
    {
        if (_chartViewModel == null || _currentSelectedObjects.Count == 0) return;

        var coordinator = _chartViewModel.DrawingEditCoordinator;
        var manager = _chartViewModel.ObjectManager;

        void Action()
        {
            for (int i = 0; i < _currentSelectedObjects.Count; i++)
            {
                manager.BringToFront(_currentSelectedObjects[i].Id);
            }
        }

        if (coordinator != null)
        {
            coordinator.ExecuteEdit(DrawingOperationKind.ZOrderChange, Action);
        }
        else
        {
            Action();
        }
    }

    [RelayCommand]
    public void SendToBack()
    {
        if (_chartViewModel == null || _currentSelectedObjects.Count == 0) return;

        var coordinator = _chartViewModel.DrawingEditCoordinator;
        var manager = _chartViewModel.ObjectManager;

        void Action()
        {
            // Reverse iteration to maintain relative order when inserting at index 0
            for (int i = _currentSelectedObjects.Count - 1; i >= 0; i--)
            {
                manager.SendToBack(_currentSelectedObjects[i].Id);
            }
        }

        if (coordinator != null)
        {
            coordinator.ExecuteEdit(DrawingOperationKind.ZOrderChange, Action);
        }
        else
        {
            Action();
        }
    }

    [RelayCommand]
    public void ToggleLock()
    {
        bool targetLocked = !IsLocked;
        IsLocked = targetLocked;

        MutateSelectedObjects(DrawingOperationKind.LockToggle, obj =>
        {
            obj.IsLocked = targetLocked;
        });
    }

    [RelayCommand]
    public void Delete()
    {
        if (_chartViewModel == null || _currentSelectedObjects.Count == 0) return;

        var coordinator = _chartViewModel.DrawingEditCoordinator;
        var manager = _chartViewModel.ObjectManager;
        var layerService = _chartViewModel.LayerService;
        var objectsToDelete = _currentSelectedObjects.ToList();

        void Action()
        {
            for (int i = 0; i < objectsToDelete.Count; i++)
            {
                var obj = objectsToDelete[i];
                manager.RemoveObject(obj.Id);
                layerService?.RemoveObjectFromLayer(obj.Id);
            }
            manager.DeselectAll();
        }

        if (coordinator != null)
        {
            coordinator.ExecuteEdit(DrawingOperationKind.Delete, Action);
        }
        else
        {
            Action();
        }

        IsVisible = false;
    }

    [RelayCommand]
    public void Group()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.GroupSelected(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void Ungroup()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.UngroupSelected(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void ActivateEyedropper()
    {
        if (_chartViewModel != null)
        {
            _chartViewModel.CurrentTool = DrawingTool.Eyedropper;
        }
    }

    [RelayCommand]
    public void AlignLeft()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.AlignLeft(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void AlignRight()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.AlignRight(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void AlignCenterHorizontal()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.AlignCenterHorizontal(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void AlignTop()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.AlignTop(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void AlignBottom()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.AlignBottom(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void AlignCenterVertical()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.AlignCenterVertical(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void DistributeHorizontal()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.DistributeHorizontal(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public void DistributeVertical()
    {
        if (_chartViewModel?.ObjectManager == null) return;
        DrawingAlignHelper.DistributeVertical(_chartViewModel.ObjectManager, _chartViewModel.DrawingEditCoordinator);
    }

    [RelayCommand]
    public async Task CopyAsync()
    {
        if (_chartViewModel?.ObjectManager == null || _currentSelectedObjects.Count == 0) return;
        await DrawingClipboardService.CopyAsync(
            _currentSelectedObjects,
            _chartViewModel.ObjectManager.CurrentContext,
            _chartViewModel.Symbol,
            _chartViewModel.SelectedTimeFrame,
            _chartViewModel.ObjectManager);
    }

    [RelayCommand]
    public async Task CutAsync()
    {
        if (_chartViewModel == null) return;
        await DrawingClipboardService.CutAsync(_chartViewModel, _lastTransform);
        _chartViewModel.RequestRender(RenderReason.DataChanged);
        IsVisible = false;
    }

    [RelayCommand]
    public void Duplicate()
    {
        if (_chartViewModel == null) return;
        DrawingClipboardService.DuplicateSelected(_chartViewModel, _lastTransform);
        _chartViewModel.RequestRender(RenderReason.DataChanged);
    }

    private void MutateSelectedObjects(DrawingOperationKind kind, Action<IChartObject> mutation)
    {
        if (_currentSelectedObjects.Count == 0) return;

        var coordinator = _chartViewModel?.DrawingEditCoordinator;
        var manager = _chartViewModel?.ObjectManager;

        void ExecuteMutation()
        {
            for (int i = 0; i < _currentSelectedObjects.Count; i++)
            {
                var obj = _currentSelectedObjects[i];
                mutation(obj);
                manager?.NotifyObjectChanged(obj.Id);
            }
        }

        if (coordinator != null)
        {
            coordinator.ExecuteEdit(kind, ExecuteMutation);
        }
        else
        {
            ExecuteMutation();
        }
    }
}
