using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Converts a FilterTemplate or FilterSettings into a human-readable preview of its rules
/// (and nested sub-filter names, if any), for hover Tooltip/click Flyout display.
/// </summary>
public class FilterTemplatePreviewConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            FilterTemplate template => FilterTemplateFormatting.ToPreviewText(template.RootSettings),
            FilterSettings settings => FilterTemplateFormatting.ToPreviewText(settings),
            _ => string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
