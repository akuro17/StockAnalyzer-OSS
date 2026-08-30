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
}
