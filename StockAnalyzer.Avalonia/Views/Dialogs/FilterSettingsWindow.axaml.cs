using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs
{
    public partial class FilterSettingsWindow : Window
    {
        private FilterSettingsViewModel? _vm;

        public FilterSettingsWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox && listBox.SelectedIndex >= 0 && DataContext is FilterSettingsViewModel vm)
            {
                vm.SelectedCategory = listBox.SelectedIndex switch
                {
                    0 => ColumnCategory.All,
                    1 => ColumnCategory.Basic,
                    2 => ColumnCategory.PriceVolume,
                    3 => ColumnCategory.Valuation,
                    4 => ColumnCategory.Ratio,
                    5 => ColumnCategory.Financial,
                    _ => ColumnCategory.All
                };
            }
        }

        private void OnFieldsSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox && listBox.SelectedItem is FilterFieldItem selectedField)
            {
                if (DataContext is FilterSettingsViewModel vm)
                {
                    var newRule = new FilterRuleViewModel(new StockAnalyzer.Core.Models.Settings.FilterRule { Field = selectedField.PropertyName }, vm.AvailableFields);
                    vm.Rules.Add(newRule);
                }
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => listBox.SelectedIndex = -1);
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            CleanupSubscriptions();

            _vm = DataContext as FilterSettingsViewModel;

            if (_vm != null)
            {
                _vm.RequestClose += OnRequestClose;
                _vm.RequestCancel += OnRequestCancel;
            }
        }

        private void OnRequestClose(object? sender, StockAnalyzer.Core.Models.Settings.FilterSettings result)
        {
            Close(result);
        }

        private void OnRequestCancel(object? sender, EventArgs e)
        {
            Close(null);
        }

        private void CleanupSubscriptions()
        {
            if (_vm != null)
            {
                _vm.RequestClose -= OnRequestClose;
                _vm.RequestCancel -= OnRequestCancel;
                _vm = null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            CleanupSubscriptions();
            base.OnClosed(e);
        }
    }
}
