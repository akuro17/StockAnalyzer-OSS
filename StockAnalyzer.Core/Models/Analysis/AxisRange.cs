namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// A fixed value range of one scatter axis, in data coordinates (the unit of the plotted metric, never pixels).
/// Invariant of every instance produced by the scatter pipeline: both values are finite and <see cref="Min"/> &lt; <see cref="Max"/>.
/// </summary>
public readonly record struct AxisRange(double Min, double Max);
