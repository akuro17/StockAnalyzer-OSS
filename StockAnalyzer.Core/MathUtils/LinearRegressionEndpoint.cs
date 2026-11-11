namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Single implementation of the least-squares regression line endpoint shared by the LRMA and LSMA indicators
/// (they are the same calculation; formula: indicators_formulas_0230).
/// </summary>
internal static class LinearRegressionEndpoint
{
    /// <summary>
    /// Output unit: price-native (the same unit as the input price series); the window is counted in bars.
    /// Appends one value to <paramref name="destination"/> for every bar from index <c>period - 1</c> to the last bar of
    /// <paramref name="prices"/>. The window of bar <c>i</c> is <c>prices[i - j]</c> with <c>x = j</c> (j = 0 is the current bar),
    /// so the endpoint of the fitted line is its intercept. A missing price is entered as 0, and a window with a zero
    /// denominator yields <c>null</c>. The caller supplies the leading <c>period - 1</c> empty entries and guarantees
    /// <c>period</c> is within the parameter contract (1..10000) and <c>prices.Count &gt;= period</c>.
    /// </summary>
    internal static void AppendEndpoints(IReadOnlyList<decimal?> prices, int period, List<decimal?> destination)
    {
        for (int i = period - 1; i < prices.Count; i++)
        {
            decimal sumX = 0, sumY = 0, sumXy = 0, sumX2 = 0;
            for (int j = 0; j < period; j++)
            {
                decimal y = prices[i - j] ?? 0m;
                decimal x = j;

                sumX += x;
                sumY += y;
                sumXy += x * y;
                sumX2 += x * x;
            }

            decimal n = period;
            decimal denominator = n * sumX2 - sumX * sumX;

            if (denominator == 0)
            {
                destination.Add(null);
                continue;
            }

            decimal b = (n * sumXy - sumX * sumY) / denominator;
            decimal a = (sumY - b * sumX) / n;
            destination.Add(a);
        }
    }
}
