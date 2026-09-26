using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StockAnalyzer.Avalonia.ViewModels.Backtest;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Maps <see cref="BacktestMetricSemantic"/> (Plus/Minus/Neutral, spec §9.2) to the same
/// Brush.Semantic.Success/Error/Neutral DynamicResources every other semantic-colored value in the
/// app already resolves through (see <see cref="SignalStatusColorConverter"/>'s identical resource
/// lookup) - MetricsTable/TradeListView reuse this instead of a Backtest-specific color definition.
/// </summary>
public sealed class BacktestSemanticToBrushConverter : IValueConverter
{
    public static readonly BacktestSemanticToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string resourceKey = value switch
        {
            BacktestMetricSemantic.Plus => "Brush.Semantic.Success",
            BacktestMetricSemantic.Minus => "Brush.Semantic.Error",
            _ => "Brush.Semantic.Neutral",
        };

        if (global::Avalonia.Application.Current is { } app &&
            app.TryGetResource(resourceKey, null, out var resource) && resource is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
