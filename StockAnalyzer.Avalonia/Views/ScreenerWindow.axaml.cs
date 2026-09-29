using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Screener;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Avalonia.Views.Screener;

namespace StockAnalyzer.Avalonia.Views
{
    public partial class ScreenerWindow : Window
    {
        public ScreenerWindow()
        {
            InitializeComponent();

            // Moving a Filters condition by drag and drop (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionDragMove.md
            // Task 5), mirroring BacktestResultsView.axaml.cs: the view only turns pointer input into (leaf, target row,
            // placement); the tree view-model decides. handledEventsToo: the tree view item's own gesture handling may
            // already have marked the press as handled.
            AddHandler(PointerPressedEvent, OnConditionTreePointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(PointerMovedEvent, OnConditionPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, OnConditionPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(PointerCaptureLostEvent, OnConditionPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnConditionDragOver);
            AddHandler(DragDrop.DragLeaveEvent, OnConditionDragLeave);
            AddHandler(DragDrop.DropEvent, OnConditionDrop);
        }

        private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        /// <summary>Mirrors BacktestResultsView's condition-tree tooltip: the popup's ToolTip is unbounded in width so a long
        /// expression stays on one line and never wraps.</summary>
        private void OnHoverTipAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is Visual content && content.FindAncestorOfType<ToolTip>() is { } tip)
            {
                tip.MaxWidth = double.PositiveInfinity;
            }
        }

        // ===== Filters condition tree: drag and drop (mirrors BacktestResultsView.axaml.cs, types substituted) =====

        /// <summary>Data format of a dragged Filters condition; the payload is the leaf view-model (the drag never leaves this process).
        /// A distinct format string from Backtest's own (<c>BacktestResultsView.ConditionLeafFormat</c>) so the two drags can never be
        /// mistaken for each other's payload type.</summary>
        internal const string ConditionLeafFormat = "StockAnalyzer.ScreenerConditionLeaf";

        private ScreenerConditionLeafViewModel? _pressedLeaf;
        private global::Avalonia.Point _pressPoint;
        private Border? _dropRow;
        private string? _dropClass;

        private ScreenerConditionTreeViewModel? ConditionTree => (DataContext as ScreenerViewModel)?.IndicatorRegistrationViewModel?.ConditionTree;

        private static Border? RowOf(TreeViewItem item)
            => item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(ScreenerConditionDragGesture.RowClass));

        private void OnConditionPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_pressedLeaf is not { } leaf || ConditionTree is not { } tree) return;
            PointerPoint point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
            {
                _pressedLeaf = null;
                return;
            }
            if (!ConditionDragGesture.HasMovedFarEnough(_pressPoint, point.Position, tree.DragStartDistance)) return;

            _pressedLeaf = null;
#pragma warning disable CS0618 // DataObject/DoDragDrop: the same drag API the other views of this application use
            var data = new DataObject();
            data.Set(ConditionLeafFormat, leaf);
            _ = RunConditionDragAsync(e, data);
#pragma warning restore CS0618
        }

        /// <summary>Runs the drag loop and clears the drop indicator when it ends, however it ends; a failed drag is observed here so it never surfaces as an unobserved task exception.</summary>
#pragma warning disable CS0618
        private async Task RunConditionDragAsync(PointerEventArgs e, DataObject data)
