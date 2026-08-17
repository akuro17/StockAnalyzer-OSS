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
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

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
            Spacing = 0
        };

        var type = parameterObject.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead && p.CanWrite);

        foreach (var prop in properties)
        {
            var displayAttribute = prop.GetCustomAttribute<DisplayNameAttribute>();
            var descAttribute = prop.GetCustomAttribute<DescriptionAttribute>();
            var rangeAttribute = prop.GetCustomAttribute<RangeAttribute>();
            var coreRangeAttribute = prop.GetCustomAttribute<CoreParameterRangeAttribute>();
            var browsableAttribute = prop.GetCustomAttribute<BrowsableAttribute>();
            var categoryAttribute = prop.GetCustomAttribute<CategoryAttribute>();

            // Skip non-screening / chart UI display properties (e.g. ShowSubWindowBar or Browsable(false) or Category("Display"))
            if (browsableAttribute != null && !browsableAttribute.Browsable) continue;
            if (categoryAttribute != null && string.Equals(categoryAttribute.Category, "Display", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(prop.Name, "ShowSubWindowBar", StringComparison.OrdinalIgnoreCase)) continue;

            // Skip if no metadata present
            if (displayAttribute == null && rangeAttribute == null && coreRangeAttribute == null) continue;

            var rowBorder = new Border
            {
                Padding = new Thickness(0)
            };

            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{LayoutConstants.ScreenerLabelColumnWidth}, *"),
                MinHeight = 36
            };

            var rawName = displayAttribute?.DisplayName ?? prop.Name;
            var displayLabel = rawName;
            if (displayLabel.Equals("Std Dev Multipliers", StringComparison.OrdinalIgnoreCase) ||
                displayLabel.Equals("Std Dev Multiplier", StringComparison.OrdinalIgnoreCase))
            {
                displayLabel = "Std Dev";
            }
            if (displayLabel.EndsWith(":"))
            {
                displayLabel = displayLabel.TrimEnd(':');
            }

            // Label cell with shaded background and vertical right border line
            var labelCell = new Border
            {
                Background = (IBrush)Application.Current!.FindResource("Brush.Background.Tertiary")!,
                BorderBrush = (IBrush)Application.Current!.FindResource("Brush.Border.Primary")!,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var label = new TextBlock
            {
                Text = displayLabel,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            if (descAttribute != null || rawName != displayLabel)
            {
                ToolTip.SetTip(label, descAttribute?.Description ?? rawName);
            }
            labelCell.Child = label;
            Grid.SetColumn(labelCell, 0);
            rowGrid.Children.Add(labelCell);

            // Input Control
            Control? inputControl = null;

            if (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(decimal) || prop.PropertyType == typeof(double))
            {
                var nud = new NumericUpDown
                {
                    FormatString = prop.PropertyType == typeof(int) ? "F0" : "F2",
                    ParsingNumberStyle = System.Globalization.NumberStyles.Any,
                    Width = 140,
                    Height = 30,
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(12, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
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

                if (prop.PropertyType == typeof(int))
                {
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
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                
                var binding = new Binding(prop.Name)
                {
                    Source = parameterObject,
                    Mode = BindingMode.TwoWay
                };
                checkBox.Bind(CheckBox.IsCheckedProperty, binding);
                
                inputControl = checkBox;
            }
            else if (prop.PropertyType.IsEnum)
            {
                var comboBox = new ComboBox
                {
                    ItemsSource = Enum.GetValues(prop.PropertyType),
                    Width = 140,
                    Height = 30,
                    Margin = new Thickness(12, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
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
                Grid.SetColumn(inputControl, 1);
                rowGrid.Children.Add(inputControl);

                rowBorder.Child = rowGrid;
                stackPanel.Children.Add(rowBorder);
            }
        }

        return stackPanel;
    }
}
