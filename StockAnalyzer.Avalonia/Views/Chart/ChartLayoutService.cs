using Avalonia;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using System.Collections.Generic;
using System.Linq;
using System;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Service for creating chart layout contexts.
/// Centralizes layout calculation logic.
/// </summary>
public static class ChartLayoutService
{
    /// <summary>
    /// Creates a ChartLayoutContext for the given bounds and chart type.
    /// Uses theme constants for margins.
    /// </summary>
    public static ChartLayoutContext CreateLayout(Rect bounds, ChartType chartType, IEnumerable<CoreIndicatorSettings>? indicators = null,
        bool showIndicators = true,
        bool isMainWindowVisible = true,
        float? customMarginTop = null, float? customMarginBottom = null, float? customMarginRight = null)
    {
        // Phase 4: Layout calculations are now delegated to a provider implementation.
        // In Phase 5, this will be retrieved directly from IChartTypeProfile.
        IChartLayoutProvider provider = chartType.IsCompactType()
            ? new CompactChartLayoutProvider(ChartHelpers.SupportsIndicators(chartType))
            : new StandardChartLayoutProvider(chartType.SupportsVolume(), ChartHelpers.SupportsIndicators(chartType));
            
        return provider.CreateLayout(bounds, indicators, showIndicators, isMainWindowVisible, customMarginTop, customMarginBottom, customMarginRight);
    }
}
