namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Extension methods for ChartLayoutContext to resolve coordinate regions.
/// </summary>
public static class ChartLayoutExtensions
{
    /// <summary>
    /// Constant representing the Main Chart panel index.
    /// </summary>
    public const int MainChartPanelIndex = -1;

    /// <summary>
    /// Constant representing a point outside any interactive chart panel (e.g. In margins or gaps).
    /// </summary>
    public const int OutsidePanelsIndex = -2;

    /// <summary>
    /// Constant representing the reserved-but-empty volume band (<see cref="Renderers.ChartLayoutContext.VolumeArea"/>).
    /// It is a visible chart region but NOT a drawing surface: the volume histogram is drawn as a
    /// normal sub-window indicator (in <see cref="Renderers.ChartLayoutContext.PanelAreas"/>), so this
    /// band carries no drawing objects and pointer interaction there must not be treated as main-chart.
    /// </summary>
    public const int VolumeAreaPanelIndex = -3;

    /// <summary>
    /// Resolves which panel (Main Chart or SubWindow) contains the specified screen point.
    /// Returns -1 for Main Chart, k (0 to PanelAreas.Count - 1) for SubWindows,
    /// -3 for the reserved volume band, or -2 if the point is outside the active chart areas.
    /// </summary>
    public static int ResolvePanelIndex(this Renderers.ChartLayoutContext layout, global::Avalonia.Point screenPoint)
    {
        // Check Main Chart Area
        if (layout.ChartArea.Contains(screenPoint))
        {
            return MainChartPanelIndex;
        }

        // Check Sub-Window Panels
        if (layout.PanelAreas != null)
        {
            for (int i = 0; i < layout.PanelAreas.Count; i++)
            {
                if (layout.PanelAreas[i].Contains(screenPoint))
                {
                    return i;
                }
            }
        }

        // Reserved-but-empty volume band (only present on SupportsVolume chart types).
        if (layout.VolumeArea.Width > 0 && layout.VolumeArea.Height > 0 && layout.VolumeArea.Contains(screenPoint))
        {
            return VolumeAreaPanelIndex;
        }

        return OutsidePanelsIndex;
    }
}
