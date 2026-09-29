using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Analysis.Sankey;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class SankeyViewModelTests
{
    private static readonly SankeyMeasure TestMeasure = new(
        SankeyMeasureKind.Volume,
        "shares",
        SankeyProvenance.Observed,
        "test_v1");

    [Fact]
    public void InitialState_HasNoData()
    {
        using var dataSource = new SankeyDataSource();
        using var viewModel = new SankeyViewModel(dataSource);

        Assert.False(viewModel.HasData);
        Assert.Null(viewModel.Snapshot);
        Assert.Empty(viewModel.NodeItems);
        Assert.Empty(viewModel.EdgeItems);
        Assert.Null(viewModel.FocusedNodeId);
        Assert.Null(viewModel.HoveredNodeId);
        Assert.Null(viewModel.HoveredEdgeId);
    }

    [Fact]
    public void SetGraph_UpdatesViewModelCollectionsAndState()
    {
        using var dataSource = new SankeyDataSource();
        using var viewModel = new SankeyViewModel(dataSource);

        var nodes = new[] { new SankeyNode(1), new SankeyNode(2) };
        var edges = new[] { new SankeyEdge(10, 1, 2, 100m) };

        dataSource.SetGraph(nodes, edges, TestMeasure);

        Assert.True(viewModel.HasData);
        Assert.NotNull(viewModel.Snapshot);
        Assert.Equal(2, viewModel.NodeItems.Count);
        Assert.Equal(1, viewModel.EdgeItems.Count);

        // Node item verification
        Assert.Equal(1, viewModel.NodeItems[0].Id);
        Assert.Equal(100m, viewModel.NodeItems[0].Capacity);
        Assert.Equal(2, viewModel.NodeItems[1].Id);
        Assert.Equal(100m, viewModel.NodeItems[1].Capacity);

        // Edge item verification
        Assert.Equal(10, viewModel.EdgeItems[0].Id);
        Assert.Equal(1, viewModel.EdgeItems[0].SourceId);
        Assert.Equal(2, viewModel.EdgeItems[0].TargetId);
        Assert.Equal(100m, viewModel.EdgeItems[0].Weight);

        Assert.Contains("Volume", viewModel.MeasureSummary);
        Assert.Contains("shares", viewModel.MeasureSummary);
    }

    [Fact]
    public void ClearGraph_ResetsViewModelState()
    {
        using var dataSource = new SankeyDataSource();
        using var viewModel = new SankeyViewModel(dataSource);

        var nodes = new[] { new SankeyNode(1), new SankeyNode(2) };
        var edges = new[] { new SankeyEdge(10, 1, 2, 100m) };
        dataSource.SetGraph(nodes, edges, TestMeasure);

        viewModel.SelectNode(1);
        Assert.Equal(1, viewModel.FocusedNodeId);

        dataSource.Clear();

        Assert.False(viewModel.HasData);
        Assert.Null(viewModel.Snapshot);
        Assert.Empty(viewModel.NodeItems);
        Assert.Empty(viewModel.EdgeItems);
        Assert.Null(viewModel.FocusedNodeId);
    }

    [Fact]
    public void SelectNode_UpdatesFocusedNodeId()
    {
        using var dataSource = new SankeyDataSource();
        using var viewModel = new SankeyViewModel(dataSource);

        viewModel.SelectNode(42);
        Assert.Equal(42, viewModel.FocusedNodeId);

        viewModel.SelectNode(null);
        Assert.Null(viewModel.FocusedNodeId);
    }

    [Fact]
    public void SetGraph_WithSkipLinks_AutomaticallyRoutesAndHidesDummyArtifactsFromViewModel()
    {
        using var dataSource = new SankeyDataSource();
        using var viewModel = new SankeyViewModel(dataSource);

        // Nodes 1 (Layer 0), 2 (Layer 1), 3 (Layer 2)
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2), new SankeyNode(3) };
        // Edges: 1->2, 2->3, and skip-link 1->3
        var edges = new[]
        {
            new SankeyEdge(10, 1, 2, 50m),
            new SankeyEdge(20, 2, 3, 50m),
            new SankeyEdge(30, 1, 3, 25m)
        };

        dataSource.SetGraph(nodes, edges, TestMeasure);

        Assert.True(viewModel.HasData);
        Assert.NotNull(viewModel.Snapshot);

        // Underlying snapshot has routed dummy nodes
        Assert.True(viewModel.Snapshot.Nodes.Length > 3);

        // ViewModel collections MUST filter out dummy nodes
        Assert.Equal(3, viewModel.NodeItems.Count);
        Assert.DoesNotContain(viewModel.NodeItems, n => SankeyConstants.IsDummyNode(n.Id));
        Assert.Contains(viewModel.NodeItems, n => n.Id == 1);
        Assert.Contains(viewModel.NodeItems, n => n.Id == 2);
        Assert.Contains(viewModel.NodeItems, n => n.Id == 3);

        // ViewModel collections MUST reconstruct logical edges
        Assert.Equal(3, viewModel.EdgeItems.Count);
        Assert.DoesNotContain(viewModel.EdgeItems, e => SankeyConstants.IsDummyEdge(e.Id));
        Assert.DoesNotContain(viewModel.EdgeItems, e => SankeyConstants.IsDummyNode(e.SourceId) || SankeyConstants.IsDummyNode(e.TargetId));

        var skipEdge = Assert.Single(viewModel.EdgeItems, e => e.Id == 30);
        Assert.Equal(1, skipEdge.SourceId);
        Assert.Equal(3, skipEdge.TargetId);
        Assert.Equal(25m, skipEdge.Weight);
    }
}
