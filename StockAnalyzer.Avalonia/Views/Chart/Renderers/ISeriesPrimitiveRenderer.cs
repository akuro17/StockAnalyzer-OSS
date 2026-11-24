using System;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Contract for a zero-allocation Skia rendering primitive implementation.
/// Instances are owned by <see cref="SeriesPrimitiveRendererRegistry"/> and disposed deterministically.
/// </summary>
internal interface ISeriesPrimitiveRenderer : IDisposable
{
    void Render(in SeriesPrimitiveRenderContext context);
}
