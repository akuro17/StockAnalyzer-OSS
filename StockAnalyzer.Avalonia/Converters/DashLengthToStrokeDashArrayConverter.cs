using System;
using System.Globalization;
using Avalonia.Collections;
using Avalonia.Data.Converters;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>Converts a "dash length in pixels" setting (Settings &gt; Notes, sa_implement Task 5)
/// into the two-value dash/gap pattern <see cref="Avalonia.Controls.Shapes.Line.StrokeDashArray"/>
/// expects. Avalonia's StrokeDashArray values are multiples of StrokeThickness, not literal pixels;
/// <see cref="LineStrokeThickness"/> must match the StrokeThickness set on the bound Line in XAML
/// (NoteCardView.axaml's CollapsedThreadIndicatorLine) so a configured "4px" dash actually renders
/// as 4px rather than needing the user to account for stroke width themselves.</summary>
public class DashLengthToStrokeDashArrayConverter : IValueConverter
{
    private const double LineStrokeThickness = 2.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double dashLength || dashLength <= 0)
        {
            return null;
        }

        var dashUnits = Math.Max(dashLength / LineStrokeThickness, 0.1);
        return new AvaloniaList<double> { dashUnits, dashUnits };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
