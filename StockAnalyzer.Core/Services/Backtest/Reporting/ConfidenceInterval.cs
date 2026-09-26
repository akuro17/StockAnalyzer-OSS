using System;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// A percentile-bootstrap confidence interval paired with a <see cref="MetricValue"/> point estimate (e.g.
/// <see cref="BacktestReport.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval"/>). Reusable for
/// any future bootstrap-based metric this project adds. Rule DT-2 (double for statistics) does not apply
/// here: like every other ratio metric in this report, the bound is exposed as <c>decimal</c> for
/// consistency with <see cref="MetricValue.Value"/>.
/// </summary>
public readonly record struct ConfidenceInterval
{
    public decimal Lower { get; }
    public decimal Upper { get; }

    /// <summary>[JsonConstructor] required for the same reason as <see cref="MetricValue"/>'s: a struct
    /// with only a parameterized constructor and no property setters otherwise deserializes via the
    /// implicit parameterless constructor, silently producing a zeroed-out default instead.</summary>
    [JsonConstructor]
    public ConfidenceInterval(decimal lower, decimal upper)
    {
        if (lower > upper)
        {
            throw new ArgumentException("Lower must be <= Upper.", nameof(lower));
        }
        Lower = lower;
        Upper = upper;
    }
}
