using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Coverage statistics for a single timeframe of a symbol's stored bar data.
/// </summary>
public sealed record TimeFrameCoverage(
    TimeFrame TimeFrame,
    int BarCount,
    DateTime EarliestTimestamp,
    DateTime LatestTimestamp);

/// <summary>
/// Stored history coverage summary for a ticker symbol across all supported timeframes.
/// </summary>
public sealed record SymbolCoverage(
    string Symbol,
    bool Found,
    bool HasMetadata,
    DateTimeOffset? TimeSeriesLastUpdated,
    IReadOnlyList<TimeFrameCoverage> TimeFrames)
{
    /// <summary>
    /// The time frames whose stored bars could not be read (the load failed for any reason other than cancellation). They are not in
    /// <see cref="TimeFrames"/>, so "nothing stored" (absent from both lists) differs from "stored but unreadable" (listed here).
    /// Carries no exception text.
    /// </summary>
    public IReadOnlyList<TimeFrame> UnreadableTimeFrames { get; init; } = Array.Empty<TimeFrame>();
}
