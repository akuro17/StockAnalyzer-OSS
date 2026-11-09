using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Indicators;
using System.ComponentModel;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Views
{
    public partial class IndicatorSettingsWindow : Window
    {
        private const string IndicatorDataFormat = "StockAnalyzer.CoreIndicatorSettings";
        private CoreIndicatorSettings? _pressedIndicator;
        private global::Avalonia.Point _pressPoint;
        private Border? _dropRow;
        private string? _dropClass;

        public IndicatorSettingsWindow()
        {
            InitializeComponent();
        }

        private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is IndicatorSettingsDialogViewModel vm)
            {
                vm.RequestClose = (result) =>
                {
                    if (result.HasValue)
                        Close(result.Value);
                    else
                        Close();
                };
            }
        }

        private void OnIndicatorCatalogDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is IndicatorSettingsDialogViewModel vm && vm.SelectedCatalogItem != null)
            {
                vm.AddSelectedLibraryIndicatorCommand.Execute(null);
            }
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Deprecated in favor of RequestClose
        }

        // ===== Indicator List Drag & Drop Reordering =====

        private void OnIndicatorRowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border row) return;
            var properties = e.GetCurrentPoint(this).Properties;
            if (!properties.IsLeftButtonPressed) return;

            // Check if pointer pressed inside CheckBox or Button
            if (e.Source is Control sourceControl && !IndicatorListDragGesture.ShouldStartOnPress(sourceControl, row))
            {
                return;
            }

            if (row.DataContext is CoreIndicatorSettings indicator)
            {
                _pressedIndicator = indicator;
                _pressPoint = e.GetPosition(this);
            }
        }

        private void OnIndicatorRowPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_pressedIndicator is null) return;
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
            {
                _pressedIndicator = null;
                return;
            }

            const double DragThreshold = 4.0;
            if (!ConditionDragGesture.HasMovedFarEnough(_pressPoint, point.Position, DragThreshold)) return;

            var indicatorToDrag = _pressedIndicator;
            _pressedIndicator = null;

#pragma warning disable CS0618
            var data = new DataObject();
            data.Set(IndicatorDataFormat, indicatorToDrag);
            _ = RunIndicatorDragAsync(e, data);
#pragma warning restore CS0618
        }

#pragma warning disable CS0618
        private async Task RunIndicatorDragAsync(PointerEventArgs e, DataObject data)
        {
            try
            {
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
            catch
            {
                // Ignore interrupted or cancelled drags
            }
            finally
            {
                ClearDropIndicator();
            }
        }
#pragma warning restore CS0618

        private void OnIndicatorRowPointerReleased(object? sender, PointerReleasedEventArgs e) => _pressedIndicator = null;
        private void OnIndicatorRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _pressedIndicator = null;

        private static CoreIndicatorSettings? DraggedIndicator(DragEventArgs e)
        {
#pragma warning disable CS0618
            return e.Data.Contains(IndicatorDataFormat) ? e.Data.Get(IndicatorDataFormat) as CoreIndicatorSettings : null;
#pragma warning restore CS0618
        }

        private (CoreIndicatorSettings Target, Border Row, ConditionDropPlacement Placement)? TargetOf(DragEventArgs e)
        {
            if (this.GetVisualAt(e.GetPosition(this)) is not { } source) return null;
            var row = source.FindAncestorOfType<Border>(includeSelf: true);
            while (row != null && !row.Classes.Contains(IndicatorListDragGesture.RowClass))
            {
                row = row.GetVisualParent()?.FindAncestorOfType<Border>(includeSelf: true);
            }
            if (row?.DataContext is not CoreIndicatorSettings target) return null;

            var inRow = e.GetPosition(row);
            var placement = IndicatorListDragGesture.GetRowPlacement(inRow.Y, row.Bounds.Height);
            return (target, row, placement);
        }

        private void OnIndicatorDragOver(object? sender, DragEventArgs e)
        {
            var dragged = DraggedIndicator(e);
            if (dragged == null || DataContext is not IndicatorSettingsDialogViewModel vm || TargetOf(e) is not { } hit)
            {
                e.DragEffects = DragDropEffects.None;
                ClearDropIndicator();
                return;
            }

            // Cross-partition dragging (e.g. Base to SubWindow) is rejected
            if (dragged.IsOverlay != hit.Target.IsOverlay)
            {
                e.DragEffects = DragDropEffects.None;
                ClearDropIndicator();
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            ShowDropIndicator(hit.Row, hit.Placement);
            e.Handled = true;
        }

        private void OnIndicatorDragLeave(object? sender, DragEventArgs e) => ClearDropIndicator();

        private void OnIndicatorDrop(object? sender, DragEventArgs e)
        {
            ClearDropIndicator();
            var dragged = DraggedIndicator(e);
            if (dragged == null || DataContext is not IndicatorSettingsDialogViewModel vm || TargetOf(e) is not { } hit)
                return;

            vm.MoveIndicator(dragged, hit.Target, hit.Placement);
            e.Handled = true;
        }

        private void ShowDropIndicator(Border row, ConditionDropPlacement placement)
        {
            string dropClass = placement == ConditionDropPlacement.Before
                ? IndicatorListDragGesture.DropBeforeClass
                : IndicatorListDragGesture.DropAfterClass;

            if (ReferenceEquals(_dropRow, row) && _dropClass == dropClass) return;

            ClearDropIndicator();
            row.Classes.Add(dropClass);
            _dropRow = row;
            _dropClass = dropClass;
        }

        private void ClearDropIndicator()
        {
            if (_dropRow is not null && _dropClass is not null)
            {
                _dropRow.Classes.Remove(_dropClass);
            }
            _dropRow = null;
            _dropClass = null;
        }
    }
}
