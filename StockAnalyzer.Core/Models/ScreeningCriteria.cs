using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Defines criteria for stock screening.
/// This DTO will be used to generate queries (e.g., SQL for DuckDB) dynamically.
/// </summary>
public record ScreeningCriteria
{
    /// <summary>
    /// The timeframe to apply the screening on.
    /// </summary>
    public TimeFrame TimeFrame { get; init; } = TimeFrame.D1;

    /// <summary>
    /// A list of conditions that must be met.
    /// </summary>
    public IReadOnlyList<IScreeningCondition> Conditions { get; init; } = [];

    /// <summary>
    /// Optional limit on the number of results to return.
    /// </summary>
    public int? TopN { get; init; }

    /// <summary>
    /// Optional field to sort the results by.
    /// </summary>
    public string? SortBy { get; init; }
}
