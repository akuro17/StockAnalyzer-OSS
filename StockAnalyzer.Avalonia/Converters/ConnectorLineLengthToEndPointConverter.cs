using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>Converts a "connector line length in pixels" setting (Settings &gt; Notes, sa_implement
/// Task 5) into the vertical <see cref="Avalonia.Controls.Shapes.Line.EndPoint"/> a Line needs
/// (paired with a fixed StartPoint="0,0") to render at that exact pixel length. Deliberately used
/// instead of a unit-length geometry ("0,1") plus Stretch="Fill": Stretch scales the Line's entire
/// rendered geometry - including its Pen's StrokeDashArray, which is measured in the pre-stretch
/// unit-length coordinate space - by the same factor, so a configured dash length would render
/// wildly too long (sa_minimal_fix, "LineLength/DashLength appear reversed" fix request).</summary>
public class ConnectorLineLengthToEndPointConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double connectorLineLength || connectorLineLength <= 0)
        {
            return null;
        }

        return new global::Avalonia.Point(0, connectorLineLength);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
