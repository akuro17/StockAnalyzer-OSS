using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class ThemeSettingsView : UserControl
{
    public ThemeSettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
