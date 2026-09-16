using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Bi-directional resolver between transient display panel indices (e.g. -1 for Main, 0..K-1 for SubWindows)
/// and persistent, stable <see cref="PanelKey"/> identifiers.
/// Delegates to <see cref="PanelLayoutEnumerator"/> as the Single Source of Truth (SSoT).
/// </summary>
public static class DrawingPanelResolver
{
    private sealed class ResolverState
    {
        public int TargetIndex;
        public PanelKey? TargetKey;
        public PanelKey? ResolvedKey;
        public int? ResolvedIndex;
        public Dictionary<PanelKey, int>? KeyToIndexMap;
        public Dictionary<int, PanelKey>? IndexToKeyMap;
    }

    /// <summary>
    /// Resolves the <see cref="PanelKey"/> for a given screen panel index.
    /// Returns <see cref="PanelKey.Main"/> for index -1.
    /// Returns null if the specified sub-window panel index does not currently correspond to any active indicator.
    /// </summary>
    public static PanelKey? TryResolvePanelKey(
        int panelIndex,
        IReadOnlyList<CoreIndicatorSettings>? indicatorSettings,
        bool isSubWindowVisible)
    {
        if (panelIndex == -1)
        {
            return PanelKey.Main;
        }

        if (panelIndex < 0 || indicatorSettings == null || !isSubWindowVisible)
        {
            return null;
        }

        var seenGroups = new HashSet<string>();
        var groupScratch = new List<CoreIndicatorSettings>();
        var state = new ResolverState { TargetIndex = panelIndex };

        PanelLayoutEnumerator.ForEachPanel(
            indicatorSettings,
            isSubWindowVisible,
            seenGroups,
            groupScratch,
            state,
            static (idx, primary, group, s) =>
            {
                if (idx == s.TargetIndex)
                {
                    s.ResolvedKey = CreateKeyFromSetting(primary);
                }
            });

        return state.ResolvedKey;
    }

    /// <summary>
    /// Resolves the <see cref="PanelKey"/> for a given screen panel index, throwing <see cref="KeyNotFoundException"/>
    /// if the panel index is unallocated.
    /// </summary>
    public static PanelKey ResolvePanelKey(
        int panelIndex,
        IReadOnlyList<CoreIndicatorSettings>? indicatorSettings,
        bool isSubWindowVisible)
    {
        var key = TryResolvePanelKey(panelIndex, indicatorSettings, isSubWindowVisible);
        if (!key.HasValue)
        {
            throw new KeyNotFoundException($"No active chart panel found for display index {panelIndex}.");
        }
        return key.Value;
    }

    /// <summary>
    /// Resolves the current display panel index for a persistent <see cref="PanelKey"/>.
    /// Returns -1 for <see cref="PanelKind.Main"/>.
    /// Returns null if the target indicator panel is currently closed or unallocated.
    /// </summary>
    public static int? TryGetPanelIndex(
        PanelKey key,
        IReadOnlyList<CoreIndicatorSettings>? indicatorSettings,
        bool isSubWindowVisible)
    {
        if (key.Kind == PanelKind.Main)
        {
            return -1;
        }

        if (indicatorSettings == null || !isSubWindowVisible)
        {
            return null;
        }

        var seenGroups = new HashSet<string>();
        var groupScratch = new List<CoreIndicatorSettings>();
        var state = new ResolverState { TargetKey = key };

        PanelLayoutEnumerator.ForEachPanel(
            indicatorSettings,
            isSubWindowVisible,
            seenGroups,
            groupScratch,
            state,
            static (idx, primary, group, s) =>
            {
                if (s.ResolvedIndex.HasValue) return;

                var currentKey = CreateKeyFromSetting(primary);
                if (currentKey.Equals(s.TargetKey))
                {
                    s.ResolvedIndex = idx;
                }
            });

        return state.ResolvedIndex;
    }

    /// <summary>
    /// Populates pre-allocated bi-directional mapping dictionaries for all currently active panels.
    /// Clears the destination dictionaries before populating.
    /// </summary>
    public static void BuildPanelMap(
        IReadOnlyList<CoreIndicatorSettings>? indicatorSettings,
        bool isSubWindowVisible,
        Dictionary<PanelKey, int> keyToIndex,
        Dictionary<int, PanelKey> indexToKey,
        HashSet<string>? seenGroupsScratch = null,
        List<CoreIndicatorSettings>? groupScratch = null)
    {
        if (keyToIndex == null) throw new ArgumentNullException(nameof(keyToIndex));
        if (indexToKey == null) throw new ArgumentNullException(nameof(indexToKey));

        keyToIndex.Clear();
        indexToKey.Clear();

        keyToIndex[PanelKey.Main] = -1;
        indexToKey[-1] = PanelKey.Main;

        if (indicatorSettings == null || !isSubWindowVisible)
        {
            return;
        }

        var seenGroups = seenGroupsScratch ?? new HashSet<string>();
        var group = groupScratch ?? new List<CoreIndicatorSettings>();

        var state = new ResolverState
        {
            KeyToIndexMap = keyToIndex,
            IndexToKeyMap = indexToKey
        };

        PanelLayoutEnumerator.ForEachPanel(
            indicatorSettings,
            isSubWindowVisible,
            seenGroups,
            group,
            state,
            static (idx, primary, grp, s) =>
            {
                var key = CreateKeyFromSetting(primary);
                s.KeyToIndexMap![key] = idx;
                s.IndexToKeyMap![idx] = key;
            });
    }

    /// <summary>
    /// Creates a <see cref="PanelKey"/> from an active primary <see cref="CoreIndicatorSettings"/>.
    /// </summary>
    public static PanelKey CreateKeyFromSetting(CoreIndicatorSettings setting)
    {
        if (setting == null) throw new ArgumentNullException(nameof(setting));

        if (!string.IsNullOrEmpty(setting.OverlayPanelId))
        {
            return PanelKey.OverlayGroup(setting.OverlayPanelId);
        }

        return PanelKey.Indicator(setting.Id);
    }
}
