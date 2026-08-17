using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using System.ComponentModel;

namespace StockAnalyzer.Avalonia.Views
{
    public partial class IndicatorSettingsWindow : Window
    {
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

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Deprecated in favor of RequestClose
        }
    }
}
