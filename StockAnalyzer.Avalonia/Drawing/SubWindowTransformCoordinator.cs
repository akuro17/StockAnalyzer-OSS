using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Coordinates and caches SubWindowCoordinateTransform instances for all active indicator sub-windows.
/// Ensures that drawing tools and renderers can access synchronized panel-specific transforms.
/// </summary>
public class SubWindowTransformCoordinator
{
    private readonly Dictionary<int, SubWindowCoordinateTransform> _transforms = new();
    private readonly Dictionary<int, GenericCoordinateTransform> _yTransforms = new();
    private readonly HashSet<string> _seenGroups = new();
    private readonly List<CoreIndicatorSettings> _groupBuffer = new();

    // The render thread rebuilds these maps every frame (UpdateTransforms), while the UI thread
    // reads them on every pointer move (ChartBaseControl.ResolvePointerTarget -> GetTransform).
    // Dictionary<,> is not safe for concurrent read/write, so a mouse move landing mid-rebuild
    // could corrupt the buckets and hand a later lookup a different panel's transform, mapping a
    // sub-panel series with the wrong Y-scale (a stray near-vertical stub in the volume panel).
    // Serialize every public entry point on this gate; the critical sections are short and the
    // lock is uncontended in the common case, so the render hot path pays only a monitor acquire.
    private readonly object _gate = new();

    // Highest panel index that has ever been configured, so stale keys can be pruned
    // over a fixed range without allocating a removal list on the render hot path.
    private int _maxConfiguredPanelIndex = -1;
    private int _panelCountThisPass;

    /// <summary>
    /// Updates and synchronizes all panel transforms based on the current layout and snapshot.
    /// Panel allocation is delegated to <see cref="PanelLayoutEnumerator"/> (the SSoT shared with
    /// <see cref="Views.Chart.Renderers.ChartRenderPipeline"/>) so panel indices match the renderer exactly.
    /// </summary>
    public void UpdateTransforms(
        ChartLayoutContext layout,
        ChartDataSnapshot snapshot,
        ICoordinateTransform? mainTransform,
        bool isSubWindowVisible)
    {
        lock (_gate)
        {
        if (mainTransform == null || layout.PanelAreas == null || layout.PanelAreas.Count == 0
            || snapshot.IndicatorSettings == null || !isSubWindowVisible)
        {
            _transforms.Clear();
            _yTransforms.Clear();
            _maxConfiguredPanelIndex = -1;
            return;
        }

        _panelCountThisPass = 0;

        PanelLayoutEnumerator.ForEachPanel(
            snapshot.IndicatorSettings,
            isSubWindowVisible,
            _seenGroups,
            _groupBuffer,
            (Coord: this, Layout: layout, Snapshot: snapshot, Main: mainTransform),
            static (int panelIndex, CoreIndicatorSettings primary, IReadOnlyList<CoreIndicatorSettings>? group,
                    (SubWindowTransformCoordinator Coord, ChartLayoutContext Layout, ChartDataSnapshot Snapshot, ICoordinateTransform Main) st) =>
            {
                if (panelIndex >= st.Layout.PanelAreas.Count) return;

                var (minVal, maxVal) = group != null
                    ? PanelValueRangeCalculator.CalculateGroup(st.Snapshot, group)
                    : PanelValueRangeCalculator.Calculate(st.Snapshot, primary);

                st.Coord.ConfigurePanelTransform(panelIndex, st.Layout.PanelAreas[panelIndex], minVal, maxVal, st.Main, st.Snapshot);
                st.Coord._panelCountThisPass = panelIndex + 1;
            });

        // Prune transforms for panels that no longer exist. Panel indices are always the
        // contiguous range [0, _panelCountThisPass); anything above, up to the previous high-water
        // mark, is stale. Fixed-range loop => zero allocation.
        for (int k = _panelCountThisPass; k <= _maxConfiguredPanelIndex; k++)
        {
            _transforms.Remove(k);
            _yTransforms.Remove(k);
        }
        _maxConfiguredPanelIndex = _panelCountThisPass - 1;
        }
    }

    private void ConfigurePanelTransform(
        int panelIndex,
        Rect panelArea,
        decimal minVal,
        decimal maxVal,
        ICoordinateTransform mainTransform,
        ChartDataSnapshot snapshot)
    {
        double width = Math.Max(1.0, panelArea.Width);
        double height = Math.Max(1.0, panelArea.Height);

        if (!_yTransforms.TryGetValue(panelIndex, out var yTransform))
        {
            yTransform = new GenericCoordinateTransform(ChartAxisMode.Time, width, height);
            _yTransforms[panelIndex] = yTransform;
        }
        else
        {
            yTransform.UpdateCanvasSize(width, height, 0, 0, width, height);
        }

        yTransform.PriceScale = PriceScaleType.Linear;
        yTransform.SetPriceRange(minVal, maxVal);

        if (snapshot.Candles != null && snapshot.Candles.Count > 0)
        {
            yTransform.SetTimeRange(snapshot.Candles[0].Timestamp, snapshot.Candles[^1].Timestamp);
        }

        if (!_transforms.TryGetValue(panelIndex, out var subTransform))
        {
            subTransform = new SubWindowCoordinateTransform(mainTransform, yTransform);
            _transforms[panelIndex] = subTransform;
        }
        else
        {
            subTransform.UpdateTransforms(mainTransform, yTransform);
        }
    }

    /// <summary>
    /// Gets the SubWindowCoordinateTransform for the given sub-window panel index.
    /// Returns null if panelIndex is not registered.
    /// </summary>
    public SubWindowCoordinateTransform? GetTransform(int panelIndex)
    {
        lock (_gate)
        {
            if (panelIndex >= 0 && _transforms.TryGetValue(panelIndex, out var transform))
            {
                return transform;
            }
            return null;
        }
    }

    /// <summary>
    /// Resolves the appropriate coordinate transform for any panel.
    /// If panelIndex is -1 (Main Chart) or unresolved, returns mainTransform.
    /// </summary>
    public ICoordinateTransform GetTransformForPanel(int panelIndex, ICoordinateTransform mainTransform)
    {
        lock (_gate)
        {
            if (panelIndex >= 0 && _transforms.TryGetValue(panelIndex, out var subTransform))
            {
                return subTransform;
            }
            return mainTransform;
        }
    }
}
