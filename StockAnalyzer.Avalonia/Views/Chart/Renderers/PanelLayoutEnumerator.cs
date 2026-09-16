using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Single source of truth for "which enabled indicator owns which sub-window panel index".
///
/// The panel-allocation walk (order of <see cref="ChartDataSnapshot.IndicatorSettings"/>, one
/// panel per non-overlay indicator, one shared panel per <see cref="CoreIndicatorSettings.OverlayPanelId"/>
/// group, a panel for Granville only when its sub-window bar is active, never a panel for
/// Volume Profile) was previously hand-rolled in four places
/// (<see cref="ChartRenderPipeline"/>'s indicator loop, its axis-label pre-pass, its crosshair
/// loop, and <c>SubWindowTransformCoordinator</c>), which drifted apart. This type centralizes it
/// on top of <see cref="IndicatorPlacementResolver"/> so drawing-tool transforms and the renderer
/// agree on panel indices exactly.
///
/// Zero-allocation: the caller supplies reusable scratch collections and a state value; use a
/// <c>static</c> callback so no closure is allocated on the render hot path.
/// </summary>
public static class PanelLayoutEnumerator
{
    /// <param name="panelIndex">Sub-window panel index, starting at 0 with Volume when enabled; all remaining panels follow <see cref="ChartDataSnapshot.IndicatorSettings"/> order.</param>
    /// <param name="primarySetting">The indicator that caused this panel to be allocated (the first group member, for grouped panels).</param>
    /// <param name="panelGroup">
    /// Non-null when the panel is shared by an <see cref="CoreIndicatorSettings.OverlayPanelId"/> group:
    /// the full member list (in settings order), to feed <see cref="PanelValueRangeCalculator.CalculateGroup"/>.
    /// Null for a single-indicator panel: use <see cref="PanelValueRangeCalculator.Calculate"/> on <paramref name="primarySetting"/>.
    /// The referenced list is the caller's <c>groupScratch</c> and is reused, so consume it before the next callback.
    /// </param>
    public delegate void PanelCallback<in TState>(
        int panelIndex,
        CoreIndicatorSettings primarySetting,
        IReadOnlyList<CoreIndicatorSettings>? panelGroup,
        TState state);

    /// <summary>
    /// Invokes <paramref name="callback"/> once per allocated sub-window panel, in ascending panel-index order.
    /// Mirrors <see cref="ChartRenderPipeline"/>'s indicator loop exactly.
    /// </summary>
    /// <param name="seenGroupsScratch">Reusable set; cleared internally.</param>
    /// <param name="groupScratch">Reusable list; cleared and refilled per grouped panel.</param>
    public static void ForEachPanel<TState>(
        IReadOnlyList<CoreIndicatorSettings> indicatorSettings,
        bool isSubWindowVisible,
        HashSet<string> seenGroupsScratch,
        List<CoreIndicatorSettings> groupScratch,
        TState state,
        PanelCallback<TState> callback)
    {
        if (indicatorSettings == null) return;

        seenGroupsScratch.Clear();
        int panelIndex = 0;

        CoreIndicatorSettings? volume = null;
        for (int i = 0; i < indicatorSettings.Count; i++)
        {
            var setting = indicatorSettings[i];
            if (setting.TypeEnum == IndicatorType.Volume
                && string.IsNullOrEmpty(setting.OverlayPanelId)
                && IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible) == IndicatorPlacement.SubWindowPanel)
            {
                volume = setting;
                callback(panelIndex, volume, null, state);
                panelIndex++;
                break;
            }
        }

        for (int i = 0; i < indicatorSettings.Count; i++)
        {
            var setting = indicatorSettings[i];
            if (ReferenceEquals(setting, volume)) continue;
            var placement = IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible);

            switch (placement)
            {
                case IndicatorPlacement.GranvilleMainAndSubWindow:
                    callback(panelIndex, setting, null, state);
                    panelIndex++;
                    break;

                case IndicatorPlacement.SubWindowPanel:
                    string? groupId = setting.OverlayPanelId;
                    if (!string.IsNullOrEmpty(groupId))
                    {
                        if (seenGroupsScratch.Contains(groupId)) break; // subsequent member: shares the already-allocated panel
                        seenGroupsScratch.Add(groupId);

                        groupScratch.Clear();
                        for (int j = 0; j < indicatorSettings.Count; j++)
                        {
                            var s = indicatorSettings[j];
                            if (s.IsEnabled && !s.IsOverlay
                                && s.TypeEnum != IndicatorType.GranvilleLaw
                                && s.TypeEnum != IndicatorType.VolumeProfile
                                && s.OverlayPanelId == groupId)
                            {
                                groupScratch.Add(s);
                            }
                        }
                        callback(panelIndex, setting, groupScratch, state);
                        panelIndex++;
                    }
                    else
                    {
                        callback(panelIndex, setting, null, state);
                        panelIndex++;
                    }
                    break;

                // Skipped / MainChartOverlay / GranvilleMainOnly: no sub-window panel allocated.
                default:
                    break;
            }
        }
    }
}
