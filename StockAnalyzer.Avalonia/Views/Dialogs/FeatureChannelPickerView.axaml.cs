using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs
{
    /// <summary>
    /// Composed-features picker surface for the Training Wizard. Bound to a
    /// <see cref="ViewModels.Dialogs.FeatureChannelPickerViewModel"/> supplied as its DataContext by
    /// the hosting wizard window.
    /// </summary>
    public partial class FeatureChannelPickerView : UserControl
    {
        public FeatureChannelPickerView()
        {
            InitializeComponent();
        }

        /// <summary>Double-clicking an indicator catalog row adds it, mirroring
        /// <c>IndicatorSettingsWindow.OnIndicatorCatalogDoubleTapped</c>. Semantics: this executes
        /// <see cref="FeatureChannelPickerViewModel.AddIndicatorChannelCommand"/> with whatever is
        /// currently bound in <see cref="FeatureChannelPickerViewModel.SelectedIndicatorSettings"/> at
        /// the moment of the second tap - i.e. it is "Add with the current detail-column settings", not
        /// "Add with defaults". In practice this is the registry default unless the user already edited
        /// the same, already-selected row's parameters before double-tapping it (selecting a different
        /// row first resets the detail column to defaults, since <c>OnSelectedCatalogItemChanged</c>
        /// always rebuilds fresh default settings for the newly selected item).</summary>
        private void OnIndicatorCatalogDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is FeatureChannelPickerViewModel vm && vm.SelectedCatalogItem != null)
            {
                vm.AddIndicatorChannelCommand.Execute(null);
            }
        }

        /// <summary>Double-clicking a price catalog row adds it, same convention as the indicator catalog.</summary>
        private void OnPriceCatalogDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is FeatureChannelPickerViewModel vm && vm.SelectedPriceField != null)
            {
                vm.AddPriceChannelCommand.Execute(null);
            }
        }
    }
}
