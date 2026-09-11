using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class ColorPickerDialog : Window
{
    private Color _selectedColor;

    public ColorPickerDialog()
    {
        InitializeComponent();
    }

    public ColorPickerDialog(Color initialColor) : this()
    {
        _selectedColor = initialColor;

        var colorView = this.FindControl<ColorView>("MainColorView");
        var preview = this.FindControl<Border>("ColorPreviewBorder");

        if (colorView != null)
        {
            colorView.Color = initialColor;
            colorView.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name == nameof(ColorView.Color))
                {
                    _selectedColor = colorView.Color;
                    UpdatePreview();
                }
            };
        }

        if (preview != null)
        {
            preview.Background = new SolidColorBrush(initialColor);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UpdatePreview()
    {
        var preview = this.FindControl<Border>("ColorPreviewBorder");
        if (preview != null)
        {
            preview.Background = new SolidColorBrush(_selectedColor);
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Close((Color?)_selectedColor);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close((Color?)null);
    }
}
