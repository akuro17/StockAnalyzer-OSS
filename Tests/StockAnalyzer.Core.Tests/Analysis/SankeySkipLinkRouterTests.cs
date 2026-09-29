using System;
using StockAnalyzer.Core.Analysis.Sankey;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class SankeySkipLinkRouterTests
{
    private static readonly SankeyMeasure TestMeasure = new(
        SankeyMeasureKind.Volume,
        "shares",
        SankeyProvenance.Observed,
        "test_v1");

    [Fact]
    public void EmptyGraph_ReturnsNoSkipLinks()
    {
        var routedNodes = new SankeyNode[10];
        var routedEdges = new SankeyEdge[10];

        SankeySkipLinkRouter.Route(
            ReadOnlySpan<SankeyNode>.Empty,
            ReadOnlySpan<SankeyEdge>.Empty,
            routedNodes,
            routedEdges,
            out var result);

        Assert.Equal(SankeyRoutingStatus.NoSkipLinks, result.Status);
        Assert.Equal(0, result.NodeCount);
        Assert.Equal(0, result.EdgeCount);
        Assert.Equal(0, result.DummyNodeCount);
    }

    [Fact]
    public void StrictlyAdjacentGraph_ReturnsNoSkipLinks()
    {
        var nodes = new[]
        {
            new SankeyNode(1),
            new SankeyNode(2),
            new SankeyNode(3)
        };
        var edges = new[]
        {
            new SankeyEdge(10, 1, 2, 60m),
            new SankeyEdge(20, 2, 3, 60m)
        };

        var routedNodes = new SankeyNode[10];
        var routedEdges = new SankeyEdge[10];

        SankeySkipLinkRouter.Route(nodes, edges, routedNodes, routedEdges, out var result);

        Assert.Equal(SankeyRoutingStatus.NoSkipLinks, result.Status);
        Assert.Equal(3, result.NodeCount);
        Assert.Equal(2, result.EdgeCount);
        Assert.Equal(0, result.DummyNodeCount);
        Assert.Equal(0, result.SegmentedEdgeCount);
    }

    [Fact]
    public void T13_SkipLinkGraph_RoutesSuccessfully_AndPassesCoreDecomposition()
    {
        // Graph with skip-link:
        // A(1) -> B(2) [60m]
        // B(2) -> C(3) [60m]
        // A(1) -> C(3) [40m] (Skip-link: Layer 0 to Layer 2, skipping Layer 1)
        var nodes = new[]
        {
            new SankeyNode(1),
            new SankeyNode(2),
            new SankeyNode(3)
        };
        var edges = new[]
        {
            new SankeyEdge(10, 1, 2, 60m),
            new SankeyEdge(20, 2, 3, 60m),
            new SankeyEdge(30, 1, 3, 40m)
        };

        var routedNodes = new SankeyNode[10];
        var routedEdges = new SankeyEdge[10];

        SankeySkipLinkRouter.Route(nodes, edges, routedNodes, routedEdges, out var routingResult);

        Assert.Equal(SankeyRoutingStatus.Success, routingResult.Status);
        Assert.Equal(4, routingResult.NodeCount); // 3 original + 1 dummy
        Assert.Equal(4, routingResult.EdgeCount); // 2 original + 2 segmented from edge 30
        Assert.Equal(1, routingResult.DummyNodeCount);
        Assert.Equal(2, routingResult.SegmentedEdgeCount);

        // Verify that dummy node ID is in the designated dummy range
        int dummyNodeId = routedNodes[3].Id;
        Assert.True(SankeyConstants.IsDummyNode(dummyNodeId));

        // Now pass the routed graph directly to SankeyDecompositionEngine
        using var workspace = new SankeyWorkspace();
        var nodeOutput = new SankeyNodeValue[routingResult.NodeCount];
        var edgeOutput = new SankeyEdgeValue[routingResult.EdgeCount];

        SankeyDecompositionEngine.Analyze(
            routedNodes.AsSpan(0, routingResult.NodeCount),
            routedEdges.AsSpan(0, routingResult.EdgeCount),
            TestMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var decompResult);

        // This previously failed with NonAdjacentLayer! Now it succeeds!
        Assert.Equal(SankeyStatus.Success, decompResult.Status);
        Assert.Equal(SankeyError.None, decompResult.Error);
        Assert.Equal(4, decompResult.NodeCount);
        Assert.Equal(4, decompResult.EdgeCount);
        Assert.Equal(3, decompResult.LayerCount); // Layer 0, Layer 1, Layer 2

        // Verify flow conservation at the dummy node in Layer 1
        var dummyNodeValue = Array.Find(nodeOutput, n => n.Id == dummyNodeId);
        Assert.Equal(1, dummyNodeValue.Layer);
        Assert.Equal(40m, dummyNodeValue.Incoming);
        Assert.Equal(40m, dummyNodeValue.Outgoing);
        Assert.Equal(40m, dummyNodeValue.Capacity);
        Assert.Equal(0m, dummyNodeValue.Balance);

        // Verify original edge ID mapping
        for (int i = 0; i < decompResult.EdgeCount; i++)
        {
            var edge = edgeOutput[i];
            if (SankeyConstants.IsDummyEdge(edge.Id))
            {
                Assert.Equal(30, SankeyConstants.GetOriginalEdgeId(edge.Id));
            }
        }
    }

    [Fact]
    public void MultiHopSpan_RoutesMultipleDummyNodesAcrossLayers()
    {
        // 4 layers: 1 -> 2 -> 3 -> 4, with skip-link 1 -> 4 [50m]
        var nodes = new[]
        {
            new SankeyNode(1),
            new SankeyNode(2),
            new SankeyNode(3),
            new SankeyNode(4)
        };
        var edges = new[]
        {
            new SankeyEdge(10, 1, 2, 50m),
            new SankeyEdge(20, 2, 3, 50m),
            new SankeyEdge(30, 3, 4, 50m),
            new SankeyEdge(40, 1, 4, 50m) // Spans 3 layers: Layer 0 to Layer 3
        };

        var routedNodes = new SankeyNode[16];
        var routedEdges = new SankeyEdge[16];

        SankeySkipLinkRouter.Route(nodes, edges, routedNodes, routedEdges, out var result);

        Assert.Equal(SankeyRoutingStatus.Success, result.Status);
        Assert.Equal(2, result.DummyNodeCount); // Intermediate dummy nodes at Layer 1 and Layer 2
        Assert.Equal(3, result.SegmentedEdgeCount); // 3 hops: 1->D1, D1->D2, D2->4

        using var workspace = new SankeyWorkspace();
        var nodeOutput = new SankeyNodeValue[result.NodeCount];
        var edgeOutput = new SankeyEdgeValue[result.EdgeCount];

        SankeyDecompositionEngine.Analyze(
            routedNodes.AsSpan(0, result.NodeCount),
            routedEdges.AsSpan(0, result.EdgeCount),
            TestMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var decompResult);

        Assert.Equal(SankeyStatus.Success, decompResult.Status);
        Assert.Equal(4, decompResult.LayerCount);
    }

    [Fact]
    public void BufferTooSmall_ReturnsBufferTooSmallStatus()
    {
        var nodes = new[]
        {
            new SankeyNode(1),
            new SankeyNode(2),
            new SankeyNode(3)
        };
        var edges = new[]
        {
            new SankeyEdge(10, 1, 2, 60m),
            new SankeyEdge(20, 2, 3, 60m),
            new SankeyEdge(30, 1, 3, 40m)
        };

        // Buffer sized only for original nodes (needs 4 nodes)
        var routedNodes = new SankeyNode[3];
        var routedEdges = new SankeyEdge[3];

        SankeySkipLinkRouter.Route(nodes, edges, routedNodes, routedEdges, out var result);

        Assert.Equal(SankeyRoutingStatus.BufferTooSmall, result.Status);
    }
}
