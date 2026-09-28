using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Models.Confluence;

/// <summary>
/// Interface for indicator results that can provide technical signals for confluence calculation.
/// </summary>
public interface IConfluenceSignalProvider
{
    /// <summary>
    /// Returns all active signals for a specific bar index.
    /// </summary>
    /// <param name="index">The bar index to check for signals.</param>
    /// <param name="result">The actual indicator result containing the data series.</param>
    /// <param name="settings">The settings of the indicator (containing weight/group information).</param>
    /// <returns>A collection of confluence signals found at the specified index.</returns>
    IEnumerable<ConfluenceSignal> GetSignals(int index, IIndicatorResult result, CoreIndicatorSettings settings);
}
