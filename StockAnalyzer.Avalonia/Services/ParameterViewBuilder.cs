using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
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
    private readonly IUniverseSuggestionProvider? _universeSuggestionProvider;
    private readonly IMessenger? _messenger;

    public ParameterViewBuilder()
    {
    }

    internal ParameterViewBuilder(IUniverseSuggestionProvider? universeSuggestionProvider, IMessenger? messenger)
    {
        _universeSuggestionProvider = universeSuggestionProvider;
        _messenger = messenger;
    }

    public Control Build(object parameterObject) => Build(parameterObject, Array.Empty<string>(), ParameterFormLayout.InlineLabel);

    /// <summary>
    /// Builds the settings panel. <paramref name="hiddenTags"/> is currently always empty at every call
    /// site (see DynamicPeriodDriverHelper.GetHiddenParameterTags) - the Dynamic Period Driver tag-based
    /// row-hiding this parameter originally supported was removed because it hid a value
    /// (Period) that still affects calculation results while the driver's own warm-up window has no
    /// value yet. The parameter is kept as a general extensibility point for a future tag-based use.
    /// </summary>
    public Control Build(object parameterObject, IReadOnlyCollection<string> hiddenTags) =>
        Build(parameterObject, hiddenTags, ParameterFormLayout.InlineLabel);

    /// <summary>
    /// Builds the settings panel with the specified <see cref="ParameterFormLayout"/>.
    /// </summary>
    public Control Build(object parameterObject, IReadOnlyCollection<string> hiddenTags, ParameterFormLayout layout)
    {
        if (parameterObject == null) return new TextBlock { Text = "No parameters" };

        Panel container = layout == ParameterFormLayout.TwoColumnFields
            ? new StockAnalyzer.Avalonia.Views.Controls.ResponsiveFormPanel()
            : new StackPanel { Spacing = 0 };

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
                var row = layout == ParameterFormLayout.TwoColumnFields
                    ? BuildFieldPropertyControl(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange)
                    : BuildPropertyRow(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange);

                if (row != null)
                {
                    container.Children.Add(row);
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
                        BorderThickness = new Thickness(0, container.Children.Count > 0 ? 1 : 0, 0, 1),
                        Padding = new Thickness(12, 6, 12, 6),
                        Margin = container.Children.Count > 0 ? new Thickness(0, 8, 0, 0) : new Thickness(0, 0, 0, 0)
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

                    if (layout == ParameterFormLayout.TwoColumnFields)
                    {
                        StockAnalyzer.Avalonia.Views.Controls.ResponsiveFormPanel.SetIsFullWidth(headerBorder, true);
                    }

                    container.Children.Add(headerBorder);
                }

                foreach (var item in group)
                {
                    var row = layout == ParameterFormLayout.TwoColumnFields
                        ? BuildFieldPropertyControl(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange)
                        : BuildPropertyRow(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange);

                    if (row != null)
                    {
                        container.Children.Add(row);
                    }
                }
            }
        }

        return container;
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
                if (decimal.TryParse(Convert.ToString(rangeAttribute.Minimum, CultureInfo.InvariantCulture), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var min)) nud.Minimum = min;
                if (decimal.TryParse(Convert.ToString(rangeAttribute.Maximum, CultureInfo.InvariantCulture), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var max)) nud.Maximum = max;
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
            inputControl = CreateEnumComboBox(prop, parameterObject, isInlineLayout: true);
        }
        else if (prop.PropertyType == typeof(IndicatorColor) || prop.PropertyType == typeof(IndicatorColor?))
        {
            var colorPicker = new ColorPicker
            {
                Width = 140,
                Height = 30,
                Margin = new Thickness(12, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            var binding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay,
                Converter = new IndicatorColorToAvaloniaColorConverter()
            };
            colorPicker.Bind(ColorPicker.ColorProperty, binding);

            inputControl = colorPicker;
        }
        else if (prop.PropertyType == typeof(string))
        {
            if (IsUniverseGroupIdProperty(prop))
            {
                inputControl = CreateUniverseGroupAutoCompleteBox(prop, parameterObject, isInlineLayout: true);
            }
            else if (IsTickerSymbolProperty(prop))
            {
                var autoBox = new AutoCompleteBox
                {
                    Width = 160,
                    Height = 30,
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(12, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Watermark = ComparisonSymbolWatermark(parameterObject),
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

    private Control? BuildFieldPropertyControl(
        PropertyInfo prop,
        object parameterObject,
        DisplayNameAttribute? displayAttribute,
        DescriptionAttribute? descAttribute,
        RangeAttribute? rangeAttribute,
        CoreParameterRangeAttribute? coreRangeAttribute)
    {
        var rawName = displayAttribute?.DisplayName ?? prop.Name;
        var displayLabel = ResolveDisplayName(prop, rawName);

        if (prop.PropertyType == typeof(bool))
        {
            var checkBox = new CheckBox
            {
                Content = displayLabel,
                MinHeight = LayoutConstants.IndicatorFormControlHeight,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            if (descAttribute != null || rawName != displayLabel)
            {
                ToolTip.SetTip(checkBox, descAttribute?.Description ?? rawName);
            }

            var binding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay
            };
            checkBox.Bind(CheckBox.IsCheckedProperty, binding);

            return checkBox;
        }

        var fieldPanel = new StackPanel
        {
            Spacing = LayoutConstants.IndicatorFormLabelInputSpacing
        };

        var label = new TextBlock
        {
            Text = displayLabel,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (descAttribute != null || rawName != displayLabel)
        {
            ToolTip.SetTip(label, descAttribute?.Description ?? rawName);
        }
        fieldPanel.Children.Add(label);

        Control? inputControl = null;

        if (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(decimal) || prop.PropertyType == typeof(double))
        {
            var nud = new NumericUpDown
            {
                FormatString = prop.PropertyType == typeof(int) ? "F0" : "F2",
                ParsingNumberStyle = System.Globalization.NumberStyles.Any,
                Height = LayoutConstants.IndicatorFormControlHeight,
                Padding = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Range
            if (rangeAttribute != null)
            {
                ToolTip.SetTip(nud, $"Range: {rangeAttribute.Minimum} - {rangeAttribute.Maximum}");
                if (decimal.TryParse(Convert.ToString(rangeAttribute.Minimum, CultureInfo.InvariantCulture), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var min)) nud.Minimum = min;
                if (decimal.TryParse(Convert.ToString(rangeAttribute.Maximum, CultureInfo.InvariantCulture), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var max)) nud.Maximum = max;
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
        else if (prop.PropertyType.IsEnum)
        {
            inputControl = CreateEnumComboBox(prop, parameterObject, isInlineLayout: false);
        }
        else if (prop.PropertyType == typeof(IndicatorColor) || prop.PropertyType == typeof(IndicatorColor?))
        {
            var colorPicker = new ColorPicker
            {
                Height = LayoutConstants.IndicatorFormControlHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0
            };

            var binding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay,
                Converter = new IndicatorColorToAvaloniaColorConverter()
            };
            colorPicker.Bind(ColorPicker.ColorProperty, binding);
            inputControl = colorPicker;
        }
        else if (prop.PropertyType == typeof(string))
        {
            if (IsUniverseGroupIdProperty(prop))
            {
                inputControl = CreateUniverseGroupAutoCompleteBox(prop, parameterObject, isInlineLayout: false);
            }
            else if (IsTickerSymbolProperty(prop))
            {
                var autoBox = new AutoCompleteBox
                {
                    Height = LayoutConstants.IndicatorFormControlHeight,
                    Padding = new Thickness(6, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Watermark = ComparisonSymbolWatermark(parameterObject),
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
                    Height = LayoutConstants.IndicatorFormControlHeight,
                    Padding = new Thickness(6, 4),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
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
            fieldPanel.Children.Add(inputControl);
            return fieldPanel;
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

    /// <summary>
    /// The hint of the comparison-symbol box: "Price vs Volume" when a blank symbol is a valid setting (<see cref="ICrossTickerParameter.ComparisonSymbolOptional"/>: Correlation and
    /// CrossCorrelation) and for every parameter object that is not a two-symbol one; empty when the indicator cannot give a value without a comparison symbol.
    /// The one definition behind both comparison-symbol boxes of this builder (no type is named here).
    /// </summary>
    internal static string ComparisonSymbolWatermark(object parameterObject)
        => parameterObject is StockAnalyzer.Core.Models.Parameters.ICrossTickerParameter { ComparisonSymbolOptional: false } ? string.Empty : "Price vs Volume";

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

    private static bool IsUniverseGroupIdProperty(PropertyInfo prop) =>
        prop.PropertyType == typeof(string) &&
        string.Equals(prop.Name, "UniverseGroupId", StringComparison.Ordinal);

    private static bool IsTickerSymbolProperty(PropertyInfo prop) =>
        prop.Name.Contains("Symbol", StringComparison.OrdinalIgnoreCase) ||
        prop.Name.Contains("Ticker", StringComparison.OrdinalIgnoreCase);

    private ComboBox CreateEnumComboBox(PropertyInfo prop, object parameterObject, bool isInlineLayout)
    {
        // The attribute opts into localized display texts; the items are then sorted by that text unless the attribute says the declaration order is the scale (SortAlphabetically = false).
        var sortAttribute = prop.GetCustomAttribute<EnumDisplaySortAttribute>(inherit: false)
            ?? prop.PropertyType.GetCustomAttribute<EnumDisplaySortAttribute>(inherit: false);
        bool hasSortAttribute = sortAttribute != null;

        Array rawValues = Enum.GetValues(prop.PropertyType);
        System.Collections.IEnumerable itemsSource = rawValues;

        if (sortAttribute is { SortAlphabetically: true })
        {
            itemsSource = rawValues.Cast<object>()
                .OrderBy(v => ResolveEnumDisplayText((Enum)v), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(v => Convert.ToInt32(v, CultureInfo.InvariantCulture))
                .ToArray();
        }

        var comboBox = new ComboBox
        {
            ItemsSource = itemsSource,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (isInlineLayout)
        {
            comboBox.Width = hasSortAttribute ? double.NaN : LayoutConstants.IndicatorFormInlineComboWidth;
            if (hasSortAttribute)
            {
                comboBox.MinWidth = LayoutConstants.IndicatorFormInlineComboMinWidth;
            }
            comboBox.Height = LayoutConstants.IndicatorFormControlHeight;
            comboBox.Margin = new Thickness(12, 0, 0, 0);
            comboBox.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            comboBox.Height = LayoutConstants.IndicatorFormControlHeight;
            comboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        if (hasSortAttribute)
        {
            comboBox.ItemTemplate = new FuncDataTemplate<object>((_, _) =>
            {
                var textBlock = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center
                };
                textBlock.Bind(TextBlock.TextProperty, new Binding(".") { Converter = StockAnalyzer.Avalonia.Converters.EnumToLocalizedDisplayNameConverter.Instance });
                return textBlock;
            }, true);
        }

        var binding = new Binding(prop.Name)
        {
            Source = parameterObject,
            Mode = BindingMode.TwoWay
        };
        comboBox.Bind(ComboBox.SelectedItemProperty, binding);

        return comboBox;
    }

    internal static string ResolveEnumDisplayText(Enum value)
        => StockAnalyzer.Avalonia.Converters.EnumToLocalizedDisplayNameConverter.ResolveDisplayText(value);

    private AutoCompleteBox CreateUniverseGroupAutoCompleteBox(PropertyInfo prop, object parameterObject, bool isInlineLayout)
    {
        var localizedWatermark = LocalizationManager.Instance.Get("Watermark_UniverseGroupId");
        var watermark = IsValidLocalization(localizedWatermark) ? localizedWatermark : "List / Filter";

        var autoBox = new AutoCompleteBox
        {
            Padding = new Thickness(6, 2),
            MinimumPrefixLength = 0,
            MaxDropDownHeight = LayoutConstants.IndicatorFormAutoCompleteMaxDropDownHeight,
            FilterMode = AutoCompleteFilterMode.Custom,
            ClearSelectionOnLostFocus = false,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Watermark = watermark,
            VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = ResolveSuggestionRowTemplate()
        };

        if (TryFindBorderBrush() is { } borderBrush)
        {
            autoBox.BorderBrush = borderBrush;
        }

        if (isInlineLayout)
        {
            autoBox.Width = LayoutConstants.IndicatorFormUniverseSelectorWidth;
            autoBox.Height = LayoutConstants.IndicatorFormControlHeight;
            autoBox.Margin = new Thickness(12, 0, 0, 0);
            autoBox.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            autoBox.Height = LayoutConstants.IndicatorFormControlHeight;
            autoBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        var provider = _universeSuggestionProvider
            ?? ((App.Current as App)?.Services?.GetService(typeof(IUniverseSuggestionProvider)) as IUniverseSuggestionProvider);
        var messenger = _messenger
            ?? ((App.Current as App)?.Services?.GetService(typeof(IMessenger)) as IMessenger)
            ?? WeakReferenceMessenger.Default;

        _ = new UniverseGroupAutoCompleteAdapter(autoBox, prop, parameterObject, provider, messenger);
        return autoBox;
    }

    private static IBrush? TryFindBorderBrush()
    {
        if (Application.Current == null) return null;
        if (Application.Current.TryFindResource("Brush.Border", out var borderRes) && borderRes is IBrush b1)
            return b1;
        if (Application.Current.TryFindResource("Brush.Border.Primary", out var primaryRes) && primaryRes is IBrush b2)
            return b2;
        return null;
    }

    private static IDataTemplate ResolveSuggestionRowTemplate()
    {
        if (Application.Current != null &&
            Application.Current.TryFindResource("SuggestionRowTemplate", out var res) &&
            res is IDataTemplate template)
        {
            return template;
        }

        return new FuncDataTemplate<StockAnalyzer.Avalonia.ViewModels.ISuggestionRow>((row, _) =>
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(4, 2)
            };

            var display = new TextBlock
            {
                Text = row?.DisplayText ?? string.Empty,
                VerticalAlignment = VerticalAlignment.Center
            };
            var kind = new TextBlock
            {
                Text = row?.KindLabel ?? string.Empty,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray
            };

            panel.Children.Add(display);
            panel.Children.Add(kind);
            return panel;
        }, true);
    }

    private sealed class UniverseGroupSelectionCoordinator
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IMessenger, UniverseGroupSelectionCoordinator> Instances = new();

        private readonly List<UniverseGroupAutoCompleteAdapter> _active = new();
        private UniverseGroupAutoCompleteAdapter? _lastFocused;

        public static UniverseGroupSelectionCoordinator ForMessenger(IMessenger messenger) =>
            Instances.GetOrCreateValue(messenger);

        public void Register(UniverseGroupAutoCompleteAdapter adapter)
        {
            if (!_active.Contains(adapter))
            {
                _active.Add(adapter);
            }
        }

        public void Unregister(UniverseGroupAutoCompleteAdapter adapter)
        {
            _active.Remove(adapter);
            if (ReferenceEquals(_lastFocused, adapter))
            {
                _lastFocused = null;
            }
        }

        public void MarkFocused(UniverseGroupAutoCompleteAdapter adapter)
        {
            if (_active.Contains(adapter))
            {
                _lastFocused = adapter;
            }
        }

        public bool ShouldApplyClick(UniverseGroupAutoCompleteAdapter candidate)
        {
            UniverseGroupAutoCompleteAdapter? firstEligible = null;

            foreach (var adapter in _active)
            {
                if (!adapter.IsEligibleForClickBind()) continue;

                if (ReferenceEquals(adapter, _lastFocused))
                {
                    return ReferenceEquals(candidate, adapter);
                }

                firstEligible ??= adapter;
            }

            return ReferenceEquals(candidate, firstEligible);
        }
    }

    private sealed class UniverseGroupAutoCompleteAdapter
    {
        private readonly AutoCompleteBox _autoBox;
        private readonly PropertyInfo _prop;
        private readonly object _parameterObject;
        private readonly IUniverseSuggestionProvider? _provider;
        private readonly IMessenger _messenger;
        private readonly UniverseGroupSelectionCoordinator _coordinator;
        private bool _isAttached;
        private bool _isPropertySubscribed;
        private bool _isUpdatingUi;
        private bool _isWritingModel;
        private string _committedCanonicalId = string.Empty;
        private string _committedDisplayText = string.Empty;
        private IReadOnlyList<UniverseGroupSuggestion> _suggestions = Array.Empty<UniverseGroupSuggestion>();

        public UniverseGroupAutoCompleteAdapter(
            AutoCompleteBox autoBox,
            PropertyInfo prop,
            object parameterObject,
            IUniverseSuggestionProvider? provider,
            IMessenger messenger)
        {
            _autoBox = autoBox;
            _prop = prop;
            _parameterObject = parameterObject;
            _provider = provider;
            _messenger = messenger;
            _coordinator = UniverseGroupSelectionCoordinator.ForMessenger(messenger);

            _autoBox.ItemFilter = FilterSuggestion;
            _autoBox.ItemSelector = SelectSuggestionText;

            _autoBox.AttachedToVisualTree += OnAttachedToVisualTree;
            _autoBox.DetachedFromVisualTree += OnDetachedFromVisualTree;
            _autoBox.SelectionChanged += OnSelectionChanged;
            _autoBox.PropertyChanged += OnAutoBoxPropertyChanged;
            _autoBox.GotFocus += OnGotFocus;
            _autoBox.PointerPressed += OnPointerPressed;
            _autoBox.KeyDown += OnKeyDown;
            _autoBox.LostFocus += OnLostFocus;
            _autoBox.DropDownClosed += OnDropDownClosed;

            RefreshSuggestions();
            SyncFromModel();
        }

        private string CurrentModelGroupId => (_prop.GetValue(_parameterObject) as string)?.Trim() ?? string.Empty;

        public bool IsEligibleForClickBind() =>
            _isAttached && _autoBox.IsEffectivelyVisible && string.IsNullOrWhiteSpace(CurrentModelGroupId);

        private static bool FilterSuggestion(string? search, object? item)
        {
            if (item is not UniverseGroupSuggestion s) return false;
            if (string.IsNullOrWhiteSpace(search)) return true;

            string trimmed = search.Trim();
            return s.DisplayText.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || s.KindLabel.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        }

        private static string SelectSuggestionText(string? search, object? item) =>
            item is UniverseGroupSuggestion s ? s.DisplayText : (search ?? string.Empty);

        private void OnAttachedToVisualTree(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
        {
            _isAttached = true;
            _coordinator.Register(this);

            _messenger.UnregisterAll(this);
            _messenger.Register<UniverseGroupAutoCompleteAdapter, TickerRowClickedMessage>(
                this,
                static (recipient, message) => recipient.OnTickerRowClicked(message));

            if (_parameterObject is INotifyPropertyChanged inpc && !_isPropertySubscribed)
            {
                inpc.PropertyChanged += OnParameterPropertyChanged;
                _isPropertySubscribed = true;
            }

            RefreshSuggestions();
            SyncFromModel();
        }

        private void OnDetachedFromVisualTree(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
        {
            _isAttached = false;
            _messenger.UnregisterAll(this);

            if (_parameterObject is INotifyPropertyChanged inpc && _isPropertySubscribed)
            {
                inpc.PropertyChanged -= OnParameterPropertyChanged;
                _isPropertySubscribed = false;
            }

            _coordinator.Unregister(this);
        }

        private void OnParameterPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isWritingModel) return;
            if (string.IsNullOrEmpty(e.PropertyName) || string.Equals(e.PropertyName, _prop.Name, StringComparison.Ordinal))
            {
                RefreshSuggestions();
                SyncFromModel();
            }
        }

        private void OnGotFocus(object? sender, GotFocusEventArgs e)
        {
            _coordinator.MarkFocused(this);
            // Clicking an item inside PART_Popup bubbles GotFocus from the ListBoxItem back to
            // AutoCompleteBox; replacing ItemsSource while open destroys the popup containers mid-click.
            if (_autoBox.IsDropDownOpen)
            {
                return;
            }

            RefreshSuggestions();
            if (_suggestions.Count > 0 && string.IsNullOrWhiteSpace(_autoBox.Text))
            {
                _autoBox.IsDropDownOpen = true;
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Visual visual && e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
            {
                _coordinator.MarkFocused(this);
                if (!_autoBox.IsDropDownOpen)
                {
                    RefreshSuggestions();
                    if (_suggestions.Count > 0)
                    {
                        _autoBox.IsDropDownOpen = true;
                    }
                }
            }
        }

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;

            var selected = e.AddedItems.OfType<UniverseGroupSuggestion>().FirstOrDefault()
                ?? (_autoBox.SelectedItem as UniverseGroupSuggestion);
            if (selected == null) return;

            CommitSuggestion(selected);
        }

        private void OnAutoBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_isUpdatingUi) return;

            if (e.Property == AutoCompleteBox.TextProperty && string.IsNullOrWhiteSpace(_autoBox.Text))
            {
                ClearCommittedSelection();
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitPendingOrRevert();
                _autoBox.IsDropDownOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _autoBox.IsDropDownOpen = false;
                RestoreCommittedText();
                e.Handled = true;
            }
        }

        private void OnLostFocus(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            // While the suggestion popup is open, clicking a popup item moves focus out of the TextBox
            // before PointerReleased commits SelectionChanged; defer revert until DropDownClosed.
            if (_autoBox.IsDropDownOpen || _autoBox.IsKeyboardFocusWithin)
            {
                return;
            }

            CommitPendingOrRevert();
        }

        private void OnDropDownClosed(object? sender, EventArgs e)
        {
            if (_isUpdatingUi || _autoBox.IsKeyboardFocusWithin)
            {
                return;
            }

            CommitPendingOrRevert();
        }

        private void OnTickerRowClicked(TickerRowClickedMessage message)
        {
            if (_provider == null || !_coordinator.ShouldApplyClick(this)) return;

            Guid? resolvedId = _provider.ResolveTargetGroupId(message);
            if (resolvedId == null) return;

            RefreshSuggestions();
            var match = _suggestions.FirstOrDefault(s => s.GroupId == resolvedId.Value)
                ?? _provider.FindByGroupId(resolvedId.Value);
            if (match == null) return;

            CommitSuggestion(match);
        }

        private void RefreshSuggestions()
        {
            var list = _provider?.GetSuggestions() ?? Array.Empty<UniverseGroupSuggestion>();
            _suggestions = list;
            _autoBox.ItemsSource = list;
        }

        private void SyncFromModel()
        {
            string rawId = CurrentModelGroupId;
            _isUpdatingUi = true;
            try
            {
                if (string.IsNullOrEmpty(rawId))
                {
                    _committedCanonicalId = string.Empty;
                    _committedDisplayText = string.Empty;
                    _autoBox.SelectedItem = null;
                    _autoBox.Text = string.Empty;
                    ToolTip.SetTip(_autoBox, null);
                }
                else if (Guid.TryParse(rawId, out var guid) &&
                         (_suggestions.FirstOrDefault(s => s.GroupId == guid) ?? _provider?.FindByGroupId(guid)) is { } match)
                {
                    _committedCanonicalId = match.CanonicalId;
                    _committedDisplayText = match.DisplayText;
                    _autoBox.SelectedItem = match;
                    _autoBox.Text = match.DisplayText;
                    ToolTip.SetTip(_autoBox, null);
                }
                else
                {
                    _committedCanonicalId = rawId;
                    _committedDisplayText = rawId;
                    _autoBox.SelectedItem = null;
                    _autoBox.Text = rawId;
                    ToolTip.SetTip(_autoBox, rawId);
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void CommitPendingOrRevert()
        {
            string text = _autoBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                ClearCommittedSelection();
                return;
            }

            if (_autoBox.SelectedItem is UniverseGroupSuggestion selected &&
                string.Equals(text, selected.DisplayText, StringComparison.OrdinalIgnoreCase))
            {
                CommitSuggestion(selected);
                return;
            }

            var exactTextMatches = _suggestions
                .Where(s => string.Equals(s.DisplayText, text, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exactTextMatches.Count == 1)
            {
                CommitSuggestion(exactTextMatches[0]);
                return;
            }

            var exactNameMatches = _suggestions
                .Where(s => string.Equals(s.DisplayName, text, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exactNameMatches.Count == 1)
            {
                CommitSuggestion(exactNameMatches[0]);
                return;
            }

            RestoreCommittedText();
        }

        private void CommitSuggestion(UniverseGroupSuggestion suggestion)
        {
            _committedCanonicalId = suggestion.CanonicalId;
            _committedDisplayText = suggestion.DisplayText;

            _isWritingModel = true;
            try
            {
                if (!string.Equals(CurrentModelGroupId, suggestion.CanonicalId, StringComparison.OrdinalIgnoreCase))
                {
                    _prop.SetValue(_parameterObject, suggestion.CanonicalId);
                }
            }
            finally
            {
                _isWritingModel = false;
            }

            _isUpdatingUi = true;
            try
            {
                _autoBox.SelectedItem = suggestion;
                _autoBox.Text = suggestion.DisplayText;
                _autoBox.IsDropDownOpen = false;
                ToolTip.SetTip(_autoBox, null);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void ClearCommittedSelection()
        {
            _committedCanonicalId = string.Empty;
            _committedDisplayText = string.Empty;

            _isWritingModel = true;
            try
            {
                if (!string.IsNullOrEmpty(CurrentModelGroupId))
                {
                    _prop.SetValue(_parameterObject, string.Empty);
                }
            }
            finally
            {
                _isWritingModel = false;
            }

            _isUpdatingUi = true;
            try
            {
                _autoBox.SelectedItem = null;
                if (!string.IsNullOrEmpty(_autoBox.Text))
                {
                    _autoBox.Text = string.Empty;
                }
                ToolTip.SetTip(_autoBox, null);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void RestoreCommittedText()
        {
            _isUpdatingUi = true;
            try
            {
                var committedItem = Guid.TryParse(_committedCanonicalId, out var guid)
                    ? _suggestions.FirstOrDefault(s => s.GroupId == guid)
                    : null;
                _autoBox.SelectedItem = committedItem;
                _autoBox.Text = _committedDisplayText;
                if (committedItem != null || string.IsNullOrEmpty(_committedCanonicalId))
                {
                    ToolTip.SetTip(_autoBox, null);
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
    }
}
