using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Where a single indicator setting should be rendered for a given SubChart visibility state.
/// </summary>
public enum IndicatorPlacement
{
    Skipped,
    MainChartOverlay,
    SubWindowPanel,
    GranvilleMainOnly,
    GranvilleMainAndSubWindow
}

/// <summary>
/// Resolves the render placement decision used by <see cref="ChartRenderPipeline"/>'s indicator loop.
/// Kept separate and dependency-free so the SubChart-visibility branching can be unit tested
/// without constructing a full render pipeline (SKCanvas/IChartRenderConfig).
/// </summary>
public static class IndicatorPlacementResolver
{
    public static IndicatorPlacement Resolve(CoreIndicatorSettings setting, bool isSubWindowVisible)
    {
        if (!setting.IsEnabled) return IndicatorPlacement.Skipped;

        if (setting.TypeEnum == IndicatorType.GranvilleLaw)
        {
            bool showSubWindowBar = setting.ParameterObject is CoreGranvilleLawParameter p && p.ShowSubWindowBar;
            return (showSubWindowBar && isSubWindowVisible)
                ? IndicatorPlacement.GranvilleMainAndSubWindow
                : IndicatorPlacement.GranvilleMainOnly;
        }

        if (!setting.IsOverlay)
        {
            return isSubWindowVisible ? IndicatorPlacement.SubWindowPanel : IndicatorPlacement.Skipped;
        }

        return IndicatorPlacement.MainChartOverlay;
    }
}
