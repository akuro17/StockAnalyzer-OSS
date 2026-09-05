using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// SSoT dispatcher for recalculating drawing objects whose derived state (regression fit,
/// extracted spline points, volume histogram, indicator-linked levels) is deferred out of
/// the constructor into a separate Recalculate() call.
/// </summary>
public static class DeferredComputationRecalculator
{
    /// <summary>
    /// Recalculates <paramref name="obj"/> using the specified candle sequence.
    /// </summary>
    public static bool TryRecalculate(IChartObject obj, IReadOnlyList<CoreCandleData> candles)
    {
        return TryRecalculate(obj, new DrawingCalculationContext(candles));
    }

    /// <summary>
    /// Recalculates <paramref name="obj"/> using the specified unified calculation context.
    /// </summary>
    public static bool TryRecalculate(IChartObject obj, DrawingCalculationContext context)
    {
        if (context.Candles == null) return false;
        var candles = context.Candles;

        switch (obj)
        {
            case LongShortPositionObject longShort:
                longShort.Recalculate(context);
                return true;
            case RegressionTrendObject reg:
                reg.Recalculate(candles);
                return true;
            case RangeSplineObject rangeSpline:
                rangeSpline.Recalculate(candles);
                return true;
            case FixedRangeVolumeProfileObject frvp:
                frvp.Recalculate(candles);
                return true;
            case FftSpectrumObject fft:
                fft.Recalculate(candles);
                return true;
            case Objects.KalmanFilterProjectionObject kalman:
                kalman.Recalculate(candles);
                return true;
            case Objects.ArimaProjectionObject arima:
                arima.Recalculate(candles);
                return true;
            case Objects.FftProjectionObject fftProj:
                fftProj.Recalculate(candles);
                return true;
            case Objects.HmmProjectionObject hmm:
                hmm.Recalculate(candles);
                return true;
            case Objects.PearsonProjectionObject pearson:
                pearson.Recalculate(candles);
                return true;
            case Objects.FrechetProjectionObject frechet:
                frechet.Recalculate(candles);
                return true;
            case Objects.SsaProjectionObject ssa:
                ssa.Recalculate(candles);
                return true;
            case Objects.SsaMultiComponentObject ssaMulti:
                ssaMulti.Recalculate(candles);
                return true;
            case Objects.SsaSupportResistanceObject ssaSnr:
                ssaSnr.Recalculate(candles);
                return true;
            case Objects.SsaAnomalyHighlightObject ssaAnomaly:
                ssaAnomaly.Recalculate(candles);
                return true;
            case HarmonicPatternObject harmonic:
                harmonic.Recalculate(ToCandleData(candles));
                return true;
            case AutoElliottWaveObject elliott:
                elliott.Recalculate(ToCandleData(candles));
                return true;
            case GeometricPatternObject geometric:
                geometric.Recalculate(ToCandleData(candles));
                return true;
            case Objects.HoughAutoLinesObject houghAuto:
                houghAuto.Recalculate(candles);
                return true;
            case Objects.HoughParabolicCurveObject houghParabola:
                houghParabola.Recalculate(candles);
                return true;
            case Objects.HoughKeyLevelsObject houghKey:
                houghKey.Recalculate(candles);
                return true;
            case Objects.HoughResonantFanObject houghFan:
                houghFan.Recalculate(candles);
                return true;
            case Objects.HoughMagneticLineObject houghMag:
                houghMag.Recalculate(candles);
                return true;
            case Objects.AutoTimeCycleObject autoCycle:
                autoCycle.Recalculate(candles);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Converts CoreCandleData to the older CandleData type that Harmonic/AutoElliottWave/
    /// GeometricPattern's Recalculate() still takes -- same field-by-field mapping used by
    /// ChartInteractionController's drag-release path and ChartViewModel's DTW/Harmonic
    /// region-select path for the same three object types.
    /// </summary>
    private static IEnumerable<CandleData> ToCandleData(IReadOnlyList<CoreCandleData> candles)
    {
        return candles.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
    }
}
