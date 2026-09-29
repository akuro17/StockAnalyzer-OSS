namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Defines parameter contracts for indicators that compare against a secondary benchmark ticker.
/// </summary>
public interface ICrossTickerParameter
{
    /// <summary>
    /// Gets or sets the ticker symbol of the comparison benchmark.
    /// </summary>
    string ComparisonSymbol { get; set; }

    /// <summary>
    /// Gets or sets the price source type used for the comparison benchmark.
    /// </summary>
    PriceType ComparisonPriceSource { get; set; }
}
