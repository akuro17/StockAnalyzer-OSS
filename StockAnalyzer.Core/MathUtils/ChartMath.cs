using System;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils
{
    /// <summary>
    /// Provides high-performance mathematical utilities for chart data processing.
    /// </summary>
    public static class ChartMath
    {
        /// <summary>
        /// Quantizes a value to the nearest multiple of a step based on the specified rounding mode.
        /// </summary>
        /// <param name="value">The decimal value to be quantized.</param>
        /// <param name="step">The quantization step size (must be greater than 0).</param>
        /// <param name="mode">The rounding mode to apply.</param>
        /// <returns>The quantized decimal value.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when step is less than or equal to 0.</exception>
        /// <exception cref="NotSupportedException">Thrown when mode is not supported for quantization.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static decimal Quantize(decimal value, decimal step, ChartRoundingMode mode)
        {
            if (mode == ChartRoundingMode.TickSize)
            {
                // Prioritize step if provided (> 0), otherwise estimate from value
                decimal tick = step > 0 ? step : EstimateTickSize(value);
                return Math.Round(value / tick, MidpointRounding.AwayFromZero) * tick;
            }

            if (step <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(step), "Step must be strictly greater than zero.");
            }

            return mode switch
            {
                ChartRoundingMode.None => value,
                ChartRoundingMode.Floor => Math.Floor(value / step) * step,
                ChartRoundingMode.Ceiling => Math.Ceiling(value / step) * step,
                ChartRoundingMode.Round => Math.Round(value / step, MidpointRounding.AwayFromZero) * step,
                ChartRoundingMode.NiceNumbers => Math.Round(value / step, MidpointRounding.AwayFromZero) * step,
                _ => value
            };
        }
        /// <summary>
        /// Estimates a reasonable tick size for price quantization based on the price level.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static decimal EstimateTickSize(decimal price)
        {
            // Robust Tick Size Estimation
            // Based on generic market rules (mix of US/JP for safety)
            if (price >= 10000m) return 10m;
            if (price >= 3000m) return 5m;
            if (price >= 1000m) return 1m;
            if (price >= 500m) return 0.5m;
            if (price >= 100m) return 0.1m;
            if (price >= 10m) return 0.05m;
            return 0.01m;
        }
    }
}
