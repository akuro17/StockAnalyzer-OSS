using System;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    /// <summary>
    /// The one definition of the volume-weighted standard deviation bands shared by VWAP and Anchored VWAP (series names and the band computation).
    /// Unit: price-native. Bands = VWAP +/- multiplier * sigma, sigma = sqrt(max(0, sum(P^2*V) / sum(V) - VWAP^2)) over the same bars as the VWAP,
    /// which equals the volume-weighted standard deviation sqrt(sum(V*(P - VWAP)^2) / sum(V)).
    /// </summary>
    internal static class VwapBandMath
    {
        internal const string UpperBandSeriesName = "UpperBand";
        internal const string LowerBandSeriesName = "LowerBand";

        /// <summary>sqrt(variance), or null when the result cannot be represented (never throws). A negative variance (rounding noise) is the caller's job to clamp to 0.</summary>
        internal static decimal? Sqrt(decimal variance) => IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt((double)variance));

        /// <summary>
        /// Volume-weighted standard deviation sigma = sqrt(max(0, sum(P^2*V) / sum(V) - vwap^2)), or null when
        /// <paramref name="sumVolume"/> &lt;= 0, squared sum overflowed, variance subtraction overflows, or sigma is non-finite.
        /// </summary>
        internal static decimal? ComputeSigma(decimal vwap, decimal sumPriceSquaredVolume, decimal sumVolume, bool squaredSumOverflowed)
        {
            if (squaredSumOverflowed || sumVolume <= 0m)
            {
                return null;
            }

            try
            {
                decimal variance = sumPriceSquaredVolume / sumVolume - vwap * vwap;
                if (variance < 0m)
                {
                    variance = 0m;
                }

                return Sqrt(variance);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        /// <summary>
        /// The two bands of one bar from the running sums. Returns false (bands null) when there is no VWAP (<paramref name="sumVolume"/> is 0), when the squared sum
        /// has overflowed, when sigma cannot be represented, or when the arithmetic overflows. A variance below 0 (rounding noise) counts as 0.
        /// </summary>
        internal static bool TryComputeBands(decimal? vwap, decimal sumPriceSquaredVolume, decimal sumVolume, bool squaredSumOverflowed, decimal multiplier, out decimal upper, out decimal lower)
        {
            upper = 0m;
            lower = 0m;
            if (vwap is not { } center || ComputeSigma(center, sumPriceSquaredVolume, sumVolume, squaredSumOverflowed) is not { } sigma)
            {
                return false;
            }

            try
            {
                upper = center + multiplier * sigma;
                lower = center - multiplier * sigma;
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}
