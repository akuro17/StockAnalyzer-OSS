using Avalonia.Controls;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class IndicatorPropertiesDialog : Window
{
    public IndicatorPropertiesDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is IndicatorPropertiesViewModel vm)
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
}
