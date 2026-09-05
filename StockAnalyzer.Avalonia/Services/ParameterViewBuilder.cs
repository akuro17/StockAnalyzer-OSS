using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;


/// <summary>
/// Service to dynamically build UI controls for indicator parameters based on attributes.
/// Replaces hardcoded XAML DataTemplates.
/// </summary>
public class ParameterViewBuilder
{
    public Control Build(object parameterObject) => Build(parameterObject, Array.Empty<string>());

    /// <summary>
    /// Builds the settings panel. <paramref name="hiddenTags"/> is currently always empty at every call
    /// site (see DynamicPeriodDriverHelper.GetHiddenParameterTags) - the Dynamic Period Driver tag-based
    /// row-hiding this parameter originally supported was removed because it hid a value
    /// (Period) that still affects calculation results while the driver's own warm-up window has no
    /// value yet. The parameter is kept as a general extensibility point for a future tag-based use.
    /// </summary>
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

            // Skip non-screening / chart UI display properties (e.g. ShowSubWindowBar or Browsable(false) or Category("Display"))
            if (browsableAttribute != null && !browsableAttribute.Browsable) continue;
            if (categoryAttribute != null && string.Equals(categoryAttribute.Category, "Display", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(prop.Name, "ShowSubWindowBar", StringComparison.OrdinalIgnoreCase)) continue;

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
        else if (prop.PropertyType == typeof(string))
        {
            bool isTickerProp = prop.Name.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || 
                               prop.Name.Contains("Ticker", StringComparison.OrdinalIgnoreCase);

            if (isTickerProp)
            {
                var autoBox = new AutoCompleteBox
                {
                    Width = 160,
                    Height = 30,
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(12, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Watermark = "Price vs Volume",
                    FilterMode = AutoCompleteFilterMode.StartsWith,
                    ItemTemplate = BuildTickerItemTemplate()
                };

                if (_cachedTickers != null && _cachedTickers.Count > 0)
                {
                    autoBox.ItemsSource = _cachedTickers;
                }
                else
                {
                    _ = LoadAvailableTickersAsync().ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result != null && t.Result.Count > 0)
                        {
                            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                autoBox.ItemsSource = t.Result;
                            });
                        }
                    });
                }

                var binding = new Binding(prop.Name)
                {
                    Source = parameterObject,
                    Mode = BindingMode.TwoWay
                };
                autoBox.Bind(AutoCompleteBox.TextProperty, binding);

                autoBox.SelectionChanged += (s, e) =>
                {
                    if (autoBox.SelectedItem is string selected)
                    {
                        prop.SetValue(parameterObject, selected.Trim().ToUpperInvariant());
                    }
                };

                autoBox.KeyDown += (s, e) =>
                {
                    if (e.Key == global::Avalonia.Input.Key.Enter)
                    {
                        prop.SetValue(parameterObject, string.IsNullOrWhiteSpace(autoBox.Text) ? string.Empty : autoBox.Text.Trim().ToUpperInvariant());
                        autoBox.IsDropDownOpen = false;
                    }
                };

                autoBox.LostFocus += (s, e) =>
                {
                    prop.SetValue(parameterObject, string.IsNullOrWhiteSpace(autoBox.Text) ? string.Empty : autoBox.Text.Trim().ToUpperInvariant());
                };

                inputControl = autoBox;

            }
            else
            {
                var textBox = new TextBox
                {
                    Width = 140,
                    Height = 30,
                    Padding = new Thickness(6, 4),
                    Margin = new Thickness(12, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Watermark = descAttribute?.Description
                };

                var binding = new Binding(prop.Name)
                {
                    Source = parameterObject,
                    Mode = BindingMode.TwoWay
                };
                textBox.Bind(TextBox.TextProperty, binding);

                inputControl = textBox;
            }
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

    private static IReadOnlyList<string>? _cachedTickers;

    public static async Task<IReadOnlyList<string>> LoadAvailableTickersAsync()
    {
        if (_cachedTickers != null && _cachedTickers.Count > 0) return _cachedTickers;
        try
        {
            var provider = (App.Current as App)?.Services?.GetService(typeof(IMarketDataProvider)) as IMarketDataProvider;
            if (provider != null)
            {
                var tickers = await provider.GetAvailableTickersAsync().ConfigureAwait(false);
                if (tickers != null && tickers.Count > 0)
                {
                    _cachedTickers = tickers;
                    return _cachedTickers;
                }
            }
        }
        catch { }
        return Array.Empty<string>();
    }

    private static IDataTemplate BuildTickerItemTemplate()
    {
        return new FuncDataTemplate<string>((symbol, _) =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
                Margin = new Thickness(0, 2)
            };

            var symText = new TextBlock
            {
                Text = symbol,
                FontWeight = FontWeight.Bold,
                Foreground = (Application.Current?.FindResource("Brush.Semantic.Success") as IBrush) ?? Brushes.Green,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(symText, 0);
            grid.Children.Add(symText);

            var sepText = new TextBlock
            {
                Text = LocalizationManager.Instance.Get("AddTicker_SymbolSeparator") ?? " : ",
                Foreground = (Application.Current?.FindResource("Brush.Text.Tertiary") as IBrush) ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(sepText, 1);
            grid.Children.Add(sepText);

            var descText = new TextBlock
            {
                Text = LocalizationManager.Instance.Get("AddTicker_SymbolDescription") ?? "Stock Symbol",
                Foreground = (Application.Current?.FindResource("Brush.Text.Secondary") as IBrush) ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };
            Grid.SetColumn(descText, 2);
            grid.Children.Add(descText);

            var marketBorder = new Border
            {
                Background = (Application.Current?.FindResource("Brush.Background.Primary") as IBrush) ?? Brushes.Transparent,
                Padding = new Thickness(4, 1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(6, 0)
            };
            var marketText = new TextBlock
            {
                Text = LocalizationManager.Instance.Get("AddTicker_MarketLabel") ?? "US",
                FontSize = 11,
                Foreground = (Application.Current?.FindResource("Brush.Text.Tertiary") as IBrush) ?? Brushes.Gray,
                FontWeight = FontWeight.Bold
            };
            marketBorder.Child = marketText;
            Grid.SetColumn(marketBorder, 3);
            grid.Children.Add(marketBorder);

            var validPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2
            };
            var path = new global::Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z"),
                Fill = (Application.Current?.FindResource("Brush.Semantic.Success") as IBrush) ?? Brushes.Green,
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };

            var validText = new TextBlock
            {
                Text = LocalizationManager.Instance.Get("AddTicker_ValidLabel") ?? "Valid",
                Foreground = (Application.Current?.FindResource("Brush.Semantic.Success") as IBrush) ?? Brushes.Green,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            validPanel.Children.Add(path);
            validPanel.Children.Add(validText);
            Grid.SetColumn(validPanel, 4);
            grid.Children.Add(validPanel);

            return grid;
        }, true);
    }
}

