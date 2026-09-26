using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Service to dynamically build UI controls for chart drawing object parameters based on attributes.
/// Completely decoupled from ParameterViewBuilder (which handles indicator parameters).
/// Supports Avalonia Color, Nullable Color, SKColor, HsvData, Numerics, Booleans, Enums, and multiline text.
/// Includes static reflection caching and deterministic category/property ordering.
/// </summary>
public class DrawingParameterViewBuilder
{
    private const int DefaultCategoryRank = 999;
    private static readonly EnumToLocalizedDisplayNameConverter EnumDisplayNameConverter = new();

    public record PropertyMetadata(
        PropertyInfo Prop,
        DisplayNameAttribute? Display,
        DescriptionAttribute? Desc,
        RangeAttribute? Range,
        CoreParameterRangeAttribute? CoreRange,
        DataTypeAttribute? DataType,
        int Order,
        string Category,
        IReadOnlyList<string> Tags);

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyMetadata>> _metadataCache = new();

    private static readonly Dictionary<string, int> CategoryRank = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Style", 0 },
        { "Fill Style", 10 },
        { "FillStyle", 10 },
        { "Typography", 20 },
        { "Coordinates", 30 },
        { "Geometry", 35 },
        { "Projection", 40 },
        { "Risk / Reward", 50 },
        { "RiskReward", 50 },
        { "Analysis", 60 },
        { "Volume Profile", 70 },
        { "VolumeProfile", 70 },
        { "Parameters", 100 },
        { "General", 110 }
    };

    public static IReadOnlyList<PropertyMetadata> GetOrReflectMetadata(Type type)
    {
        return _metadataCache.GetOrAdd(type, t =>
        {
            var properties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Where(p => p.CanRead && p.CanWrite);

            var list = new List<PropertyMetadata>();

            foreach (var prop in properties)
            {
                // Skip internal infrastructure properties using SSoT
                if (DrawingParameterTags.IgnoredPropertyNames.Contains(prop.Name))
                {
                    continue;
                }

                var browsable = prop.GetCustomAttribute<BrowsableAttribute>();
                if (browsable != null && !browsable.Browsable) continue;

                var display = prop.GetCustomAttribute<DisplayNameAttribute>();
                var displayAttr = prop.GetCustomAttribute<DisplayAttribute>();
                var desc = prop.GetCustomAttribute<DescriptionAttribute>();
                var range = prop.GetCustomAttribute<RangeAttribute>();
                var coreRange = prop.GetCustomAttribute<CoreParameterRangeAttribute>();
                var categoryAttr = prop.GetCustomAttribute<CategoryAttribute>();
                var tagAttrs = prop.GetCustomAttributes<ParameterTagAttribute>().Select(a => a.Tag).ToList();
                var dataType = prop.GetCustomAttribute<DataTypeAttribute>();

                // Include if any metadata attribute exists
                if (display == null && displayAttr == null && range == null && coreRange == null && categoryAttr == null && tagAttrs.Count == 0)
                {
                    continue;
                }

                int order = displayAttr?.GetOrder() ?? int.MaxValue;
                var category = categoryAttr?.Category?.Trim() ?? "Parameters";

                list.Add(new PropertyMetadata(prop, display, desc, range, coreRange, dataType, order, category, tagAttrs));
            }

            return list.AsReadOnly();
        });
    }

    public Control Build(object parameterObject) => Build(parameterObject, Array.Empty<string>());

    public Control Build(object parameterObject, IReadOnlyCollection<string> hiddenTags)
    {
        if (parameterObject == null) return new TextBlock { Text = LocalizationManager.Instance.Get("Msg_NoParameters") ?? "No parameters" };

        var stackPanel = new StackPanel
        {
            Spacing = 12
        };

        var type = parameterObject.GetType();
        var allMetadata = GetOrReflectMetadata(type);

        var hiddenSet = hiddenTags != null && hiddenTags.Count > 0 ? new HashSet<string>(hiddenTags, StringComparer.OrdinalIgnoreCase) : null;

        var validProperties = allMetadata.Where(m =>
        {
            if (hiddenSet != null && m.Tags.Any(t => hiddenSet.Contains(t)))
            {
                return false;
            }
            return true;
        }).ToList();

        bool hasCategory = validProperties.Any(p => !string.IsNullOrEmpty(p.Category));

        if (!hasCategory)
        {
            var card = new Border
            {
                Classes = { "SettingsCard" }
            };

            var cardPanel = new StackPanel
            {
                Spacing = 10
            };
            card.Child = cardPanel;

            var sorted = validProperties.OrderBy(p => p.Order).ToList();
            foreach (var item in sorted)
            {
                var row = BuildPropertyRow(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange, item.DataType);
                if (row != null)
                {
                    cardPanel.Children.Add(row);
                }
            }

            if (cardPanel.Children.Count > 0)
            {
                stackPanel.Children.Add(card);
            }
        }
        else
        {
            var categoryGroups = validProperties
                .GroupBy(p => p.Category)
                .OrderBy(g => GetCategoryOrder(g.Key))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in categoryGroups)
            {
                var card = new Border
                {
                    Classes = { "SettingsCard" }
                };

                var cardPanel = new StackPanel
                {
                    Spacing = 10
                };
                card.Child = cardPanel;


                var sortedItems = group.OrderBy(i => i.Order).ToList();

                foreach (var item in sortedItems)
                {
                    var row = BuildPropertyRow(item.Prop, parameterObject, item.Display, item.Desc, item.Range, item.CoreRange, item.DataType);
                    if (row != null)
                    {
                        cardPanel.Children.Add(row);
                    }
                }

                if (cardPanel.Children.Count > 0)
                {
                    stackPanel.Children.Add(card);
                }
            }
        }

        return stackPanel;
    }

    private static int GetCategoryOrder(string category)
    {
        return CategoryRank.TryGetValue(category, out var rank) ? rank : DefaultCategoryRank;
    }


    private Control? BuildPropertyRow(
        PropertyInfo prop,
        object parameterObject,
        DisplayNameAttribute? displayAttribute,
        DescriptionAttribute? descAttribute,
        RangeAttribute? rangeAttribute,
        CoreParameterRangeAttribute? coreRangeAttribute,
        DataTypeAttribute? dataTypeAttribute)
    {
        var propType = prop.PropertyType;
        var rawName = displayAttribute?.DisplayName ?? prop.Name;
        var displayLabel = ResolveDisplayName(prop, rawName);

        // 1. Boolean (Unified single-row CheckBox, matching other drawing tool panels)
        if (propType == typeof(bool))
        {
            var checkBox = new CheckBox
            {
                Content = displayLabel,
                Foreground = (Application.Current?.FindResource("Brush.Text.Primary") as IBrush) ?? Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            if (Application.Current?.FindResource("DetailFontSize") is double detailFontSize)
            {
                checkBox.FontSize = detailFontSize;
            }
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

        // 2. Multiline String (Stacked label and multiline text area)
        bool isMultiline = propType == typeof(string) && (
            (dataTypeAttribute?.DataType == DataType.MultilineText) ||
            prop.Name.Contains("Text", StringComparison.OrdinalIgnoreCase) ||
            prop.Name.Contains("Note", StringComparison.OrdinalIgnoreCase) ||
            prop.Name.Contains("Content", StringComparison.OrdinalIgnoreCase));

        if (propType == typeof(string) && isMultiline)
        {
            var multiPanel = new StackPanel
            {
                Spacing = 4
            };
            var label = new TextBlock
            {
                Text = displayLabel,
                Foreground = (Application.Current?.FindResource("Brush.Dialog.LabelText") as IBrush) ?? (Application.Current?.FindResource("Brush.Text.Primary") as IBrush) ?? Brushes.Gray
            };
            if (descAttribute != null || rawName != displayLabel)
            {
                ToolTip.SetTip(label, descAttribute?.Description ?? rawName);
            }
            multiPanel.Children.Add(label);

            var textBox = new TextBox
            {
                Height = 60,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(6, 4),
                Watermark = descAttribute?.Description
            };
            var binding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay
            };
            textBox.Bind(TextBox.TextProperty, binding);
            multiPanel.Children.Add(textBox);
            return multiPanel;
        }

        // 3. Single-line row with left label and right-aligned editor control (*, Auto)
        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto"),
            MinHeight = 32
        };

        var rowLabel = new TextBlock
        {
            Text = displayLabel,
            Foreground = (Application.Current?.FindResource("Brush.Dialog.LabelText") as IBrush) ?? (Application.Current?.FindResource("Brush.Text.Primary") as IBrush) ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (descAttribute != null || rawName != displayLabel)
        {
            ToolTip.SetTip(rowLabel, descAttribute?.Description ?? rawName);
        }
        Grid.SetColumn(rowLabel, 0);
        rowGrid.Children.Add(rowLabel);

        Control? inputControl = null;

        // 3a. Color types
        if (propType == typeof(Color))
        {
            var picker = new ColorPicker
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 50,
                Height = 28,
                MinWidth = 0
            };
            var binding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay
            };
            picker.Bind(ColorPicker.ColorProperty, binding);
            inputControl = picker;
        }
        else if (propType == typeof(Color?))
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var picker = new ColorPicker
            {
                VerticalAlignment = VerticalAlignment.Center,
                Width = 50,
                Height = 28,
                MinWidth = 0
            };

            var toggle = new CheckBox
            {
                Content = LocalizationManager.Instance.Get("Common_Enable") ?? "Enable",
                VerticalAlignment = VerticalAlignment.Center
            };

            var pickerBinding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay,
                Converter = new NullableColorToColorConverter()
            };
            picker.Bind(ColorPicker.ColorProperty, pickerBinding);

            var toggleBinding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay,
                Converter = new NullableColorToBoolConverter()
            };
            toggle.Bind(CheckBox.IsCheckedProperty, toggleBinding);

            picker.Bind(Control.IsEnabledProperty, new Binding("IsChecked") { Source = toggle });

            panel.Children.Add(toggle);
            panel.Children.Add(picker);
            inputControl = panel;
        }
        else if (propType == typeof(SKColor))
        {
            var picker = new ColorPicker
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 50,
                Height = 28,
                MinWidth = 0
            };
            var binding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay,
                Converter = new SkColorToAvaloniaColorConverter()
            };
            picker.Bind(ColorPicker.ColorProperty, binding);
            inputControl = picker;
        }
        else if (propType == typeof(HsvData))
        {
            var picker = new ColorPicker
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 50,
                Height = 28,
                MinWidth = 0
            };
            var binding = new Binding(prop.Name)
            {
                Source = parameterObject,
                Mode = BindingMode.TwoWay,
                Converter = new HsvDataToAvaloniaColorConverter()
            };
            picker.Bind(ColorPicker.ColorProperty, binding);
            inputControl = picker;
        }
        // 3b. Numeric types (Unified Width=120, Height=30 for 4+ digits)
        else if (propType == typeof(int) || propType == typeof(decimal) || propType == typeof(double) || propType == typeof(float))
        {
            bool isIntegerLike = propType == typeof(int) ||
                                 prop.Name.Contains("FontSize", StringComparison.OrdinalIgnoreCase) ||
                                 prop.Name.Contains("Count", StringComparison.OrdinalIgnoreCase) ||
                                 prop.Name.Contains("Bars", StringComparison.OrdinalIgnoreCase) ||
                                 prop.Name.Contains("Index", StringComparison.OrdinalIgnoreCase);

            var nud = new NumericUpDown
            {
                FormatString = isIntegerLike ? "0" : (propType == typeof(int) ? "F0" : "F2"),
                ParsingNumberStyle = NumberStyles.Any,
                Width = 120,
                Height = 30,
                Padding = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

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
                catch { }
            }

            nud.Increment = isIntegerLike ? 1m : (propType == typeof(int) ? 1m : 0.1m);

            var numericAdapter = new NumericParameterAdapter(
                parameterObject,
                prop,
                CreateRejectedValueMessage(displayLabel));
            var binding = new Binding(nameof(NumericParameterAdapter.Value))
            {
                Source = numericAdapter,
                Mode = BindingMode.TwoWay
            };
            nud.Bind(NumericUpDown.ValueProperty, binding);
            // Avalonia holds an explicit binding Source only weakly, and the adapter is otherwise
            // referenced solely by its own event handler below; without a strong owner a GC would
            // collect it and every later edit of this NumericUpDown would be silently lost. The
            // control owns the adapter through Tag so both share one lifetime.
            nud.Tag = numericAdapter;

            void UpdateValidationFeedback()
            {
                bool hasError = numericAdapter.HasErrors;
                nud.BorderBrush = Application.Current?.FindResource(
                    hasError ? "Brush.Semantic.Error" : "Brush.Border.Primary") as IBrush;
                if (hasError)
                {
                    ToolTip.SetTip(nud, numericAdapter.ErrorMessage);
                }
                else if (rangeAttribute != null)
                {
                    ToolTip.SetTip(nud, $"Range: {rangeAttribute.Minimum} - {rangeAttribute.Maximum}");
                }
                else if (coreRangeAttribute != null)
                {
                    ToolTip.SetTip(nud, $"Range: {coreRangeAttribute.Minimum} - {coreRangeAttribute.Maximum}");
                }
            }

            numericAdapter.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NumericParameterAdapter.ErrorMessage))
                {
                    UpdateValidationFeedback();
                }
            };
            UpdateValidationFeedback();

            inputControl = nud;
        }
        // 3c. Enum
        else if (propType.IsEnum)
        {
            var comboBox = new ComboBox
            {
                ItemsSource = Enum.GetValues(propType),
                ItemTemplate = BuildLocalizedEnumItemTemplate(),
                Width = 140,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
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
        // 3d. Single-line String
        else if (propType == typeof(string))
        {
            var textBox = new TextBox
            {
                Width = 140,
                Height = 30,
                Padding = new Thickness(6, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
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

        if (inputControl != null)
        {
            Grid.SetColumn(inputControl, 1);
            rowGrid.Children.Add(inputControl);
            return rowGrid;
        }

        return null;
    }

    private static IDataTemplate BuildLocalizedEnumItemTemplate()
    {
        return new FuncDataTemplate<object>((value, _) => new TextBlock
        {
            Text = value is Enum enumValue
                ? EnumDisplayNameConverter.Convert(enumValue, typeof(string), null, CultureInfo.CurrentUICulture)?.ToString()
                : value?.ToString()
        });
    }

    private static string ResolveCategoryName(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName)) return categoryName;

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
        var key1 = $"Param_{prop.Name}";
        var loc = LocalizationManager.Instance.Get(key1);
        if (IsValidLocalization(loc)) return loc;

        var sanitized = rawDisplayName.Replace(" ", "").Replace("-", "").Replace("/", "").TrimEnd(':');
        var key2 = $"Param_{sanitized}";
        loc = LocalizationManager.Instance.Get(key2);
        if (IsValidLocalization(loc)) return loc;

        loc = LocalizationManager.Instance.Get(rawDisplayName);
        if (IsValidLocalization(loc)) return loc;

        return rawDisplayName.TrimEnd(':');
    }

    private static bool IsValidLocalization(string? value)
    {
        return !string.IsNullOrEmpty(value) && !(value.StartsWith("[") && value.EndsWith("]"));
    }

    private static string CreateRejectedValueMessage(string displayLabel)
    {
        string format = LocalizationManager.Instance.Get("Msg_DrawingParameter_ValueRejected");
        return string.Format(CultureInfo.CurrentUICulture, format, displayLabel);
    }

    internal sealed class NumericParameterAdapter : INotifyPropertyChanged, INotifyDataErrorInfo
    {
        private readonly object _parameterObject;
        private readonly PropertyInfo _property;
        private readonly Type _propertyType;
        private readonly string _rejectedValueMessage;
        private string? _errorMessage;

        internal NumericParameterAdapter(object parameterObject, PropertyInfo property, string rejectedValueMessage)
        {
            _parameterObject = parameterObject;
            _property = property;
            _propertyType = property.PropertyType;
            _rejectedValueMessage = rejectedValueMessage;
        }

        public decimal? Value
        {
            get => Convert.ToDecimal(_property.GetValue(_parameterObject), CultureInfo.InvariantCulture);
            set
            {
                if (!value.HasValue)
                {
                    return;
                }

                try
                {
                    _property.SetValue(_parameterObject, ConvertToPropertyType(value.Value));
                    ClearError();
                }
                catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
                {
                    SetError(_rejectedValueMessage);
                    OnPropertyChanged(nameof(Value));
                }
            }
        }

        public string? ErrorMessage => _errorMessage;

        public bool HasErrors => _errorMessage is not null;

        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

        public IEnumerable GetErrors(string? propertyName)
        {
            return HasErrors && (string.IsNullOrEmpty(propertyName) || propertyName == nameof(Value))
                ? new[] { _errorMessage! }
                : Array.Empty<string>();
        }

        private object ConvertToPropertyType(decimal value)
        {
            if (_propertyType == typeof(int)) return decimal.ToInt32(value);
            if (_propertyType == typeof(decimal)) return value;
            if (_propertyType == typeof(double)) return (double)value;
            if (_propertyType == typeof(float)) return (float)value;

            throw new InvalidOperationException($"Unsupported numeric property type: {_propertyType}.");
        }

        private void SetError(string errorMessage)
        {
            if (_errorMessage == errorMessage) return;

            _errorMessage = errorMessage;
            OnPropertyChanged(nameof(ErrorMessage));
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Value)));
        }

        private void ClearError()
        {
            if (_errorMessage is null) return;

            _errorMessage = null;
            OnPropertyChanged(nameof(ErrorMessage));
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Value)));
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private class SkColorToAvaloniaColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SKColor sk) return Color.FromArgb(sk.Alpha, sk.Red, sk.Green, sk.Blue);
            return Colors.Black;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Color c) return new SKColor(c.R, c.G, c.B, c.A);
            return SKColors.Black;
        }
    }

    private class HsvDataToAvaloniaColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is HsvData hsv)
            {
                var ic = hsv.ToIndicatorColor();
                return Color.FromArgb(ic.A, ic.R, ic.G, ic.B);
            }
            return Colors.Black;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Color c)
            {
                return HsvData.FromColor(new IndicatorColor(c.A, c.R, c.G, c.B));
            }
            return new HsvData(1.0, 0.0, 0.0, 1.0);
        }
    }

    private class NullableColorToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Color c) return c;
            return Colors.Black;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Color c) return (Color?)c;
            return null;
        }
    }

    private class NullableColorToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value != null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b) return (Color?)Colors.Black;
            return null;
        }
    }

    private class DecimalToFloatConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is float f) return (decimal)f;
            return 0m;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is decimal d) return (float)d;
            return 0f;
        }
    }
}
