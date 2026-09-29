using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis.Sankey;

namespace StockAnalyzer.Avalonia.ViewModels;

public sealed record SankeyNodeItemViewModel(
    int Id,
    decimal Incoming,
    decimal Outgoing,
    decimal Capacity,
    decimal Balance,
    int Layer,
    int Order);

public sealed record SankeyEdgeItemViewModel(
    int Id,
    int SourceId,
    int TargetId,
    decimal Weight);

/// <summary>
/// ViewModel coordinating the Sankey plot view, value lists, and data source events.
/// </summary>
public sealed partial class SankeyViewModel : ViewModelBase, IDisposable
{
    private readonly ISankeyDataSource _dataSource;

    [ObservableProperty]
    private SankeySnapshot? _snapshot;

    [ObservableProperty]
    private int? _focusedNodeId;

    [ObservableProperty]
    private int? _hoveredNodeId;

    [ObservableProperty]
    private int? _hoveredEdgeId;

    [ObservableProperty]
    private string _measureSummary = string.Empty;

    [ObservableProperty]
    private bool _hasData;

    public BulkObservableCollection<SankeyNodeItemViewModel> NodeItems { get; } = new();
    public BulkObservableCollection<SankeyEdgeItemViewModel> EdgeItems { get; } = new();

    public SankeyViewModel(ISankeyDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _dataSource.Changed += OnDataSourceChanged;
        RefreshFromDataSource();
    }

    private void OnDataSourceChanged(object? sender, EventArgs e)
    {
        RefreshFromDataSource();
    }

    public void RefreshFromDataSource()
    {
        Snapshot = _dataSource.Current;

        if (Snapshot == null || Snapshot.Nodes.Length == 0)
        {
            HasData = false;
            MeasureSummary = string.Empty;
            FocusedNodeId = null;
            HoveredNodeId = null;
            HoveredEdgeId = null;
            NodeItems.ReplaceRange(Array.Empty<SankeyNodeItemViewModel>());
            EdgeItems.ReplaceRange(Array.Empty<SankeyEdgeItemViewModel>());
            return;
        }

        HasData = true;
        MeasureSummary = $"{Snapshot.Measure.Kind} ({Snapshot.Measure.UnitCode}) - {Snapshot.Measure.Provenance} [{Snapshot.Measure.DefinitionId}]";

        var newNodes = new List<SankeyNodeItemViewModel>(Snapshot.Nodes.Length);
        for (int i = 0; i < Snapshot.Nodes.Length; i++)
        {
            var node = Snapshot.Nodes[i];
            if (SankeyConstants.IsDummyNode(node.Id))
                continue;

            newNodes.Add(new SankeyNodeItemViewModel(
                node.Id,
                node.Incoming,
                node.Outgoing,
                node.Capacity,
                node.Balance,
                node.Layer,
                node.Order));
        }

        var edgeMap = new Dictionary<int, (int SourceId, int TargetId, decimal Weight)>();
        for (int i = 0; i < Snapshot.Edges.Length; i++)
        {
            var edge = Snapshot.Edges[i];
            int origId = SankeyConstants.GetOriginalEdgeId(edge.Id);
            if (!edgeMap.TryGetValue(origId, out var existing))
            {
                edgeMap[origId] = (edge.SourceId, edge.TargetId, edge.Weight);
            }
            else
            {
                int src = !SankeyConstants.IsDummyNode(edge.SourceId) ? edge.SourceId : existing.SourceId;
                int tgt = !SankeyConstants.IsDummyNode(edge.TargetId) ? edge.TargetId : existing.TargetId;
                edgeMap[origId] = (src, tgt, edge.Weight);
            }
        }

        var newEdges = new List<SankeyEdgeItemViewModel>(edgeMap.Count);
        foreach (var (edgeId, (srcId, tgtId, weight)) in edgeMap)
        {
            newEdges.Add(new SankeyEdgeItemViewModel(
                edgeId,
                srcId,
                tgtId,
                weight));
        }

        NodeItems.ReplaceRange(newNodes);
        EdgeItems.ReplaceRange(newEdges);
    }

    public void SelectNode(int? nodeId)
    {
        FocusedNodeId = nodeId;
    }

    public void Dispose()
    {
        _dataSource.Changed -= OnDataSourceChanged;
    }
}
