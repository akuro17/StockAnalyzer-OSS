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
    /// Single-bar Rogers-Satchell variance step: <c>ln(H/O)*ln(H/C) + ln(L/O)*ln(L/C)</c>.
    /// Returns <see cref="double.NaN"/> when any OHLC value is non-positive or non-finite.
    /// </summary>
    public static double ComputeRogersSatchellStep(decimal open, decimal high, decimal low, decimal close)
    {
        double o = (double)open;
        double h = (double)high;
        double l = (double)low;
        double c = (double)close;
        if (!(o > 0.0 && h > 0.0 && l > 0.0 && c > 0.0) ||
            !double.IsFinite(o) ||
            !double.IsFinite(h) ||
            !double.IsFinite(l) ||
            !double.IsFinite(c))
        {
            return double.NaN;
        }

        return Math.Log(h / o) * Math.Log(h / c) + Math.Log(l / o) * Math.Log(l / c);
    }
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

        var result = new List<decimal?>(v.Length);
        Rolling(v, valid, period, annualize, periodsPerYear, result);
        return result;
    }

    /// <summary>
    /// The same series as <see cref="Rolling(double[], bool[], int, bool, int)"/>, written into <paramref name="output"/> (cleared first; exactly
    /// <paramref name="v"/>.Length items are appended), so a caller that owns the list and borrows the two work spans allocates nothing here.
    /// <c>v[k]</c> is read only when <c>valid[k]</c> is true, so a borrowed buffer needs only <paramref name="valid"/> to be initialised.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="valid"/> and <paramref name="v"/> differ in length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> is below 1, or <paramref name="annualize"/> is true and <paramref name="periodsPerYear"/> is below 1.</exception>
    public static void Rolling(ReadOnlySpan<double> v, ReadOnlySpan<bool> valid, int period, bool annualize, int periodsPerYear, List<decimal?> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (valid.Length != v.Length) throw new ArgumentException("v and valid must have the same length.", nameof(valid));
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        if (annualize && periodsPerYear < 1) throw new ArgumentOutOfRangeException(nameof(periodsPerYear));

        int count = v.Length;
        double annualFactor = IndicatorCalculationHelper.AnnualizationFactor(annualize, periodsPerYear);
        output.Clear();
        if (output.Capacity < count) output.Capacity = count;

        for (int i = 0; i < count; i++)
        {
            if (i < period - 1)
            {
                output.Add(null);
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
                output.Add(null);
                continue;
            }

            double sigma = Math.Sqrt(Math.Max(0.0, sum / period));
            output.Add(IndicatorCalculationHelper.ToDecimalOrNull(sigma * annualFactor * IndicatorDefaultConstants.PercentScale));
        }
    }
}
