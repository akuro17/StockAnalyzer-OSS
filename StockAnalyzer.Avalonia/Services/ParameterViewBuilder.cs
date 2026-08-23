using System;
using System.Collections.Generic;
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
    public Control Build(object parameterObject) => Build(parameterObject, Array.Empty<string>());

    public Control Build(object parameterObject, IReadOnlyCollection<string> hiddenTags)
    {
        if (parameterObject == null) return new TextBlock { Text = "No parameters" };

        var stackPanel = new StackPanel
        {
            Spacing = 0
        };

        var type = parameterObject.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead && p.CanWrite);

        var validProperties = new List<(PropertyInfo Prop, DisplayNameAttribute? Display, DescriptionAttribute? Desc, RangeAttribute? Range, CoreParameterRangeAttribute? CoreRange, string Category)>();

        foreach (var prop in properties)
        {
            var displayAttribute = prop.GetCustomAttribute<DisplayNameAttribute>();
            var descAttribute = prop.GetCustomAttribute<DescriptionAttribute>();
            var rangeAttribute = prop.GetCustomAttribute<RangeAttribute>();
            var coreRangeAttribute = prop.GetCustomAttribute<CoreParameterRangeAttribute>();
            var browsableAttribute = prop.GetCustomAttribute<BrowsableAttribute>();
            var categoryAttribute = prop.GetCustomAttribute<CategoryAttribute>();
            var tagAttributes = prop.GetCustomAttributes<ParameterTagAttribute>();

            // Skip non-screening / chart UI display properties (e.g. ShowSubWindowBar or Browsable(false) or Category("Display"))
            if (browsableAttribute != null && !browsableAttribute.Browsable) continue;
            if (categoryAttribute != null && string.Equals(categoryAttribute.Category, "Display", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(prop.Name, "ShowSubWindowBar", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsDynamicPeriodSensitive(prop, categoryAttribute, tagAttributes, hiddenTags)) continue;

            // Skip if no metadata present
            if (displayAttribute == null && rangeAttribute == null && coreRangeAttribute == null) continue;

            var category = categoryAttribute?.Category?.Trim() ?? string.Empty;
            validProperties.Add((prop, displayAttribute, descAttribute, rangeAttribute, coreRangeAttribute, category));
        }

        bool hasCategory = validProperties.Any(p => !string.IsNullOrEmpty(p.Category));

        if (!hasCategory)
        {
            foreach (var item in validProperties)
            {
                var row = BuildPropertyRow(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange);
                if (row != null)
                {
                    stackPanel.Children.Add(row);
                }
            }
        }
        else
        {
            var categoryGroups = validProperties.GroupBy(p => p.Category).ToList();

            foreach (var group in categoryGroups)
            {
                if (!string.IsNullOrEmpty(group.Key))
                {
                    var categoryName = group.Key;
                    var localizedCategory = ResolveCategoryName(categoryName);

                    var headerBorder = new Border
                    {
                        Background = (Application.Current?.FindResource("Brush.Background.Tertiary") as IBrush) ?? Brushes.Transparent,
                        BorderBrush = (Application.Current?.FindResource("Brush.Border.Primary") as IBrush) ?? Brushes.Gray,
                        BorderThickness = new Thickness(0, stackPanel.Children.Count > 0 ? 1 : 0, 0, 1),
                        Padding = new Thickness(12, 6, 12, 6),
                        Margin = stackPanel.Children.Count > 0 ? new Thickness(0, 8, 0, 0) : new Thickness(0, 0, 0, 0)
                    };

                    var headerText = new TextBlock
                    {
                        Text = localizedCategory,
                        FontWeight = FontWeight.Bold,
                        FontSize = 12,
                        Foreground = (Application.Current?.FindResource("Brush.Text.Secondary") as IBrush) ?? Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    headerBorder.Child = headerText;
                    stackPanel.Children.Add(headerBorder);
                }

                foreach (var item in group)
                {
                    var row = BuildPropertyRow(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange);
                    if (row != null)
                    {
                        stackPanel.Children.Add(row);
                    }
                }
            }
        }

        return stackPanel;
    }

    private Control? BuildPropertyRow(
        PropertyInfo prop,
        object parameterObject,
        DisplayNameAttribute? displayAttribute,
        DescriptionAttribute? descAttribute,
        RangeAttribute? rangeAttribute,
        CoreParameterRangeAttribute? coreRangeAttribute)
    {
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
        var displayLabel = ResolveDisplayName(prop, rawName);

        // Label cell with shaded background and vertical right border line
        var labelCell = new Border
        {
            Background = (Application.Current?.FindResource("Brush.Background.Tertiary") as IBrush) ?? Brushes.Transparent,
            BorderBrush = (Application.Current?.FindResource("Brush.Border.Primary") as IBrush) ?? Brushes.Gray,
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
                Content = displayLabel,
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
            return rowBorder;
        }

        return null;
    }

    private static string ResolveCategoryName(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName)) return categoryName;

        // Try direct key or sanitized key: Category_{CategoryName}, Category_{CategoryNameWithoutSpaces}
        var key1 = $"Category_{categoryName}";
        var key2 = $"Category_{categoryName.Replace(" ", "").Replace("-", "")}";

        var loc = LocalizationManager.Instance.Get(key1);
        if (IsValidLocalization(loc)) return loc;

        loc = LocalizationManager.Instance.Get(key2);
        if (IsValidLocalization(loc)) return loc;

        loc = LocalizationManager.Instance.Get(categoryName);
        if (IsValidLocalization(loc)) return loc;

        return categoryName;
    }

    private static string ResolveDisplayName(PropertyInfo prop, string rawDisplayName)
    {
        // 1. Try Param_{prop.Name}
        var key1 = $"Param_{prop.Name}";
        var loc = LocalizationManager.Instance.Get(key1);
        if (IsValidLocalization(loc)) return loc;

        // 2. Try Param_{rawDisplayNameWithoutSpecialChars}
        var sanitized = rawDisplayName.Replace(" ", "").Replace("-", "").Replace("/", "").TrimEnd(':');
        var key2 = $"Param_{sanitized}";
        loc = LocalizationManager.Instance.Get(key2);
        if (IsValidLocalization(loc)) return loc;

        // 3. Try rawDisplayName as key
        loc = LocalizationManager.Instance.Get(rawDisplayName);
        if (IsValidLocalization(loc)) return loc;

        // 4. Fallback adjustments
        var displayLabel = rawDisplayName;
        if (displayLabel.Equals("Std Dev Multipliers", StringComparison.OrdinalIgnoreCase) ||
            displayLabel.Equals("Std Dev Multiplier", StringComparison.OrdinalIgnoreCase))
        {
            displayLabel = "Std Dev";
        }
        if (displayLabel.EndsWith(":"))
        {
            displayLabel = displayLabel.TrimEnd(':');
        }

        return displayLabel;
    }

    private static bool IsValidLocalization(string value)
    {
        return !string.IsNullOrEmpty(value) && !(value.StartsWith("[") && value.EndsWith("]"));
    }

    private static bool IsDynamicPeriodSensitive(
        PropertyInfo prop,
        CategoryAttribute? categoryAttribute,
        IEnumerable<ParameterTagAttribute> tagAttributes,
        IReadOnlyCollection<string> hiddenTags)
    {
        if (hiddenTags.Count == 0) return false;

        // 1. Explicit tag match
        if (tagAttributes.Any(t => hiddenTags.Contains(t.Tag))) return true;

        // 2. Comprehensive Period sensitivity when DynamicPeriodSensitive tag is hidden
        if (hiddenTags.Contains(ParameterTags.DynamicPeriodSensitive))
        {
            var category = categoryAttribute?.Category?.Trim();
            if (!string.IsNullOrEmpty(category))
            {
                if (string.Equals(category, "Periods", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "ROC Periods", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "SMA Periods", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "Waveform", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "Smoothing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "Moving Average Cross", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "EMA", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "MACD", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var name = prop.Name;
            if (name.EndsWith("Period", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Sample", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Period", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "KPeriod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "DPeriod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Slowing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
