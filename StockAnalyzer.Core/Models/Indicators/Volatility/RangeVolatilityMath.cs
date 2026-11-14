using System;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Shared rolling aggregation of the range-based volatility estimators (Parkinson, Rogers-Satchell).
/// A caller supplies the per-bar variance contribution v_k and whether it is valid; this class turns them into the output series.
/// Unit of the output: percentage per bar; with annualisation, annual percentage (multiplied by sqrt(periodsPerYear)).
/// </summary>
public static class RangeVolatilityMath
{
    /// <summary>
    /// Rolling volatility: bar i is null for i &lt; period - 1 and when any bar of the window [i - period + 1, i] is invalid;
    /// otherwise <c>sqrt(max(0, sum(v) / period)) x (annualize ? sqrt(periodsPerYear) : 1) x 100</c>, null when the result is not a finite decimal-range value.
    /// The window sum is rebuilt from its <paramref name="period"/> terms at every bar (no incremental update, so no accumulated rounding drift).
    /// <paramref name="periodsPerYear"/> is used only when <paramref name="annualize"/> is true.
    /// </summary>
    /// <param name="v">Per-bar variance contribution (the value of an invalid bar is ignored).</param>
    /// <param name="valid">True when <paramref name="v"/> of the bar is usable.</param>
    public static List<decimal?> Rolling(double[] v, bool[] valid, int period, bool annualize, int periodsPerYear)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(valid);
        if (valid.Length != v.Length) throw new ArgumentException("v and valid must have the same length.", nameof(valid));
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        if (annualize && periodsPerYear < 1) throw new ArgumentOutOfRangeException(nameof(periodsPerYear));

        int count = v.Length;
        double annualFactor = IndicatorCalculationHelper.AnnualizationFactor(annualize, periodsPerYear);
        var result = new List<decimal?>(count);

        for (int i = 0; i < count; i++)
        {
            if (i < period - 1)
            {
                result.Add(null);
                continue;
            }

            double sum = 0.0;
            bool windowValid = true;
            for (int k = i - period + 1; k <= i; k++)
            {
                if (!valid[k])
                {
                    windowValid = false;
                    break;
                }

                sum += v[k];
            }

            if (!windowValid)
            {
                result.Add(null);
                continue;
            }

            double sigma = Math.Sqrt(Math.Max(0.0, sum / period));
            result.Add(IndicatorCalculationHelper.ToDecimalOrNull(sigma * annualFactor * IndicatorDefaultConstants.PercentScale));
        }

        return result;
    }
}
