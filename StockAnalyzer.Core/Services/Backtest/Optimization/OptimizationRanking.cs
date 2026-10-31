namespace StockAnalyzer.Core.Services.Backtest.Optimization;

/// <summary>Ranking and averaging of the objective values of a parameter sweep (pure functions, no I/O).</summary>
public static class OptimizationRanking
{
    /// <summary>
    /// The indices of the entries that have a value, best first: the highest value, or the lowest when <paramref name="minimize"/>; equal values keep the
    /// lower index first. An entry without a value (a combination whose objective is not Valid) is not ranked.
    /// </summary>
    public static IEnumerable<int> Rank(IReadOnlyList<decimal?> values, bool minimize)
    {
        ArgumentNullException.ThrowIfNull(values);
        IEnumerable<int> ranked = Enumerable.Range(0, values.Count).Where(index => values[index] is not null);
        return minimize
            ? ranked.OrderBy(index => values[index]!.Value).ThenBy(index => index)
            : ranked.OrderByDescending(index => values[index]!.Value).ThenBy(index => index);
    }

    /// <summary>The simple mean of <paramref name="values"/>, or null when there is none.</summary>
    public static decimal? Mean(IReadOnlyCollection<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count == 0 ? null : values.Sum() / values.Count;
    }
}
