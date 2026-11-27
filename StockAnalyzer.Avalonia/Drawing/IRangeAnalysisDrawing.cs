using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// A two-anchor drawing tool whose result is a heavy calculation over the bars between its anchors (DSL expansion: GarchVolatilityCone and the range regression tools).
/// One marker so the chart interaction code names the family once instead of once per tool: the anchors are the two vertical range edges (selection handles move the time only),
/// and the calculation runs when a placement or a move/drag has finished (<see cref="DeferredComputationRecalculator"/>), never per pointer-move frame.
/// </summary>
public interface IRangeAnalysisDrawing
{
    /// <summary>Recalculates the result from <paramref name="candles"/> (oldest first) for the current anchors.</summary>
    void Recalculate(IReadOnlyList<CoreCandleData> candles);
}
