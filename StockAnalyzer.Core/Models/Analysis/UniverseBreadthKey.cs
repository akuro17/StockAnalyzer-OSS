using System;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Value-equality cache key identifying a unique universe breadth calculation request
/// (<see cref="GroupId"/>, <see cref="TimeFrame"/>, and relevant parameter windows).
/// </summary>
public readonly record struct UniverseBreadthKey(
    Guid GroupId,
    TimeFrame TimeFrame,
    int AdRatioBars,
    int McClellanFast,
    int McClellanSlow,
    int MaBars,
    int HighLowBars,
    DateOnly? EndDate = null)
{
    /// <summary>
    /// Creates a <see cref="UniverseBreadthKey"/> from a <see cref="MarketBreadthParameters"/> instance.
    /// </summary>
    public static UniverseBreadthKey From(
        Guid groupId,
        TimeFrame timeFrame,
        MarketBreadthParameters parameters,
        DateOnly? endDateOverride = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new UniverseBreadthKey(
            groupId,
            timeFrame,
            parameters.AdRatioBars,
            parameters.McClellanFast,
            parameters.McClellanSlow,
            parameters.MaBars,
            parameters.HighLowBars,
            endDateOverride ?? parameters.EndDate);
    }

    /// <summary>
    /// Materializes a <see cref="MarketBreadthParameters"/> instance matching this key.
    /// </summary>
    public MarketBreadthParameters ToParameters() => new()
    {
        TimeFrame = TimeFrame,
        AdRatioBars = AdRatioBars,
        McClellanFast = McClellanFast,
        McClellanSlow = McClellanSlow,
        MaBars = MaBars,
        HighLowBars = HighLowBars,
        EndDate = EndDate
    };
}
