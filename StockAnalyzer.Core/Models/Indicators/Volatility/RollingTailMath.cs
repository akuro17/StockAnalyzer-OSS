using System;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    /// <summary>
    /// The rolling-window steps that VaR and CVaR share: the one-bar returns and the ascending-sorted window of returns, loaded into a caller-owned buffer
    /// so that no list or array is allocated per bar. Unit: ratio (a return of 0.01 is 1 %).
    /// </summary>
    internal static class RollingTailMath
    {
        /// <summary>Windows up to this many returns use a stack buffer; longer ones rent an array from the pool (the buffer is 16 bytes per return).</summary>
        internal const int MaxStackWindow = 256;

        /// <summary>
        /// One-bar simple returns (price[i] - price[i-1]) / price[i-1], one entry per bar after the first. An undefined return (a missing price on either bar,
        /// or a previous price of 0 or less) is null, never a 0 % day.
        /// </summary>
        internal static decimal?[] ComputeReturns(System.Collections.Generic.IReadOnlyList<decimal?> priceSeries)
        {
            var returns = new decimal?[Math.Max(0, priceSeries.Count - 1)];
            for (int i = 1; i < priceSeries.Count; i++)
            {
                decimal? prev = priceSeries[i - 1];
                decimal? curr = priceSeries[i];
                returns[i - 1] = prev.HasValue && curr.HasValue && prev.Value > 0m ? (curr.Value - prev.Value) / prev.Value : null;
            }

            return returns;
        }

        /// <summary>
        /// Copies returns[start .. start + length) into buffer[0 .. length) and sorts that part ascending. Returns false (buffer content undefined) when any
        /// return of the range is null; the caller then reports a null value for the bar. <paramref name="buffer"/> must hold at least <paramref name="length"/> items.
        /// </summary>
        internal static bool TryLoadSortedWindow(decimal?[] returns, int start, int length, Span<decimal> buffer)
        {
            for (int k = 0; k < length; k++)
            {
                decimal? value = returns[start + k];
                if (!value.HasValue)
                {
                    return false;
                }

                buffer[k] = value.Value;
            }

            buffer.Slice(0, length).Sort();
            return true;
        }
    }
}
