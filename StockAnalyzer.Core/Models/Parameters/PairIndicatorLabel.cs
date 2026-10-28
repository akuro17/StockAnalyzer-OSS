using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The one definition of the display label of the two-symbol Pair Ratio / Pair Spread indicators, shared by the parameter
/// (<see cref="CorePairParameterBase.GetDisplayName"/>) and the indicator name so both always read the same.
/// </summary>
internal static class PairIndicatorLabel
{
    private const string ChartPricePrefix = "A:";
    private const string ComparisonPricePrefix = "B:";
    private const string PartsOpen = "(";
    private const string PartsSeparator = ", ";
    private const string PartsClose = ")";

    /// <summary>
    /// <paramref name="typeLabel"/> alone when nothing differs from the defaults; otherwise <c>typeLabel(parts)</c> with, in this order,
    /// the upper-cased comparison symbol, <c>A:price</c> when the chart symbol does not use Close, <c>B:price</c> when the comparison symbol does not use Close.
    /// </summary>
    internal static string Format(string typeLabel, string comparisonSymbol, PriceType priceSource, PriceType comparisonPriceSource)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(comparisonSymbol))
        {
            parts.Add(comparisonSymbol.Trim().ToUpperInvariant());
        }

        if (priceSource != PriceType.Close)
        {
            parts.Add(ChartPricePrefix + priceSource);
        }

        if (comparisonPriceSource != PriceType.Close)
        {
            parts.Add(ComparisonPricePrefix + comparisonPriceSource);
        }

        return parts.Count == 0 ? typeLabel : typeLabel + PartsOpen + string.Join(PartsSeparator, parts) + PartsClose;
    }
}
