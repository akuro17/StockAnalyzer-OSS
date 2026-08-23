using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Styling;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Value converter returning PrimaryButtonStyle when true and SecondaryButtonStyle when false.
/// Directs control styling to the 4 theme resources configured under Settings -&gt; Theme -&gt; Button.
/// </summary>
public class BoolToButtonThemeConverter : IValueConverter
{
    public static readonly BoolToButtonThemeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isTrue = value is true;
        // When active (true), use ActiveModeButtonStyle (Accent background).
        // When inactive (false), use InactiveModeButtonStyle (Transparent background with visible border).
        string themeKey = isTrue ? "ActiveModeButtonStyle" : "InactiveModeButtonStyle";
        if (Application.Current?.TryGetResource(themeKey, null, out var resource) == true && resource is ControlTheme theme)
        {
            return theme;
        }
        if (Application.Current?.TryGetResource("PrimaryButtonStyle", null, out var primaryTheme) == true && primaryTheme is ControlTheme fallbackTheme)
        {
            return fallbackTheme;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
