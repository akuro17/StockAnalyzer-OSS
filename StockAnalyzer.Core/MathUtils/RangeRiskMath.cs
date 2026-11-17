using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// The result of <see cref="RangeRiskMath.Compute"/> for a run of bars: the extreme prices and two risk statistics of the closes.
/// A statistic that is not defined for the bars is null (never a placeholder number).
/// </summary>
/// <param name="HighestHigh">Largest <c>High</c> of the bars.</param>
/// <param name="LowestLow">Smallest <c>Low</c> of the bars.</param>
/// <param name="MaxDrawdownPercent">Largest fall of a close from the highest close so far, in percent of that highest close (a positive number, 0 when the closes never fall).</param>
/// <param name="VolatilityPercent">Sample standard deviation of the close-to-close log returns, in percent per bar (not annualised).</param>
public readonly record struct RangeRiskStats(
    decimal HighestHigh,
    decimal LowestLow,
    decimal? MaxDrawdownPercent,
    decimal? VolatilityPercent);

/// <summary>
/// Max drawdown and per-bar volatility of the closes of a run of bars, plus its highest high and lowest low, in one allocation-free pass.
/// The volatility core (sample standard deviation of ln(C_t / C_(t-1))) is the same as the one of <c>CoreHistoricalVolatilityIndicator</c>; the indicator itself is not changed,
/// and this class never annualises (the owner of an annual figure applies its own factor).
/// </summary>
public static class RangeRiskMath
{
    /// <summary>The fewest bars for which the volatility is defined: 3 bars give 2 returns, the least a sample standard deviation (n - 1) needs.</summary>
    public const int MinBarsForVolatility = 3;

    /// <summary>The factor that turns a ratio into a percentage.</summary>
    private const decimal PercentFactor = 100m;

    private const double PercentFactorDouble = 100.0;

    /// <summary>
    /// Statistics of <c>candles[firstIndex..lastIndex]</c> (both inclusive, chronological order). Returns null when the run holds no bar.
    /// <list type="bullet">
    /// <item>MaxDrawdownPercent = max over j of (H_j - C_j) / H_j * 100, with H_j the highest close from the first bar to bar j. Null when some H_j is 0 or less, or when a fall does not fit a decimal (corrupt prices; never an exception).</item>
    /// <item>VolatilityPercent = sample standard deviation (n - 1) of ln(C_t / C_(t-1)) for t from the second bar of the run (the bar before the run is not used) * 100.
    /// Null for fewer than <see cref="MinBarsForVolatility"/> bars, when a close in the run is 0 or less, or when the result is not a finite decimal.</item>
    /// </list>
    /// </summary>
    public static RangeRiskStats? Compute(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex)
    {
        if (firstIndex < 0 || lastIndex >= candles.Count || lastIndex < firstIndex)
        {
            return null;
        }

        decimal highestHigh = candles[firstIndex].High;
        decimal lowestLow = candles[firstIndex].Low;
        decimal peakClose = candles[firstIndex].Close;
        decimal maxDrawdown = 0m;
        bool drawdownDefined = true;

        // Welford running mean / sum of squared deviations of the log returns.
        bool volatilityDefined = true;
        int returnCount = 0;
        double mean = 0.0;
        double sumSquares = 0.0;
        decimal previousClose = peakClose;

        for (int i = firstIndex; i <= lastIndex; i++)
        {
            var bar = candles[i];
            if (bar.High > highestHigh)
            {
                highestHigh = bar.High;
            }

            if (bar.Low < lowestLow)
            {
                lowestLow = bar.Low;
            }

            decimal close = bar.Close;
            if (close > peakClose)
            {
                peakClose = close;
            }

            if (drawdownDefined)
            {
                if (peakClose <= 0m)
                {
                    drawdownDefined = false;
                }
                else if (TryDrawdownPercent(peakClose, close, out decimal drawdown))
                {
                    if (drawdown > maxDrawdown)
                    {
                        maxDrawdown = drawdown;
                    }
                }
                else
                {
                    // The fall does not fit a decimal (corrupt prices): the maximum is not representable, so it is undefined, not an exception.
                    drawdownDefined = false;
                }
            }

            if (!volatilityDefined)
            {
                continue;
            }

            if (close <= 0m)
            {
                volatilityDefined = false;
                continue;
            }

            if (i > firstIndex)
            {
                if (!TryLogReturn(close, previousClose, out double logReturn))
                {
                    volatilityDefined = false;
                    continue;
                }

                returnCount++;
                double delta = logReturn - mean;
                mean += delta / returnCount;
                sumSquares += delta * (logReturn - mean);
            }

            previousClose = close;
        }

        int barCount = lastIndex - firstIndex + 1;
        decimal? volatility = null;
        if (volatilityDefined && barCount >= MinBarsForVolatility)
        {
            double sampleStdDev = Math.Sqrt(sumSquares / (returnCount - 1));
            volatility = IndicatorCalculationHelper.ToDecimalOrNull(sampleStdDev * PercentFactorDouble);
        }

        return new RangeRiskStats(highestHigh, lowestLow, drawdownDefined ? maxDrawdown : null, volatility);
    }

    /// <summary>(peak - close) / peak * 100 for a positive peak; false when an intermediate decimal result overflows.</summary>
    private static bool TryDrawdownPercent(decimal peakClose, decimal close, out decimal drawdownPercent)
    {
        try
        {
            drawdownPercent = (peakClose - close) / peakClose * PercentFactor;
            return true;
        }
        catch (OverflowException)
        {
            drawdownPercent = 0m;
            return false;
        }
    }

    /// <summary>ln(close / previousClose) of two positive closes; false when the decimal ratio does not fit a decimal.</summary>
    private static bool TryLogReturn(decimal close, decimal previousClose, out double logReturn)
    {
        try
        {
            logReturn = Math.Log((double)(close / previousClose));
            return double.IsFinite(logReturn);
        }
        catch (OverflowException)
        {
            logReturn = 0.0;
            return false;
        }
    }
}
