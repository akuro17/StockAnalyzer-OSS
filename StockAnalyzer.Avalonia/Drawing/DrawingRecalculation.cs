using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Recalculation of derived drawing state after an object has been moved. SSoT shared by the chart drag
/// interaction and by operations that reposition objects without a drag (e.g. link-time anchor point snap).
/// Depends only on data (candles / <see cref="DrawingCalculationContext"/>), never on view models or views.
/// </summary>
public static class DrawingRecalculation
{
    /// <summary>
    /// Per-frame lightweight recalculation after a whole-object move. Heavy recomputation (pattern detection,
    /// histogram rebuilds) is deliberately excluded here and deferred to <see cref="AfterMoveCompleted"/>.
    /// </summary>
    public static void AfterMoveFrame(IChartObject obj, IReadOnlyList<CoreCandleData> candles)
    {
        if (obj is StockAnalyzer.Avalonia.Drawing.Objects.SsaAnomalyHighlightObject ssaAnomaly)
        {
            ssaAnomaly.Recalculate(candles);
        }
        else if (obj is not StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject &&
                 obj is not StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject &&
                 obj is not StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject &&
                 obj is not AnchoredVwapObject &&
                 obj is not FixedRangeVolumeProfileObject &&
                 obj is not TimeAtPriceObject)
        {
            DeferredComputationRecalculator.TryRecalculate(obj, candles);
        }
    }

    /// <summary>
    /// Recalculates patterns/analyses once a move or drag has finished, instead of on every PointerMoved frame
    /// (LINQ + regression/histogram/pattern-detection recompute per frame caused visible drag stutter, and for
    /// Harmonic/Elliott it also made large patterns flicker as candle subsets changed). Does nothing when the
    /// context carries no candles.
    /// </summary>
    public static void AfterMoveCompleted(IChartObject obj, DrawingCalculationContext context)
    {
        if (context.Candles == null) return;

        if (obj is StockAnalyzer.Avalonia.Drawing.Objects.HarmonicPatternObject harmonic)
        {
            harmonic.Recalculate(ToCandleData(context.Candles));
        }
        else if (obj is StockAnalyzer.Avalonia.Drawing.Objects.AutoElliottWaveObject elliott)
        {
            elliott.Recalculate(ToCandleData(context.Candles));
        }
        else if (obj is StockAnalyzer.Avalonia.Drawing.Objects.GeometricPatternObject geometric)
        {
            geometric.Recalculate(ToCandleData(context.Candles));
        }
        else
        {
            DeferredComputationRecalculator.TryRecalculate(obj, context);
        }
    }

    private static IEnumerable<CandleData> ToCandleData(IReadOnlyList<CoreCandleData> candles)
        => candles.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
}
