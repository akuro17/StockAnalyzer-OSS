using System;
using StockAnalyzer.Core.Analysis.Sankey;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Immutable snapshot representing the output of a completed Sankey decomposition run.
/// Ownership of the arrays is isolated from the engine's scratchpad buffers.
/// </summary>
public sealed record SankeySnapshot(
    SankeyNodeValue[] Nodes,
    SankeyEdgeValue[] Edges,
    SankeyMeasure Measure,
    long Revision)
{
    /// <summary>
    /// An empty, valid snapshot sentinel.
    /// </summary>
    public static SankeySnapshot Empty => new(
        Array.Empty<SankeyNodeValue>(),
        Array.Empty<SankeyEdgeValue>(),
        new SankeyMeasure(SankeyMeasureKind.Other, "none", SankeyProvenance.Observed, "empty"),
        0L);
}
