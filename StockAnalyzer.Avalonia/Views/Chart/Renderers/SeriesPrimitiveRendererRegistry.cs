using System;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Owns and disposes one reusable renderer instance per <see cref="SeriesPrimitiveType"/>.
/// Array-indexed by enum ordinal for O(1) allocation-free dispatch.
/// </summary>
internal sealed class SeriesPrimitiveRendererRegistry : IDisposable
{
    private const int PrimitiveTypeCount = 19;
    private readonly ISeriesPrimitiveRenderer?[] _renderers = new ISeriesPrimitiveRenderer?[PrimitiveTypeCount];
    private bool _disposed;

    internal SeriesPrimitiveRendererRegistry()
    {
        Register(SeriesPrimitiveType.Line, new LinePrimitiveRenderer());
        Register(SeriesPrimitiveType.DashedLine, new LinePrimitiveRenderer());
        Register(SeriesPrimitiveType.Dot, new DotPrimitiveRenderer());
        Register(SeriesPrimitiveType.Histogram, new HistogramPrimitiveRenderer());
        Register(SeriesPrimitiveType.StepLine, new StepLinePrimitiveRenderer());
        Register(SeriesPrimitiveType.HorizontalSegment, new HorizontalSegmentRenderer());
        Register(SeriesPrimitiveType.Spline, new SplinePrimitiveRenderer());
        Register(SeriesPrimitiveType.Area, new AreaPrimitiveRenderer());
        Register(SeriesPrimitiveType.BandCloud, new BandCloudPrimitiveRenderer());
        Register(SeriesPrimitiveType.PositiveNegativeArea, new PositiveNegativeAreaRenderer());
        Register(SeriesPrimitiveType.FloatingBar, new FloatingBarRenderer());
        Register(SeriesPrimitiveType.StackedArea, new StackedAreaRenderer());
        Register(SeriesPrimitiveType.Bubble, new BubbleRenderer());
        Register(SeriesPrimitiveType.MarkerSymbol, new MarkerSymbolRenderer());
        Register(SeriesPrimitiveType.ColorStrip, new ColorStripRenderer());
        Register(SeriesPrimitiveType.DynamicStyling, new DynamicStyleLineRenderer());
        Register(SeriesPrimitiveType.Vector, new VectorRenderer());
        Register(SeriesPrimitiveType.Waterfall, new WaterfallRenderer());
        Register(SeriesPrimitiveType.Lollipop, new LollipopRenderer());
    }

    internal void Register(SeriesPrimitiveType type, ISeriesPrimitiveRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        int idx = (int)type;
        if ((uint)idx >= PrimitiveTypeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        _renderers[idx]?.Dispose();
        _renderers[idx] = renderer;
    }

    internal ISeriesPrimitiveRenderer? Resolve(SeriesPrimitiveType type)
    {
        if (_disposed) return null;
        int idx = (int)type;
        return (uint)idx < PrimitiveTypeCount ? _renderers[idx] : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < _renderers.Length; i++)
        {
            _renderers[i]?.Dispose();
            _renderers[i] = null;
        }
    }
}
