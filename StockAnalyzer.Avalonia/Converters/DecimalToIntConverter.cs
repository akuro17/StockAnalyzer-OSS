using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// ValueConverter between integer model values and decimal? Avalonia UI NumericUpDown values.
/// Provides safe fallback bounds for null, non-numeric, or invalid conversion inputs.
/// </summary>
public class DecimalToIntConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Model (int/long/short) -> UI (decimal?)
        if (value is int i) return (decimal)i;
        if (value is long l) return (decimal)l;
        if (value is short s) return (decimal)s;
        if (value is byte b) return (decimal)b;
        if (value is double db)
        {
            if (double.IsNaN(db) || double.IsInfinity(db) || db > (double)decimal.MaxValue || db < (double)decimal.MinValue) return 0m;
            return (decimal)db;
        }
        if (value is float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f) || f > (float)decimal.MaxValue || f < (float)decimal.MinValue) return 0m;
            return (decimal)f;
        }
        if (value is decimal d) return d;

        return 0m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // UI (decimal?) -> Model (int)
        if (value is decimal d) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
        if (value is double db)
        {
            if (double.IsNaN(db) || double.IsInfinity(db) || db > (double)decimal.MaxValue || db < (double)decimal.MinValue) return BindingNotification.UnsetValue;
            return (int)Math.Clamp((decimal)db, int.MinValue, int.MaxValue);
        }
        if (value is int i) return i;

        return BindingNotification.UnsetValue;
    }
}
