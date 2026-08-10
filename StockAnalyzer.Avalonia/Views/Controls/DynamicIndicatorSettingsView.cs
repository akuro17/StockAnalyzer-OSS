using Avalonia;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// A control that dynamically generates settings UI for a given ParameterObject.
/// Uses ParameterViewBuilder to create controls based on attributes.
/// </summary>
public class DynamicIndicatorSettingsView : UserControl
{
    public static readonly StyledProperty<object?> ParameterObjectProperty =
        AvaloniaProperty.Register<DynamicIndicatorSettingsView, object?>(nameof(ParameterObject));

    public object? ParameterObject
    {
        get => GetValue(ParameterObjectProperty);
        set => SetValue(ParameterObjectProperty, value);
    }

    private readonly ParameterViewBuilder _builder = new();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ParameterObjectProperty)
        {
            UpdateContent();
        }
    }

    private void UpdateContent()
    {
        if (ParameterObject == null)
        {
            Content = null;
            return;
        }

        Content = _builder.Build(ParameterObject);
    }
}
