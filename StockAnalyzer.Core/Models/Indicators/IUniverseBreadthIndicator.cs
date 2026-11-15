using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Contract for parameter objects carrying a workspace ticker group GUID for universe-breadth calculation.
/// Implemented by parameter classes so validation can inspect <c>UniverseGroupId</c> without listing types.
/// </summary>
public interface IUniverseBreadthParameter
{
    /// <summary>
    /// Workspace group identifier formatted as a GUID string, or empty string when unset.
    /// </summary>
    string UniverseGroupId { get; set; }
}

/// <summary>
/// Contract for indicators whose series depend on a universe-wide <see cref="MarketBreadthResult"/>
/// computed across a workspace ticker group rather than on a single symbol alone.
/// </summary>
public interface IUniverseBreadthIndicator
{
    /// <summary>
    /// Workspace group identifier formatted as a GUID string, or empty string when no universe is selected.
    /// </summary>
    string UniverseGroupId { get; set; }

    /// <summary>
    /// Market breadth calculation parameters required from <c>IMarketBreadthSeriesProvider</c>.
    /// </summary>
    MarketBreadthParameters BreadthParameters { get; }

    /// <summary>
    /// Injects the precomputed universe breadth result and the timeframe used to align trading dates.
    /// </summary>
    void SetUniverseBreadth(MarketBreadthResult? result, TimeFrame timeFrame);
}
