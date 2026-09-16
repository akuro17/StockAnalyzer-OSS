using System;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Adapter mediating Avalonia pointer events and the FreehandInputController.
/// Handles pointer routing, preview lifecycle, layer-lock checks, and transactional persistence.
/// </summary>
public sealed class FreehandPointerAdapter
{
    private readonly FreehandInputController _controller;
    private DrawingEditToken? _activeEditToken;
    private IDrawingEditCoordinator? _activeCoordinator;
    private ICoordinateTransform? _frozenTransform;

    public FreehandPointerAdapter(int capacity = DrawingInteractionLimits.MaxStrokeSamples)
    {
        _controller = new FreehandInputController(capacity);
    }

    public FreehandPointerAdapter(FreehandInputController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <summary>
    /// Gets the underlying input controller.
    /// </summary>
    public FreehandInputController Controller => _controller;

    /// <summary>
    /// Gets the temporary preview object currently being drawn, or null if idle.
    /// </summary>
    public FreehandObject? PreviewObject { get; private set; }

    /// <summary>
    /// Gets whether a freehand stroke is actively in progress.
    /// </summary>
    public bool IsActive => _controller.IsDrawing;

    /// <summary>
    /// Handles the pointer pressed event when the Freehand tool is active.
    /// </summary>
    public bool HandlePointerPressed(
        Point position,
        Point chartPosition,
        long pointerId,
        ChartViewModel? viewModel,
        ICoordinateTransform transform,
        int panelIndex,
        PanelKey panelKey)
    {
        if (viewModel?.CurrentTool != DrawingTool.Freehand)
        {
            return false;
        }

        // Enforce single-pointer ownership: reject if already actively drawing
        if (_controller.IsDrawing || _activeEditToken.HasValue)
        {
            return false;
        }

        // Validate screen and chart coordinates are finite
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            !double.IsFinite(chartPosition.X) || !double.IsFinite(chartPosition.Y))
        {
            return false;
        }

        // Layer visibility and edit-lock check: prevent drawing on hidden or edit-locked layers
        if (viewModel.LayerService != null)
        {
            var activeLayerId = viewModel.LayerService.GetActiveLayerId(panelKey);
            if (activeLayerId.HasValue)
            {
                var layer = viewModel.LayerService.GetLayer(activeLayerId.Value);
                if (layer.HasValue && (!layer.Value.IsVisible || layer.Value.IsEditLocked))
                {
                    return true; // Reject drawing on hidden/locked layer; consume event to suppress drawing
                }
            }
        }

        // Acquire transactional edit token early
        DrawingEditToken? token = null;
        if (viewModel.DrawingEditCoordinator != null)
        {
            var begin = viewModel.DrawingEditCoordinator.BeginEdit(DrawingOperationKind.Add);
            if (!begin.IsSuccess)
            {
                return false;
            }
            token = begin.Token;
            _activeEditToken = token;
            _activeCoordinator = viewModel.DrawingEditCoordinator;
        }

        try
        {
            // Deselect any existing selected object before initiating a new stroke
            viewModel.ObjectManager?.DeselectAll();

            var sample = new FreehandInputSample(pointerId, position.X, position.Y);
            var chartPoint = transform.ScreenToChart(chartPosition);

            var result = _controller.Begin(sample, chartPoint);
            if (result == DrawingInputResult.Started)
            {
                _frozenTransform = transform;

                // Lock style at press time and pre-allocate stroke capacity
                var strokeColor = DrawingThemeContext.DefaultColor;
                var strokeThickness = DrawingThemeContext.DefaultStrokeThickness;

                PreviewObject = new FreehandObject
                {
                    Color = strokeColor,
                    Thickness = strokeThickness,
                    PanelIndex = panelIndex
                };
                PreviewObject.Points.Capacity = DrawingInteractionLimits.MaxStrokeSamples;
                PreviewObject.Points.Add(chartPoint);
                return true;
            }

            CancelActiveTransaction();
            return false;
        }
        catch
        {
            CancelActiveTransaction();
            throw;
        }
    }

