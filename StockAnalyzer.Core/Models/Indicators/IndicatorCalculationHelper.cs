using System.Buffers;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Common calculation utilities for indicators.
/// </summary>
public static class IndicatorCalculationHelper
{
    /// <summary>
    /// Calculates Exponential Moving Average (EMA) from a list of decimal values.
    /// </summary>
    /// <param name="prices">List of prices (non-nullable)</param>
    /// <param name="period">EMA period</param>
    /// <returns>List of EMA values with nulls for warmup period</returns>
    public static List<decimal?> CalculateEma(IReadOnlyList<decimal> prices, int period)
    {
        var result = new List<decimal?>();
        if (prices.Count == 0 || period <= 0) return result;

        decimal multiplier = 2m / (period + 1);
        decimal? ema = null;

        for (int i = 0; i < prices.Count; i++)
        {
            if (i < period - 1)
            {
                result.Add(null);
            }
            else if (i == period - 1)
            {
                // First EMA is calculated as SMA
                decimal sum = 0;
                for (int j = 0; j < period; j++)
                    sum += prices[i - j];
                ema = sum / period;
                result.Add(ema);
            }
            else
            {
                ema = (prices[i] - ema!.Value) * multiplier + ema.Value;
                result.Add(ema);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates EMA from a list of nullable decimal values.
    /// </summary>
    /// <param name="data">List of nullable prices</param>
    /// <param name="period">EMA period</param>
    /// <returns>List of EMA values</returns>
    public static List<decimal?> CalculateEmaWithNulls(IReadOnlyList<decimal?> data, int period)
    {
        var result = new List<decimal?>();
        if (data.Count == 0 || period <= 0) return result;

        decimal multiplier = 2m / (period + 1);
        decimal? ema = null;
        int validCount = 0;

        for (int i = 0; i < data.Count; i++)
        {
            if (!data[i].HasValue)
            {
                result.Add(null);
                continue;
            }

            validCount++;
            if (validCount < period)
            {
                result.Add(null);
            }
            else if (validCount == period)
            {
                // Calculate initial SMA from first 'period' valid values
                decimal sum = 0;
                int foundCount = 0;
                for (int j = i; j >= 0 && foundCount < period; j--)
                {
                    if (data[j].HasValue)
                    {
                        sum += data[j]!.Value;
                        foundCount++;
                    }
                }
                ema = sum / period;
                result.Add(ema);
            }
            else
            {
                ema = (data[i]!.Value - ema!.Value) * multiplier + ema.Value;
                result.Add(ema);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates Simple Moving Average (SMA) from a list of decimal values.
    /// </summary>
    /// <param name="prices">List of prices</param>
    /// <param name="period">SMA period</param>
    /// <returns>List of SMA values with nulls for warmup period</returns>
    public static List<decimal?> CalculateSma(IReadOnlyList<decimal> prices, int period)
    {
        var result = new List<decimal?>();
        if (prices.Count == 0 || period <= 0) return result;

        decimal sum = 0;
        for (int i = 0; i < prices.Count; i++)
        {
            sum += prices[i];
            if (i >= period)
            {
                sum -= prices[i - period];
            }

            if (i >= period - 1)
            {
                result.Add(sum / period);
            }
            else
            {
                result.Add(null);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates Pearson product-moment correlation coefficient over a rolling window between two series.
    /// </summary>
    /// <param name="seriesA">Primary series (nullable decimal)</param>
    /// <param name="seriesB">Secondary series (nullable decimal)</param>
    /// <param name="period">Rolling window period (must be >= 2)</param>
    /// <returns>Rolling correlation values clamped to [-1.0, 1.0], with nulls for warmup, missing data, or zero variance / undefined cases</returns>
    /// <exception cref="ArgumentNullException">Thrown when seriesA or seriesB is null.</exception>
    /// <exception cref="ArgumentException">Thrown when series lengths do not match.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when period is less than 2.</exception>
    public static List<decimal?> CalculateRollingPearsonCorrelation(IReadOnlyList<decimal?> seriesA, IReadOnlyList<decimal?> seriesB, int period)
    {
        if (seriesA == null) throw new ArgumentNullException(nameof(seriesA));
        if (seriesB == null) throw new ArgumentNullException(nameof(seriesB));
        if (seriesA.Count != seriesB.Count) throw new ArgumentException("Series lengths must match.", nameof(seriesB));
        if (period < 2) throw new ArgumentOutOfRangeException(nameof(period), "Period must be at least 2.");

        int count = seriesA.Count;
        var result = new List<decimal?>(count);
        if (count == 0) return result;

        for (int t = 0; t < count; t++)
        {
            if (t < period - 1)
            {
                result.Add(null);
                continue;
            }

            bool hasNull = false;
            for (int i = t - period + 1; i <= t; i++)
            {
                if (!seriesA[i].HasValue || !seriesB[i].HasValue)
                {
                    hasNull = true;
                    break;
                }
            }

            if (hasNull)
            {
                result.Add(null);
                continue;
            }

            try
            {
                checked
                {
                    decimal sumX = 0m;
                    decimal sumY = 0m;
                    decimal sumX2 = 0m;
                    decimal sumY2 = 0m;
                    decimal sumXY = 0m;

                    for (int i = t - period + 1; i <= t; i++)
                    {
                        decimal x = seriesA[i]!.Value;
                        decimal y = seriesB[i]!.Value;
                        sumX += x;
                        sumY += y;
                        sumX2 += x * x;
                        sumY2 += y * y;
                        sumXY += x * y;
                    }

                    decimal n = period;
                    decimal numerator = n * sumXY - sumX * sumY;
                    decimal denomX = n * sumX2 - sumX * sumX;
                    decimal denomY = n * sumY2 - sumY * sumY;

                    if (denomX <= 0m || denomY <= 0m)
                    {
                        result.Add(null);
                    }
                    else
                    {
                        double dx = (double)denomX;
                        double dy = (double)denomY;
                        double denom = Math.Sqrt(dx * dy);
                        if (denom <= 0.0 || double.IsNaN(denom) || double.IsInfinity(denom))
                        {
                            result.Add(null);
                        }
                        else
                        {
                            double r = (double)numerator / denom;
                            r = Math.Clamp(r, -1.0, 1.0);
                            result.Add((decimal)r);
                        }
                    }
                }
            }
            catch (OverflowException)
            {
                result.Add(null);
            }
        }

        return result;
    }

    /// <summary>
    /// Converts a price or volume series [P_0, P_1, ..., P_{N-1}] into a log returns series
    /// R_t = ln(P_t / P_{t-1}). Index 0 is always null. If either P_t or P_{t-1} is null or <= 0,
    /// the return at index t is null.
    /// </summary>
    public static List<decimal?> ConvertToLogReturns(IReadOnlyList<decimal?> series)
    {
        if (series == null || series.Count == 0) return new List<decimal?>();

        var returns = new List<decimal?>(series.Count) { null }; // First element has no return

        for (int i = 1; i < series.Count; i++)
        {
            var curr = series[i];
            var prev = series[i - 1];

            if (curr.HasValue && prev.HasValue && curr.Value > 0m && prev.Value > 0m)
            {
                double c = (double)curr.Value;
                double p = (double)prev.Value;
                double logReturn = Math.Log(c / p);
                if (double.IsNaN(logReturn) || double.IsInfinity(logReturn))
                {
                    returns.Add(null);
                }
                else
                {
                    returns.Add((decimal)logReturn);
                }
            }
            else
            {
                returns.Add(null);
            }
        }

        return returns;
    }

    /// <summary>
    /// Calculates Kaufman's Adaptive Moving Average (KAMA / AMA) from a nullable price series.
    /// <para>
    /// Formal Contract Specifications:
    /// - Time Complexity: O(T) overall, O(1) per-bar rolling updates using an (N+1) price buffer and N diff buffer.
    /// - Space Complexity: O(N) internal working buffers; zero reallocation via pre-allocated result capacity.
    /// - State Machine: Null input prices bypass state updates (emit null, do not advance valid count).
    /// - Warmup Boundary: Indices 0 to Period-1 emit null; index Period-1 records initial SMA internally; index Period onwards emits KAMA recurrence values.
    /// - Transient Guard: If fastPeriod &gt;= slowPeriod during UI commit transitions, fastPeriod is clamped to max(1, slowPeriod - 1).
    /// </para>
    /// </summary>
    /// <param name="prices">Input price series (e.g. Close prices). Non-null list required; elements may be null.</param>
    /// <param name="period">Lookback period for Efficiency Ratio (N). Domain: [1, 10000].</param>
    /// <param name="fastPeriod">Fastest EMA constant period. Domain: [1, 1000].</param>
    /// <param name="slowPeriod">Slowest EMA constant period. Domain: [1, 10000].</param>
    /// <returns>Calculated KAMA series guaranteeing result.Count == prices.Count.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="prices"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any period parameter violates its domain.</exception>
    public static List<decimal?> CalculateKama(
        IReadOnlyList<decimal?> prices,
        int period = IndicatorDefaultConstants.KamaPeriod,
        int fastPeriod = IndicatorDefaultConstants.KamaFastPeriod,
        int slowPeriod = IndicatorDefaultConstants.KamaSlowPeriod)
    {
        if (prices == null) throw new ArgumentNullException(nameof(prices));
        if (period < 1 || period > 10000) throw new ArgumentOutOfRangeException(nameof(period), "Period must be between 1 and 10000.");
        if (fastPeriod < 1 || fastPeriod > 1000) throw new ArgumentOutOfRangeException(nameof(fastPeriod), "Fast period must be between 1 and 1000.");
        if (slowPeriod < 1 || slowPeriod > 10000) throw new ArgumentOutOfRangeException(nameof(slowPeriod), "Slow period must be between 1 and 10000.");

        int totalLength = prices.Count;
        var result = new List<decimal?>(totalLength);
        if (totalLength == 0) return result;

        int effectiveFast = fastPeriod;
        int effectiveSlow = slowPeriod;
        if (effectiveFast >= effectiveSlow)
        {
            effectiveFast = Math.Max(1, effectiveSlow - 1);
        }

        decimal fastSC = 2.0m / (effectiveFast + 1.0m);
        decimal slowSC = 2.0m / (effectiveSlow + 1.0m);

        decimal[] priceBuffer = new decimal[period + 1];
        decimal[] diffBuffer = new decimal[period];
        int validCount = 0;
        decimal? lastPrice = null;
        decimal currentVolatility = 0.0m;
        decimal? previousKama = null;

        for (int i = 0; i < totalLength; i++)
        {
            decimal? p = prices[i];

            if (!p.HasValue)
            {
                result.Add(null);
                continue;
            }

            decimal currentPrice = p.Value;

            if (validCount == 0)
            {
                lastPrice = currentPrice;
                priceBuffer[0] = currentPrice;
                validCount = 1;
                result.Add(null);
                continue;
            }

            decimal diff = Math.Abs(currentPrice - lastPrice!.Value);
            lastPrice = currentPrice;

            int diffIndex = (validCount - 1) % period;
            if (validCount > period)
            {
                decimal oldDiff = diffBuffer[diffIndex];
                currentVolatility += diff - oldDiff;
                if (currentVolatility < 0.0m) currentVolatility = 0.0m;
            }
            else
            {
                currentVolatility += diff;
            }
            diffBuffer[diffIndex] = diff;

            int priceIndex = validCount % (period + 1);
            priceBuffer[priceIndex] = currentPrice;

            validCount++;

            if (validCount < period)
            {
                result.Add(null);
            }
            else if (validCount == period)
            {
                decimal sum = 0.0m;
                for (int j = 0; j < period; j++) sum += priceBuffer[j];
                previousKama = sum / period;
                result.Add(null);
            }
            else
            {
                int oldestPriceIndex = (validCount - 1 - period) % (period + 1);
                decimal oldestPrice = priceBuffer[oldestPriceIndex];
                decimal change = Math.Abs(currentPrice - oldestPrice);

                decimal er = (currentVolatility <= 0.0m) ? 0.0m : Math.Min(1.0m, change / currentVolatility);
                decimal sc = er * (fastSC - slowSC) + slowSC;
                decimal scSquared = sc * sc;

                previousKama = previousKama!.Value + scSquared * (currentPrice - previousKama.Value);
                result.Add(previousKama);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates the Adaptive Moving Average (AMA) for the given price series.
    /// Adapts smoothing speed based on the Efficiency Ratio (ER) and volatility.
    /// </summary>
    /// <param name="prices">Input price series (may contain nulls).</param>
    /// <param name="period">Lookback period for Efficiency Ratio (ER) and Volatility (2 to 10000).</param>
    /// <param name="fastPeriod">Fastest EMA smoothing period (1 to 1000).</param>
    /// <param name="slowPeriod">Slowest EMA smoothing period (1 to 10000).</param>
    /// <returns>Calculated AMA series guaranteeing result.Count == prices.Count.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="prices"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any period parameter violates its domain.</exception>
    public static List<decimal?> CalculateAma(
        IReadOnlyList<decimal?> prices,
        int period = IndicatorDefaultConstants.AmaPeriod,
        int fastPeriod = IndicatorDefaultConstants.AmaFastPeriod,
        int slowPeriod = IndicatorDefaultConstants.AmaSlowPeriod)
    {
        if (prices == null) throw new ArgumentNullException(nameof(prices));
        if (period < 2 || period > 10000) throw new ArgumentOutOfRangeException(nameof(period), "Period must be between 2 and 10000.");
        if (fastPeriod < 1 || fastPeriod > 1000) throw new ArgumentOutOfRangeException(nameof(fastPeriod), "Fast period must be between 1 and 1000.");
        if (slowPeriod < 1 || slowPeriod > 10000) throw new ArgumentOutOfRangeException(nameof(slowPeriod), "Slow period must be between 1 and 10000.");

        int totalLength = prices.Count;
        var result = new List<decimal?>(totalLength);
        if (totalLength == 0) return result;

        int effectiveFast = fastPeriod;
        int effectiveSlow = slowPeriod;
        if (effectiveFast >= effectiveSlow)
        {
            effectiveFast = Math.Max(1, effectiveSlow - 1);
        }

        decimal fastSC = 2.0m / (effectiveFast + 1.0m);
        decimal slowSC = 2.0m / (effectiveSlow + 1.0m);

        decimal[] priceBuffer = new decimal[period + 1];
        decimal[] diffBuffer = new decimal[period];
        int validCount = 0;
        decimal? lastPrice = null;
        decimal currentVolatility = 0.0m;
        decimal? previousAma = null;

        for (int i = 0; i < totalLength; i++)
        {
            decimal? p = prices[i];

            if (!p.HasValue)
            {
                result.Add(null);
                continue;
            }

            decimal currentPrice = p.Value;

            if (validCount == 0)
            {
                lastPrice = currentPrice;
                priceBuffer[0] = currentPrice;
                validCount = 1;
                result.Add(null);
                continue;
            }

            decimal diff = Math.Abs(currentPrice - lastPrice!.Value);
            lastPrice = currentPrice;

            int diffIndex = (validCount - 1) % period;
            if (validCount > period)
            {
                decimal oldDiff = diffBuffer[diffIndex];
                currentVolatility += diff - oldDiff;
                if (currentVolatility < 0.0m) currentVolatility = 0.0m;
            }
            else
            {
                currentVolatility += diff;
            }
            diffBuffer[diffIndex] = diff;

            int priceIndex = validCount % (period + 1);
            priceBuffer[priceIndex] = currentPrice;

            validCount++;

            if (validCount < period)
            {
                result.Add(null);
            }
            else if (validCount == period)
            {
                decimal sum = 0.0m;
                for (int j = 0; j < period; j++) sum += priceBuffer[j];
                previousAma = sum / period;
                result.Add(null);
            }
            else
            {
                int oldestPriceIndex = (validCount - 1 - period) % (period + 1);
                decimal oldestPrice = priceBuffer[oldestPriceIndex];
                decimal change = Math.Abs(currentPrice - oldestPrice);

                const decimal Epsilon = 1e-12m;
                decimal er = (currentVolatility <= Epsilon) ? 0.0m : Math.Min(1.0m, change / currentVolatility);
                decimal sc = er * (fastSC - slowSC) + slowSC;
                decimal scSquared = sc * sc;

                previousAma = previousAma!.Value + scSquared * (currentPrice - previousAma.Value);
                result.Add(previousAma);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates Tushar Chande's Variable Index Dynamic Average (VIDYA) from a nullable price series.
    /// <para>
    /// Formal Contract Specifications:
    /// - Time Complexity: O(T) overall, O(1) per-bar rolling updates using ring buffers of length cmoPeriod.
    /// - Space Complexity: O(M) internal working buffers; zero reallocation via pre-allocated result capacity.
    /// - Dynamic Smoothing: α_t = (2 / (smoothPeriod + 1)) * |CMO_t|.
    /// - Zero-volatility Guard: When UpSum + DnSum == 0, CMO = 0, α = 0, retaining previous VIDYA value.
    /// - Warmup Boundary: Indices 0 to cmoPeriod-1 emit null; initial seed is computed as SMA of first cmoPeriod prices; recurrence emits from index cmoPeriod onwards.
    /// </para>
    /// </summary>
    /// <param name="prices">Input price series (e.g. Close prices). Non-null list required; elements may be null.</param>
    /// <param name="smoothPeriod">Base EMA smoothing period (N). Domain: [1, 10000].</param>
    /// <param name="cmoPeriod">Chande Momentum Oscillator lookback period (M). Domain: [1, 10000].</param>
    /// <returns>Calculated VIDYA series guaranteeing result.Count == prices.Count.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="prices"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when smoothPeriod or cmoPeriod violates its domain.</exception>
    public static List<decimal?> CalculateVidya(
        IReadOnlyList<decimal?> prices,
        int smoothPeriod = IndicatorDefaultConstants.VidyaSmoothPeriod,
        int cmoPeriod = IndicatorDefaultConstants.VidyaCmoPeriod)
    {
        if (prices == null) throw new ArgumentNullException(nameof(prices));
        if (smoothPeriod < 1 || smoothPeriod > 10000) throw new ArgumentOutOfRangeException(nameof(smoothPeriod), "Smooth period must be between 1 and 10000.");
        if (cmoPeriod < 1 || cmoPeriod > 10000) throw new ArgumentOutOfRangeException(nameof(cmoPeriod), "CMO period must be between 1 and 10000.");

        int totalLength = prices.Count;
        var result = new List<decimal?>(totalLength);
        if (totalLength == 0) return result;

        decimal k = 2.0m / (smoothPeriod + 1.0m);

        decimal[] upBuffer = ArrayPool<decimal>.Shared.Rent(cmoPeriod);
        decimal[] dnBuffer = ArrayPool<decimal>.Shared.Rent(cmoPeriod);
        Array.Clear(upBuffer, 0, cmoPeriod);
        Array.Clear(dnBuffer, 0, cmoPeriod);

        try
        {
            int validCount = 0;
            decimal? lastPrice = null;
            decimal currentUpSum = 0.0m;
            decimal currentDnSum = 0.0m;
            decimal priceSeedSum = 0.0m;
            decimal? previousVidya = null;

            for (int i = 0; i < totalLength; i++)
            {
                decimal? p = prices[i];

                if (!p.HasValue)
                {
                    result.Add(null);
                    continue;
                }

                decimal currentPrice = p.Value;

                if (validCount == 0)
                {
                    lastPrice = currentPrice;
                    priceSeedSum = currentPrice;
                    validCount = 1;
                    if (cmoPeriod == 1)
                    {
                        previousVidya = currentPrice;
                    }
                    result.Add(null);
                    continue;
                }

                decimal change = currentPrice - lastPrice!.Value;
                lastPrice = currentPrice;

                decimal up = change > 0.0m ? change : 0.0m;
                decimal dn = change < 0.0m ? -change : 0.0m;

                int bufferIndex = (validCount - 1) % cmoPeriod;

                if (validCount > cmoPeriod)
                {
                    decimal oldUp = upBuffer[bufferIndex];
                    decimal oldDn = dnBuffer[bufferIndex];
                    currentUpSum += up - oldUp;
                    currentDnSum += dn - oldDn;
                    if (currentUpSum < 0.0m) currentUpSum = 0.0m;
                    if (currentDnSum < 0.0m) currentDnSum = 0.0m;
                }
                else
                {
                    currentUpSum += up;
                    currentDnSum += dn;
                }

                upBuffer[bufferIndex] = up;
                dnBuffer[bufferIndex] = dn;

                if (validCount < cmoPeriod)
                {
                    priceSeedSum += currentPrice;
                }

                validCount++;

                if (validCount <= cmoPeriod)
                {
                    if (validCount == cmoPeriod)
                    {
                        previousVidya = priceSeedSum / cmoPeriod;
                    }
                    result.Add(null);
                }
                else
                {
                    decimal denom = currentUpSum + currentDnSum;
                    decimal absCmo = 0.0m;
                    if (denom > 0.0m)
                    {
                        decimal cmo = (currentUpSum - currentDnSum) / denom;
                        absCmo = Math.Abs(cmo);
                        if (absCmo > 1.0m) absCmo = 1.0m;
                    }

                    decimal alpha = k * absCmo;
                    previousVidya = alpha * currentPrice + (1.0m - alpha) * previousVidya!.Value;
                    result.Add(previousVidya);
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(upBuffer);
            ArrayPool<decimal>.Shared.Return(dnBuffer);
        }
    }
}