#pragma warning restore CS0618
        {
            try
            {
#pragma warning disable CS0618
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(this, "The Filters condition drag failed: {Exception}", ex);
            }
            finally
            {
                ClearDropIndicator();
            }
        }

        private void OnConditionPointerReleased(object? sender, PointerReleasedEventArgs e) => _pressedLeaf = null;

        private void OnConditionPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _pressedLeaf = null;

        private (ScreenerConditionNodeViewModel Target, Border Row, ConditionDropPlacement Placement)? TargetOf(DragEventArgs e)
        {
            // The drag events are raised on the element that allows the drop (this view), so the row is found by position, not by the event source.
            if (this.GetVisualAt(e.GetPosition(this)) is not { } source) return null;
            TreeViewItem? item = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            if (item?.DataContext is not ScreenerConditionNodeViewModel target || RowOf(item) is not { } row) return null;

            global::Avalonia.Point inRow = e.GetPosition(row);
            return (target, row, ScreenerConditionDragGesture.PlacementFor(target, inRow.Y, row.Bounds.Height));
        }

        private static ScreenerConditionLeafViewModel? DraggedLeaf(DragEventArgs e)
        {
#pragma warning disable CS0618
            return e.Data.Contains(ConditionLeafFormat) ? e.Data.Get(ConditionLeafFormat) as ScreenerConditionLeafViewModel : null;
#pragma warning restore CS0618
        }

        private void OnConditionDragOver(object? sender, DragEventArgs e)
        {
            ScreenerConditionLeafViewModel? leaf = DraggedLeaf(e);
            if (leaf is null || ConditionTree is not { } tree || TargetOf(e) is not { } hit
                || tree.EvaluateMove(leaf, hit.Target, hit.Placement) == ConditionMoveResult.Rejected)
            {
                e.DragEffects = DragDropEffects.None;
                ClearDropIndicator();
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            ShowDropIndicator(hit.Row, hit.Placement);
            e.Handled = true;
        }

        private void OnConditionDragLeave(object? sender, DragEventArgs e) => ClearDropIndicator();

        private void OnConditionDrop(object? sender, DragEventArgs e)
        {
            ClearDropIndicator();
            if (DraggedLeaf(e) is not { } leaf || ConditionTree is not { } tree || TargetOf(e) is not { } hit) return;

            tree.MoveLeaf(leaf, hit.Target, hit.Placement);
            e.Handled = true;
        }

        private void ShowDropIndicator(Border row, ConditionDropPlacement placement)
        {
            string dropClass = placement switch
            {
                ConditionDropPlacement.Before => ScreenerConditionDragGesture.DropBeforeClass,
                ConditionDropPlacement.After => ScreenerConditionDragGesture.DropAfterClass,
                _ => ScreenerConditionDragGesture.DropInsideClass,
            };
            if (ReferenceEquals(_dropRow, row) && _dropClass == dropClass) return;

            ClearDropIndicator();
            row.Classes.Add(dropClass);
            _dropRow = row;
            _dropClass = dropClass;
        }

        private void ClearDropIndicator()
        {
            if (_dropRow is not null && _dropClass is not null) _dropRow.Classes.Remove(_dropClass);
            _dropRow = null;
            _dropClass = null;
        }

        private void OnConditionTreePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
            if (properties.IsLeftButtonPressed && e.Source is Visual pressed
                && pressed.FindAncestorOfType<TreeViewItem>(includeSelf: true) is { DataContext: ScreenerConditionLeafViewModel pressedLeaf } pressedItem
                && pressedItem.FindAncestorOfType<TreeView>() is { } pressedTree && pressedTree.Classes.Contains("ConditionTree")
                && pressed.FindAncestorOfType<global::Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is null)
            {
                _pressedLeaf = pressedLeaf;
                _pressPoint = e.GetPosition(this);
                return;
            }

            // A right-click selects the row under the pointer first, so the context-menu command target is unambiguous
            // (mirrors BacktestResultsView.axaml.cs's OnConditionTreePointerPressed).
            if (!properties.IsRightButtonPressed) return;
            if (e.Source is not Visual source) return;

            TreeViewItem? item = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            if (item?.FindAncestorOfType<TreeView>() is { } tree && tree.Classes.Contains("ConditionTree"))
            {
                // SetCurrentValue keeps the style's two-way IsSelected binding (a local value would replace it).
                item.SetCurrentValue(TreeViewItem.IsSelectedProperty, true);
            }
        }
    }
}