    /// <summary>
    /// Handles the pointer moved event during an active freehand stroke.
    /// </summary>
    public bool HandlePointerMoved(
        Point position,
        Point chartPosition,
        long pointerId,
        ICoordinateTransform transform,
        out bool needsInvalidate)
    {
        needsInvalidate = false;

        if (!_controller.IsDrawing)
        {
            return false;
        }

        // Ignore events from non-owner pointers without mutating stroke state
        if (pointerId != _controller.OwnedPointerId)
        {
            return false;
        }

        // Validate coordinate bounds: abort cleanly if coordinates are non-finite
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            !double.IsFinite(chartPosition.X) || !double.IsFinite(chartPosition.Y))
        {
            HandleCancel(DrawingCancelReason.CaptureLost, out needsInvalidate);
            return true;
        }

        try
        {
            var sample = new FreehandInputSample(pointerId, position.X, position.Y);
            var effectiveTransform = _frozenTransform ?? transform;
            var chartPoint = effectiveTransform.ScreenToChart(chartPosition);

            var result = _controller.Move(sample, chartPoint);
            if (result == DrawingInputResult.Updated)
            {
                PreviewObject?.Points.Add(chartPoint);
                needsInvalidate = true;
                return true;
            }

            if (result == DrawingInputResult.Cancelled)
            {
                CancelActiveTransaction();
                PreviewObject = null;
                _frozenTransform = null;
                needsInvalidate = true;
                return true;
            }

            return false;
        }
        catch
        {
            HandleCancel(DrawingCancelReason.CaptureLost, out needsInvalidate);
            return true;
        }
    }

    /// <summary>
    /// Handles the pointer released event to finalize and commit the freehand stroke.
    /// </summary>
    public bool HandlePointerReleased(
        Point position,
        Point chartPosition,
        long pointerId,
        ChartViewModel? viewModel,
        ICoordinateTransform transform,
        int panelIndex,
        PanelKey panelKey,
        out bool needsInvalidate)
    {
        needsInvalidate = false;

        if (!_controller.IsDrawing)
        {
            return false;
        }

        // Ignore events from non-owner pointers without mutating stroke state
        if (pointerId != _controller.OwnedPointerId)
        {
            return false;
        }

        try
        {
            var effectiveTransform = _frozenTransform ?? transform;
            ChartPoint chartPoint = default;
            bool hasValidCoords = double.IsFinite(position.X) && double.IsFinite(position.Y) &&
                                 double.IsFinite(chartPosition.X) && double.IsFinite(chartPosition.Y);

            if (hasValidCoords)
            {
                chartPoint = effectiveTransform.ScreenToChart(chartPosition);
            }

            var sample = new FreehandInputSample(pointerId, position.X, position.Y);
            var result = hasValidCoords ? _controller.Release(sample, chartPoint) : DrawingInputResult.Cancelled;

            if (result == DrawingInputResult.Committed)
            {
                PreviewObject?.Points.Add(chartPoint);
                CommitStroke(viewModel, panelIndex, panelKey);
                needsInvalidate = true;
                return true;
            }

            // Cancelled (e.g. capacity exceeded or non-finite coords)
            _controller.Cancel(DrawingCancelReason.CapacityExceeded);
            CancelActiveTransaction();
            PreviewObject = null;
            needsInvalidate = true;
            return true;
        }
        catch
        {
            _controller.Cancel(DrawingCancelReason.CaptureLost);
            CancelActiveTransaction();
            PreviewObject = null;
            needsInvalidate = true;
            return true;
        }
        finally
        {
            _frozenTransform = null;
        }
    }

    /// <summary>
    /// Cancels the active freehand stroke and discards preview geometry without adding history.
    /// </summary>
    public bool HandleCancel(DrawingCancelReason reason, out bool needsInvalidate)
    {
        needsInvalidate = false;

        if (!_controller.IsDrawing)
        {
            return false;
        }

        _controller.Cancel(reason);
        CancelActiveTransaction();
        PreviewObject = null;
        _frozenTransform = null;
        needsInvalidate = true;
        return true;
    }

    /// <summary>
    /// Completes the stroke early using currently accumulated points (e.g. triggered by Ctrl/Escape).
    /// </summary>
    public bool HandleModifierComplete(
        ChartViewModel? viewModel,
        int panelIndex,
        PanelKey panelKey,
        out bool needsInvalidate)
    {
        needsInvalidate = false;

        if (!_controller.IsDrawing)
        {
            return false;
        }

        var result = _controller.CompleteByModifier();
        if (result == DrawingInputResult.Committed && _controller.CurrentPointCount > 0)
        {
            CommitStroke(viewModel, panelIndex, panelKey);
            if (viewModel != null)
            {
                viewModel.CurrentTool = DrawingTool.Pointer;
            }
            needsInvalidate = true;
            return true;
        }

        CancelActiveTransaction();
        PreviewObject = null;
        _frozenTransform = null;
        if (viewModel != null)
        {
            viewModel.CurrentTool = DrawingTool.Pointer;
        }
        needsInvalidate = true;
        return true;
    }

    private void CancelActiveTransaction()
    {
        if (_activeEditToken.HasValue)
        {
            var token = _activeEditToken.Value;
            _activeEditToken = null;
            _activeCoordinator?.Cancel(token);
            _activeCoordinator = null;
        }
    }

    private void CommitStroke(ChartViewModel? viewModel, int panelIndex, PanelKey panelKey)
    {
        if (PreviewObject == null)
        {
            CancelActiveTransaction();
            return;
        }

        // Canonical object ownership transfer: do NOT create duplicate or call PreviewObject.Dispose()!
        var committedObj = PreviewObject;
        PreviewObject = null;
        _frozenTransform = null;

        // Trim excess pre-allocated capacity to minimize persistent memory footprint
        committedObj.Points.TrimExcess();

        if (_activeEditToken.HasValue)
        {
            var token = _activeEditToken.Value;
            var coord = _activeCoordinator ?? viewModel?.DrawingEditCoordinator;

            bool added = false;
            try
            {
                if (viewModel?.ObjectManager != null)
                {
                    viewModel.ObjectManager.AddObject(committedObj);
                    var layerResult = viewModel.LayerService?.AddObjectToLayer(committedObj.Id, panelKey);
                    if (layerResult.HasValue && !layerResult.Value.IsSuccess)
                    {
                        viewModel.ObjectManager.RemoveObject(committedObj.Id);
                        coord?.Cancel(token);
                        return;
                    }
                    viewModel.ObjectManager.SelectObject(committedObj.Id);
                    added = true;
                }

                if (coord != null)
                {
                    var commitResult = coord.Commit(token);
                    if (!commitResult.IsSuccess)
                    {
                        // Atomic rollback on commit failure (coord.Commit already invalidated token and restored session)
                        if (added && viewModel?.ObjectManager != null)
                        {
                            viewModel.ObjectManager.RemoveObject(committedObj.Id);
                            viewModel.LayerService?.RemoveObjectFromLayer(committedObj.Id);
                        }
                    }
                }
            }
            catch
            {
                try
                {
                    coord?.Cancel(token);
                }
                catch
                {
                    // Suppress secondary cleanup exceptions
                }

                if (added && viewModel?.ObjectManager != null)
                {
                    viewModel.ObjectManager.RemoveObject(committedObj.Id);
                    viewModel.LayerService?.RemoveObjectFromLayer(committedObj.Id);
                }
                throw;
            }
            finally
            {
                _activeEditToken = null;
                _activeCoordinator = null;
            }
        }
        else if (viewModel?.ObjectManager != null)
        {
            viewModel.ObjectManager.AddObject(committedObj);
            var layerResult = viewModel.LayerService?.AddObjectToLayer(committedObj.Id, panelKey);
            if (layerResult.HasValue && !layerResult.Value.IsSuccess)
            {
                viewModel.ObjectManager.RemoveObject(committedObj.Id);
                return;
            }
            viewModel.ObjectManager.SelectObject(committedObj.Id);
            viewModel.PersistCurrentDrawings();
        }

        // Freehand tool remains active for continuous drawing (user may explicitly switch tools or press Escape).
    }
}
