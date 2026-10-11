using System;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.ViewModels.Chart;

/// <summary>
/// ViewModel coordinating direct inline in-place text editing for <see cref="TextObject"/>.
/// Provides overlay positioning over the Skia text object, suppresses Skia drawing during active typing,
/// and executes single-transaction commit or cancellation via <see cref="IDrawingEditCoordinator"/>.
/// </summary>
public partial class InPlaceTextEditViewModel : ObservableObject
{
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
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

    private double _width = 160.0;
    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    private double _height = 40.0;
    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    private double _fontSize = 14.0;
    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, value);
    }

    private Color _textColor = Colors.Black;
    public Color TextColor
    {
        get => _textColor;
        set => SetProperty(ref _textColor, value);
    }

    public TextObject? EditingObject { get; private set; }
    public bool IsNewObject { get; private set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Begins in-place inline editing over the specified TextObject.
    /// </summary>
    public void BeginEdit(
        TextObject target,
        Point screenPoint,
        double width = 160.0,
        double height = 40.0,
        bool isNew = false)
    {
        if (!IsEnabled || target == null) return;

        EditingObject = target;
        IsNewObject = isNew;
        Text = target.Text ?? string.Empty;
        FontSize = target.FontSize > 0 ? target.FontSize : 14.0;
        TextColor = target.Color;

        Width = Math.Max(120.0, width);
        Height = Math.Max(36.0, height);

        PositionX = Math.Max(0.0, screenPoint.X - (Width / 2.0));
        PositionY = Math.Max(0.0, screenPoint.Y - (Height / 2.0));

        // Suppress Skia text drawing to prevent visual duplication during typing
        target.IsEditing = true;
        IsEditing = true;
    }

    /// <summary>
    /// Commits edited text into the TextObject via IDrawingEditCoordinator.
    /// </summary>
    public void Commit(
        IDrawingEditCoordinator? coordinator,
        ChartObjectManager? objectManager,
        IDrawingLayerService? layerService,
        Action? onCompleted = null)
    {
        if (!IsEditing || EditingObject == null) return;

        var target = EditingObject;
        string committedText = Text?.Trim() ?? string.Empty;
        bool isNew = IsNewObject;

        // Reset editing state
        target.IsEditing = false;
        IsEditing = false;
        EditingObject = null;

        // If new object and empty text was entered, discard it
        if (isNew && string.IsNullOrEmpty(committedText))
        {
            onCompleted?.Invoke();
            return;
        }

        void ApplyCommit()
        {
            target.Text = committedText;
            target.InvalidateCache();

            if (isNew && objectManager != null)
            {
                objectManager.AddObject(target);
                if (layerService != null)
                {
                    var targetPanel = target.PanelIndex >= 0
                        ? PanelKey.OverlayGroup(target.PanelIndex)
                        : PanelKey.Main;
                    layerService.AddObjectToLayer(target.Id, targetPanel);
                }
            }

            objectManager?.NotifyObjectChanged(target.Id);
        }

        if (coordinator != null)
        {
            var opKind = isNew ? DrawingOperationKind.Add : DrawingOperationKind.SettingsApply;
            coordinator.ExecuteEdit(opKind, ApplyCommit);
        }
        else
        {
            ApplyCommit();
        }

        onCompleted?.Invoke();
    }

    /// <summary>
    /// Cancels active editing without saving changes. Discards new objects.
    /// </summary>
    public void Cancel(
        ChartObjectManager? objectManager,
        Action? onCompleted = null)
    {
        if (!IsEditing || EditingObject == null) return;

        var target = EditingObject;
        target.IsEditing = false;
        IsEditing = false;
        EditingObject = null;

        objectManager?.NotifyObjectChanged(target.Id);
        onCompleted?.Invoke();
    }
}
