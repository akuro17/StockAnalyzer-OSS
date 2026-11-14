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
    /// Like <see cref="TryRecalculate(IChartObject, IReadOnlyList{CoreCandleData})"/>, but the conversion that Harmonic / Elliott /
    /// GeometricPattern objects need (<see cref="CoreCandleData"/> to <see cref="CandleData"/>) is taken from
    /// <paramref name="conversionCache"/> when one is given, so repeated calls on the same candle list convert once. All other
    /// object types behave exactly as in the overload without the cache.
    /// </summary>
    internal static bool TryRecalculate(IChartObject obj, IReadOnlyList<CoreCandleData> candles, CandleDataConversionCache? conversionCache)
    {
        if (conversionCache == null || candles == null)
        {
            return TryRecalculate(obj, candles!);
        }

        switch (obj)
        {
            case HarmonicPatternObject harmonic:
                harmonic.Recalculate(conversionCache.Get(candles));
                return true;
            case AutoElliottWaveObject elliott:
                elliott.Recalculate(conversionCache.Get(candles));
                return true;
            case GeometricPatternObject geometric:
                geometric.Recalculate(conversionCache.Get(candles));
                return true;
            default:
                return TryRecalculate(obj, candles);
        }
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
            case TimeAtPriceObject tap:
                tap.Recalculate(candles);
                return true;
            case AnchoredVwapObject avwap:
                avwap.Recalculate(candles);
                return true;
            case FftSpectrumObject fft:
                fft.Recalculate(candles);
                return true;
            case DatePriceRangeObject dpr:
                dpr.Recalculate(candles);
                return true;
            case Objects.KalmanFilterProjectionObject kalman:
                kalman.Recalculate(candles);
                return true;
            case Objects.ClothoidCurveProjectionObject clothoid:
                clothoid.Recalculate(candles);
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
                harmonic.Recalculate(CandleDataConverter.ToLazy(candles));
                return true;
            case AutoElliottWaveObject elliott:
                elliott.Recalculate(CandleDataConverter.ToLazy(candles));
                return true;
            case GeometricPatternObject geometric:
                geometric.Recalculate(CandleDataConverter.ToLazy(candles));
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
            case ClassicPivotPointsObject cpp:
                cpp.Recalculate(candles);
                return true;
            default:
                return false;
        }
    }
}
