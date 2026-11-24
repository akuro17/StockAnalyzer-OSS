using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Immutable per-call render context passed by value (<c>in</c>) to <see cref="ISeriesPrimitiveRenderer.Render"/>.
/// Avoids heap allocations in the 60fps render loop.
/// </summary>
internal readonly record struct SeriesPrimitiveRenderContext(
    SKCanvas Canvas,
    Rect ChartArea,
    ChartDataSnapshot Snapshot,
    IIndicatorResult Result,
    CoreIndicatorSettings Setting,
    SeriesPrimitiveDescriptor Descriptor,
    IChartRenderConfig Config,
    ICoordinateTransform Transform,
    SKColor SeriesColor,
    decimal MinVal,
    decimal MaxVal,
    int VisibleCandles,
    int Offset,
    int StartIndex,
    IReadOnlyList<CoreCandleData> Candles,
    TimeSpan Interval,
    float ColumnWidth,
    float XOffset,
    bool IsSubWindowContext,
    ThemeColors Theme,
    decimal? EffectiveBaselineOverride = null)
{
    /// <summary>
    /// Resolves the dynamic baseline override supplied by <see cref="IndicatorRenderer"/> when present,
    /// falling back to <see cref="SeriesPrimitiveDescriptor.BaselineValue"/> without mutating cached descriptors.
    /// </summary>
    internal decimal EffectiveBaseline => EffectiveBaselineOverride ?? Descriptor.BaselineValue;
}
