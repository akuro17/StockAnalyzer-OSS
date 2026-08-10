namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Provides general helper methods for the chart control.
/// Coordinate transformation methods have been migrated to <see cref="Drawing.ICoordinateTransform"/>.
/// </summary>
public static class ChartHelpers
{
    /// <summary>
    /// Determines if the given chart type supports rendering standard indicators.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="Core.Models.ChartTypeExtensions.SupportsIndicators"/>
    /// which is backed by <see cref="Core.Models.ChartTypeCapabilitiesRegistry"/>.
    /// </remarks>
    public static bool SupportsIndicators(StockAnalyzer.Core.Models.ChartType chartType)
    {
        return StockAnalyzer.Core.Models.ChartTypeExtensions.SupportsIndicators(chartType);
    }
}
