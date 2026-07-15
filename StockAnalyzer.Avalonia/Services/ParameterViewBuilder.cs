using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using StockAnalyzer.Core.Models.Parameters;

using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Service to dynamically build UI controls for indicator parameters based on attributes.
/// Replaces hardcoded XAML DataTemplates.
/// </summary>
public class ParameterViewBuilder
{
    public Control Build(object parameterObject)
    {
        if (parameterObject == null) return new TextBlock { Text = "No parameters" };

        var stackPanel = new StackPanel
        {
            Spacing = 10
        };

        var type = parameterObject.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead && p.CanWrite);

        foreach (var prop in properties)
        {
            // 1. Check for DisplayName or Description
            var displayAttribute = prop.GetCustomAttribute<DisplayNameAttribute>();
            var descAttribute = prop.GetCustomAttribute<DescriptionAttribute>();
            var rangeAttribute = prop.GetCustomAttribute<RangeAttribute>();
            var coreRangeAttribute = prop.GetCustomAttribute<CoreParameterRangeAttribute>();

            // Skip if no metadata present (assume internal or not for UI, unless we want to show everything)
            // For now, let's strictly require at least DisplayName or Range to avoid clutter
            if (displayAttribute == null && rangeAttribute == null && coreRangeAttribute == null) continue;

            var fieldPanel = new StackPanel { Spacing = 5 };

            // Label
            var label = new TextBlock
            {
                Text = displayAttribute?.DisplayName ?? prop.Name,
                FontWeight = FontWeight.Bold
            };
            if (descAttribute != null)
            {
                ToolTip.SetTip(label, descAttribute.Description);
            }
            fieldPanel.Children.Add(label);

            // Input Control
            Control? inputControl = null;

            if (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(decimal) || prop.PropertyType == typeof(double))
            {
                var nud = new NumericUpDown
                {
                    FormatString = prop.PropertyType == typeof(int) ? "F0" : "F2",
                    ParsingNumberStyle = System.Globalization.NumberStyles.Any
                };

                // Range
                if (rangeAttribute != null)
                {
                    ToolTip.SetTip(nud, $"Range: {rangeAttribute.Minimum} - {rangeAttribute.Maximum}");
                    if (decimal.TryParse(rangeAttribute.Minimum.ToString(), out var min)) nud.Minimum = min;
                    if (decimal.TryParse(rangeAttribute.Maximum.ToString(), out var max)) nud.Maximum = max;
                }
                else if (coreRangeAttribute != null)
                {
                    ToolTip.SetTip(nud, $"Range: {coreRangeAttribute.Minimum} - {coreRangeAttribute.Maximum}");
                    try
                    {
                        nud.Minimum = Convert.ToDecimal(coreRangeAttribute.Minimum);
                        nud.Maximum = Convert.ToDecimal(coreRangeAttribute.Maximum);
                    }
                    catch { /* Ignore invalid range values */ }
                }

                // Increment
                if (prop.PropertyType == typeof(int))
                    nud.Increment = 1;
                else
                    nud.Increment = 0.1m;

                // Binding
                var binding = new Binding(prop.Name)
                {
                    Source = parameterObject,
                    Mode = BindingMode.TwoWay
                };

                // Add converter for Int properties because NumericUpDown uses Decimal?
                if (prop.PropertyType == typeof(int))
                {
                     // Simple inline converter or anonymous not easily possible here without class. 
                     // Let's use a simple lambda logic if possible? No.
                     // We need a value converter.
                     // Or just rely on Avalonia's built-in coercion. 
                     // If standard coercion fails, we need a converter.
                     // Let's assume standard failed based on user report.
                     // Implement a local converter class at the bottom of file and use it.
                     binding.Converter = new DecimalToIntConverter();
                }

                nud.Bind(NumericUpDown.ValueProperty, binding);

                inputControl = nud;
            }
            else if (prop.PropertyType == typeof(bool))
            {
                var checkBox = new CheckBox
                {
                    Content = displayAttribute?.DisplayName ?? prop.Name,
                    Margin = new Thickness(0, 5)
                };
                
                var binding = new Binding(prop.Name)
                {
                    Source = parameterObject,
                    Mode = BindingMode.TwoWay
                };
                checkBox.Bind(CheckBox.IsCheckedProperty, binding);
                
                inputControl = checkBox;
                
                // For CheckBox, we usually don't need the left label because the CheckBox itself displays the text.
                fieldPanel.Children.Clear();
            }
            // Add other types (enum) here as needed.
            // For now, indicators mostly use numerics.
            else if (prop.PropertyType.IsEnum)
            {
                var comboBox = new ComboBox
                {
                    ItemsSource = Enum.GetValues(prop.PropertyType),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                
                var binding = new Binding(prop.Name)
                {
                    Source = parameterObject,
                    Mode = BindingMode.TwoWay
                };
                comboBox.Bind(ComboBox.SelectedItemProperty, binding);
                
                inputControl = comboBox;
            }

            if (inputControl != null)
            {
                fieldPanel.Children.Add(inputControl);
                stackPanel.Children.Add(fieldPanel);
            }
        }

        return stackPanel;
    }
}

public class DecimalToIntConverter : global::Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        // Model (int) -> UI (decimal?)
        if (value is int i) return (decimal)i;
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        // UI (decimal?) -> Model (int)
        if (value is decimal d) return (int)d;
        if (value is double db) return (int)db;
        return value;
    }
}
