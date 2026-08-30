using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Registry for managing the lifecycle and retrieval of Chart Renderers.
/// Replaces the hardcoded switch instantiation in ChartDrawOperation.
/// </summary>
public sealed class ChartRendererRegistry : IDisposable
{
    private readonly Dictionary<ChartType, IChartRenderer> _renderers = new();

    /// <summary>
    /// Gets or creates the renderer for the specified chart type.
    /// </summary>
    public IChartRenderer GetRenderer(ChartType type)
    {
        if (_renderers.TryGetValue(type, out var renderer))
        {
            return renderer;
        }

        renderer = CreateRenderer(type);
        _renderers[type] = renderer;
        return renderer;
    }

    private IChartRenderer CreateRenderer(ChartType type)
    {
        return type switch
        {
            ChartType.Candlestick => new CandleStickRenderer(),
            ChartType.HeikinAshi => new CandleStickRenderer(), // Reuse CandleStick logic for Heikin Ashi
            ChartType.Line => new LineChartRenderer(),
            ChartType.Area => new AreaChartRenderer(),
            ChartType.Renko => new RenkoRenderer(),
            ChartType.PointAndFigure => new PointAndFigureRenderer(),
            ChartType.ThreeLineBreak => new ThreeLineBreakRenderer(),
            ChartType.OHLCBar => new OHLCBarRenderer(),
            ChartType.ReverseWatch => new ReverseWatchRenderer(),
            ChartType.Kagi => new KagiRenderer(), // Fixed: Use actual KagiRenderer instead of NoOpRenderer
            ChartType.RelativePerformance => new ComparisonChartRenderer(),
            ChartType.Invisible => new NoOpRenderer(),
            _ => new CandleStickRenderer() // Default fallback
        };
    }

    public void Dispose()
    {
        // Dispose all created renderers
        foreach (var renderer in _renderers.Values.Distinct())
        {
            if (renderer is IDisposable d)
            {
                d.Dispose();
            }
        }
        _renderers.Clear();
    }

    /// <summary>
    /// A no-operation renderer for chart types that don't render on the main canvas 
    /// (e.g., invisible / price-hidden or overlay-only types).
    /// </summary>
    public sealed class NoOpRenderer : IChartRenderer
    {
        public void Render(SKCanvas canvas, global::Avalonia.Rect chartArea, ChartDataSnapshot snapshot, IChartRenderConfig config)
        {
            // Do nothing
        }
    }
}
