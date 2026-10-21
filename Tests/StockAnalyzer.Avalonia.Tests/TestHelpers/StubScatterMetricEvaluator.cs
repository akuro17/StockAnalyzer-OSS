#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>
/// An <see cref="IScatterMetricEvaluator"/> for view tests: X = index, Y = twice the index, and the fixed range of a metric is whatever the test says.
/// </summary>
public sealed class StubScatterMetricEvaluator : IScatterMetricEvaluator
{
    /// <summary>The fixed range handed out for a metric (by label); null = none.</summary>
    public Func<string, AxisRange?> FixedRangeOf { get; set; } = _ => null;

    public Task<ScatterEvaluationResult> ComputeAsync(
        IReadOnlyList<string> tickers, ScatterMetricDefinition x, ScatterMetricDefinition y, CancellationToken ct)
    {
        var items = new List<ScatterEvaluationItem>(tickers.Count);
        for (int i = 0; i < tickers.Count; i++)
        {
            items.Add(new ScatterEvaluationItem(tickers[i], i, i * 2.0));
        }

        return Task.FromResult(new ScatterEvaluationResult
        {
            Items = items,
            TotalCount = items.Count,
            ValidCount = items.Count,
            XFixedRange = FixedRangeOf(x.Label),
            YFixedRange = FixedRangeOf(y.Label),
        });
    }
}
