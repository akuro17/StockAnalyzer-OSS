namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// A price series as a <see cref="decimal"/> array plus an O(1) test whether a window of bars contains a missing price (a null price). The price-native robust statistics
/// (quantile bands, Theil-Sen) read windows of it without any <c>double</c> conversion. A window of <c>Period</c> bars ending at bar i is <c>[i - Period + 1, i]</c>.
/// </summary>
public sealed class PriceSeriesWindow
{
    private readonly InvalidValueIndex _missing;

    public PriceSeriesWindow(IReadOnlyList<decimal?> prices)
    {
        int count = prices.Count;
        Values = new decimal[count];
        _missing = new InvalidValueIndex(count);
        for (int i = 0; i < count; i++)
        {
            decimal? price = prices[i];
            Values[i] = price ?? 0m;
            _missing.Add(!price.HasValue);
        }
    }

    /// <summary>Number of bars.</summary>
    public int Count => Values.Length;

    /// <summary>The prices by bar index (0 where missing: read only windows for which <see cref="IsComplete"/> is true).</summary>
    public decimal[] Values { get; }

    /// <summary>True when every price of the bars [<paramref name="first"/>, <paramref name="last"/>] is present.</summary>
    public bool IsComplete(int first, int last) => _missing.IsClean(first, last);
}
