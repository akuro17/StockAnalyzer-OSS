#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// The measured result of a saved evaluation partition: the report's own mean return and worst per-episode maximum drawdown (both from
/// <see cref="RlEvaluation.Aggregate"/>), the two figures a mean can hide (the lowest episode return and how many episodes ended above zero),
/// and the partition they describe.
/// </summary>
public sealed record RlMeasuredEvaluation(string Partition, int Episodes, decimal MeanReturn, decimal WorstMaximumDrawdown,
    decimal MinReturn, int PositiveEpisodes)
{
    /// <summary>Null when <paramref name="rows"/> is empty (for example the final partition of a smoke run).</summary>
    public static RlMeasuredEvaluation? FromEpisodes(IReadOnlyList<RlEpisodeEvaluation> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return null;
        var aggregate = RlEvaluation.Aggregate(rows);
        var returns = rows.Select(row => RlEvaluation.Parse(row.TerminalReturn)).ToArray();
        return new RlMeasuredEvaluation(rows[0].Partition, rows.Count,
            RlEvaluation.Parse((string)aggregate["mean_return"]), RlEvaluation.Parse((string)aggregate["worst_maximum_drawdown"]),
            returns.Min(), returns.Count(value => value > 0m));
    }
}
