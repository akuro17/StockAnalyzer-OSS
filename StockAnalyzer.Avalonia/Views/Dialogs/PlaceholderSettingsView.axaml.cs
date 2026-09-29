using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class PlaceholderSettingsView : UserControl
{
    public PlaceholderSettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
