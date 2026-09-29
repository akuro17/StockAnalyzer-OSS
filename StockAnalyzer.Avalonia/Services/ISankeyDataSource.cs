using System;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis.Sankey;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Data source interface providing validated Sankey snapshots to the UI presentation layer.
/// </summary>
public interface ISankeyDataSource
{
    /// <summary>
    /// Current published decomposition snapshot, or null if no valid flow data exists.
    /// </summary>
    SankeySnapshot? Current { get; }

    /// <summary>
    /// Event raised whenever the active snapshot is updated or cleared.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Analyzes the input nodes and edges, publishing a new snapshot on success.
    /// </summary>
    void SetGraph(ReadOnlySpan<SankeyNode> nodes, ReadOnlySpan<SankeyEdge> edges, in SankeyMeasure measure);

    /// <summary>
    /// Clears the active snapshot.
    /// </summary>
    void Clear();
}
