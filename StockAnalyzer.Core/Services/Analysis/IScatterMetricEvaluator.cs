using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>One ticker's pair of metric values. A value that could not be read is <see cref="double.NaN"/>.</summary>
public readonly record struct ScatterEvaluationItem(string Ticker, double X, double Y);

/// <summary>The evaluation of one X metric and one Y metric over a list of tickers.</summary>
public sealed record ScatterEvaluationResult
{
    /// <summary>One item per input ticker, in the order of the input.</summary>
    public required IReadOnlyList<ScatterEvaluationItem> Items { get; init; }

    /// <summary>The number of input tickers (= <see cref="Items"/> count).</summary>
    public int TotalCount { get; init; }

    /// <summary>The tickers whose X and Y are both finite.</summary>
    public int ValidCount { get; init; }

    /// <summary>The tickers whose evaluation threw (an empty history is not a failure).</summary>
    public int FailedCount { get; init; }

    /// <summary>The fixed value range of the X metric, or null when the metric has none.</summary>
    public AxisRange? XFixedRange { get; init; }

    /// <summary>The fixed value range of the Y metric, or null when the metric has none.</summary>
    public AxisRange? YFixedRange { get; init; }
}

/// <summary>
/// Computes the (X, Y) value of every ticker for two registered scatter metrics, reading each ticker's latest bar of the metric's own time frame
/// (Day, Week or Month), moved back by the metric's Offset.
/// </summary>
public interface IScatterMetricEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="x"/> and <paramref name="y"/> for every ticker. The result has one item per ticker in the order of
    /// <paramref name="tickers"/>, however the work is scheduled.
    /// </summary>
    /// <exception cref="System.ArgumentException">A metric fails <see cref="ScatterMetricRules.Validate"/>.</exception>
    /// <exception cref="System.OperationCanceledException">The token was cancelled.</exception>
    Task<ScatterEvaluationResult> ComputeAsync(
        IReadOnlyList<string> tickers,
        ScatterMetricDefinition x,
        ScatterMetricDefinition y,
        CancellationToken ct);
}
