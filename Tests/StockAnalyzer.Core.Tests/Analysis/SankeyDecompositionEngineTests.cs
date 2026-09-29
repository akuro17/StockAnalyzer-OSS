using System;
using StockAnalyzer.Core.Analysis.Sankey;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class SankeyDecompositionEngineTests
{
    private static readonly SankeyMeasure ValidMeasure = new(
        SankeyMeasureKind.Volume,
        "shares",
        SankeyProvenance.Observed,
        "daily_volume_v1");

    [Fact]
    public void T01_EmptyGraph_ReturnsEmpty()
    {
        using var workspace = new SankeyWorkspace();
        var nodeOutput = new SankeyNodeValue[10];
        var edgeOutput = new SankeyEdgeValue[10];

        SankeyDecompositionEngine.Analyze(
            ReadOnlySpan<SankeyNode>.Empty,
            ReadOnlySpan<SankeyEdge>.Empty,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Empty, result.Status);
        Assert.Equal(0, result.NodeCount);
        Assert.Equal(0, result.EdgeCount);
        Assert.Equal(0, result.LayerCount);
        Assert.Equal(SankeyError.None, result.Error);
        Assert.Equal(-1, result.ErrorId);
    }

    [Fact]
    public void T02_SingleNode_NoEdges_ReturnsSuccess()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(10) };
        var nodeOutput = new SankeyNodeValue[1];
        var edgeOutput = new SankeyEdgeValue[1];

        SankeyDecompositionEngine.Analyze(
            nodes,
            ReadOnlySpan<SankeyEdge>.Empty,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Success, result.Status);
        Assert.Equal(1, result.NodeCount);
        Assert.Equal(0, result.EdgeCount);
        Assert.Equal(1, result.LayerCount);
        Assert.Equal(SankeyError.None, result.Error);

        Assert.Equal(10, nodeOutput[0].Id);
        Assert.Equal(0m, nodeOutput[0].Incoming);
        Assert.Equal(0m, nodeOutput[0].Outgoing);
        Assert.Equal(0m, nodeOutput[0].Capacity);
        Assert.Equal(0m, nodeOutput[0].Balance);
        Assert.Equal(0, nodeOutput[0].Layer);
        Assert.Equal(0, nodeOutput[0].Order);
    }

    [Fact]
    public void T03_TwoNodes_SingleEdge_ComputesCorrectMetricsAndLayers()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2) };
        var edges = new[] { new SankeyEdge(100, 1, 2, 100m) };
        var nodeOutput = new SankeyNodeValue[2];
        var edgeOutput = new SankeyEdgeValue[1];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Success, result.Status);
        Assert.Equal(2, result.NodeCount);
        Assert.Equal(1, result.EdgeCount);
        Assert.Equal(2, result.LayerCount);

        // Node 1 (Source)
        Assert.Equal(1, nodeOutput[0].Id);
        Assert.Equal(0m, nodeOutput[0].Incoming);
        Assert.Equal(100m, nodeOutput[0].Outgoing);
        Assert.Equal(100m, nodeOutput[0].Capacity);
        Assert.Equal(-100m, nodeOutput[0].Balance);
        Assert.Equal(0, nodeOutput[0].Layer);
        Assert.Equal(0, nodeOutput[0].Order);

        // Node 2 (Target)
        Assert.Equal(2, nodeOutput[1].Id);
        Assert.Equal(100m, nodeOutput[1].Incoming);
        Assert.Equal(0m, nodeOutput[1].Outgoing);
        Assert.Equal(100m, nodeOutput[1].Capacity);
        Assert.Equal(100m, nodeOutput[1].Balance);
        Assert.Equal(1, nodeOutput[1].Layer);
        Assert.Equal(0, nodeOutput[1].Order);

        // Edge output
        Assert.Equal(100, edgeOutput[0].Id);
        Assert.Equal(1, edgeOutput[0].SourceId);
        Assert.Equal(2, edgeOutput[0].TargetId);
        Assert.Equal(100m, edgeOutput[0].Weight);
    }

    [Fact]
    public void T04_BranchingFlow_CalculatesCapacities()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2), new SankeyNode(3) };
        var edges = new[]
        {
            new SankeyEdge(10, 1, 2, 60m),
            new SankeyEdge(20, 1, 3, 40m)
        };
        var nodeOutput = new SankeyNodeValue[3];
        var edgeOutput = new SankeyEdgeValue[2];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Success, result.Status);
        Assert.Equal(3, result.NodeCount);
        Assert.Equal(2, result.EdgeCount);
        Assert.Equal(2, result.LayerCount);

        // Node 1: Layer 0, Capacity 100
        Assert.Equal(1, nodeOutput[0].Id);
        Assert.Equal(100m, nodeOutput[0].Capacity);
        Assert.Equal(-100m, nodeOutput[0].Balance);
        Assert.Equal(0, nodeOutput[0].Layer);

        // Node 2: Layer 1, Capacity 60, Order 0
        Assert.Equal(2, nodeOutput[1].Id);
        Assert.Equal(60m, nodeOutput[1].Capacity);
        Assert.Equal(1, nodeOutput[1].Layer);
        Assert.Equal(0, nodeOutput[1].Order);

        // Node 3: Layer 1, Capacity 40, Order 1
        Assert.Equal(3, nodeOutput[2].Id);
        Assert.Equal(40m, nodeOutput[2].Capacity);
        Assert.Equal(1, nodeOutput[2].Layer);
        Assert.Equal(1, nodeOutput[2].Order);
    }

    [Fact]
    public void T05_IntermediateTransfer_WithUnbalancedFlow()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2), new SankeyNode(3) };
        var edges = new[]
        {
            new SankeyEdge(1, 1, 2, 100m),
            new SankeyEdge(2, 2, 3, 60m)
        };
        var nodeOutput = new SankeyNodeValue[3];
        var edgeOutput = new SankeyEdgeValue[2];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Success, result.Status);
        Assert.Equal(3, result.LayerCount);

        // Node 2 (Intermediate)
        var node2 = nodeOutput[1];
        Assert.Equal(2, node2.Id);
        Assert.Equal(100m, node2.Incoming);
        Assert.Equal(60m, node2.Outgoing);
        Assert.Equal(100m, node2.Capacity);
        Assert.Equal(40m, node2.Balance);
        Assert.Equal(1, node2.Layer);
    }

    [Fact]
    public void T06_IntermediateTransfer_FullyConserved()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2), new SankeyNode(3) };
        var edges = new[]
        {
            new SankeyEdge(1, 1, 2, 100m),
            new SankeyEdge(2, 2, 3, 100m)
        };
        var nodeOutput = new SankeyNodeValue[3];
        var edgeOutput = new SankeyEdgeValue[2];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Success, result.Status);
        var node2 = nodeOutput[1];
        Assert.Equal(2, node2.Id);
        Assert.Equal(100m, node2.Capacity);
        Assert.Equal(0m, node2.Balance);
    }

    [Fact]
    public void T07_ZeroEdges_TreatedAsIsolatedNodes()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2) };
        var edges = new[]
        {
            new SankeyEdge(10, 1, 2, 0m),
            new SankeyEdge(20, 2, 1, 0m)
        };
        var nodeOutput = new SankeyNodeValue[2];
        var edgeOutput = new SankeyEdgeValue[2];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Success, result.Status);
        Assert.Equal(0, result.EdgeCount);
        Assert.Equal(1, result.LayerCount);
        Assert.Equal(0m, nodeOutput[0].Capacity);
        Assert.Equal(0m, nodeOutput[1].Capacity);
    }

    [Fact]
    public void T08_SelfLoop_ReturnsSelfLoopError()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1) };
        var edges = new[] { new SankeyEdge(55, 1, 1, 0m) };
        var nodeOutput = new SankeyNodeValue[1];
        var edgeOutput = new SankeyEdgeValue[1];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Invalid, result.Status);
        Assert.Equal(SankeyError.SelfLoop, result.Error);
        Assert.Equal(55, result.ErrorId);
    }

    [Fact]
    public void T09_Cycle_ReturnsCycleError()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2) };
        var edges = new[]
        {
            new SankeyEdge(1, 1, 2, 10m),
            new SankeyEdge(2, 2, 1, 10m)
        };
        var nodeOutput = new SankeyNodeValue[2];
        var edgeOutput = new SankeyEdgeValue[2];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeOutput,
            edgeOutput,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Invalid, result.Status);
        Assert.Equal(SankeyError.Cycle, result.Error);
        Assert.Equal(-1, result.ErrorId);
    }

    [Fact]
    public void T10_ValidationErrors_FollowPrecedence()
    {
        using var workspace = new SankeyWorkspace();
        var nodeBuf = new SankeyNodeValue[10];
        var edgeBuf = new SankeyEdgeValue[10];

        // 1. Negative NodeId
        SankeyDecompositionEngine.Analyze(
            new[] { new SankeyNode(-5) },
            ReadOnlySpan<SankeyEdge>.Empty,
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r1);
        Assert.Equal(SankeyError.InvalidNodeId, r1.Error);
        Assert.Equal(-5, r1.ErrorId);

        // 2. Duplicate NodeId
        SankeyDecompositionEngine.Analyze(
            new[] { new SankeyNode(2), new SankeyNode(2) },
            ReadOnlySpan<SankeyEdge>.Empty,
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r2);
        Assert.Equal(SankeyError.DuplicateNodeId, r2.Error);
        Assert.Equal(2, r2.ErrorId);

        // 3. Negative EdgeId
        SankeyDecompositionEngine.Analyze(
            new[] { new SankeyNode(1), new SankeyNode(2) },
            new[] { new SankeyEdge(-9, 1, 2, 10m) },
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r3);
        Assert.Equal(SankeyError.InvalidEdgeId, r3.Error);
        Assert.Equal(-9, r3.ErrorId);

        // 4. Duplicate EdgeId
        SankeyDecompositionEngine.Analyze(
            new[] { new SankeyNode(1), new SankeyNode(2) },
            new[] { new SankeyEdge(5, 1, 2, 10m), new SankeyEdge(5, 1, 2, 20m) },
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r4);
        Assert.Equal(SankeyError.DuplicateEdgeId, r4.Error);
        Assert.Equal(5, r4.ErrorId);

        // 5. Missing Source
        SankeyDecompositionEngine.Analyze(
            new[] { new SankeyNode(2) },
            new[] { new SankeyEdge(1, 999, 2, 10m) },
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r5);
        Assert.Equal(SankeyError.MissingSource, r5.Error);
        Assert.Equal(1, r5.ErrorId);

        // 6. Missing Target
        SankeyDecompositionEngine.Analyze(
            new[] { new SankeyNode(1) },
            new[] { new SankeyEdge(2, 1, 999, 10m) },
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r6);
        Assert.Equal(SankeyError.MissingTarget, r6.Error);
        Assert.Equal(2, r6.ErrorId);
    }

    [Fact]
    public void T11_WeightPrecisionAndLimits()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2) };
        var nodeBuf = new SankeyNodeValue[2];
        var edgeBuf = new SankeyEdgeValue[1];

        // Negative weight
        SankeyDecompositionEngine.Analyze(
            nodes,
            new[] { new SankeyEdge(1, 1, 2, -1m) },
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r1);
        Assert.Equal(SankeyError.InvalidWeight, r1.Error);

        // Excess weight > 1e12
        SankeyDecompositionEngine.Analyze(
            nodes,
            new[] { new SankeyEdge(1, 1, 2, 1_000_000_000_001m) },
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r2);
        Assert.Equal(SankeyError.InvalidWeight, r2.Error);

        // Quantum violation (finer than 1e-6)
        SankeyDecompositionEngine.Analyze(
            nodes,
            new[] { new SankeyEdge(1, 1, 2, 0.0000001m) },
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r3);
        Assert.Equal(SankeyError.WeightPrecision, r3.Error);
    }

    [Fact]
    public void T12_LimitExceeded_RejectsOversizedGraph()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new SankeyNode[SankeyConstants.MaxNodes + 1];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new SankeyNode(i);
        }

        var nodeBuf = new SankeyNodeValue[nodes.Length];
        var edgeBuf = new SankeyEdgeValue[1];

        SankeyDecompositionEngine.Analyze(
            nodes,
            ReadOnlySpan<SankeyEdge>.Empty,
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var r);

        Assert.Equal(SankeyStatus.Invalid, r.Status);
        Assert.Equal(SankeyError.LimitExceeded, r.Error);
    }

    [Fact]
    public void T13_NonAdjacentLayer_RejectsSkipLinkEdges()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2), new SankeyNode(3) };
        var edges = new[]
        {
            new SankeyEdge(1, 1, 2, 10m),
            new SankeyEdge(2, 2, 3, 10m),
            new SankeyEdge(3, 1, 3, 10m) // skips Layer 1!
        };

        var nodeBuf = new SankeyNodeValue[3];
        var edgeBuf = new SankeyEdgeValue[3];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Invalid, result.Status);
        Assert.Equal(SankeyError.NonAdjacentLayer, result.Error);
        Assert.Equal(3, result.ErrorId); // edge 3 caused non-adjacent layer
    }

    [Fact]
    public void T14_InputPermutation_YieldsDeterministicResult()
    {
        using var workspace = new SankeyWorkspace();

        var nodes1 = new[] { new SankeyNode(1), new SankeyNode(2), new SankeyNode(3) };
        var edges1 = new[] { new SankeyEdge(1, 1, 2, 50m), new SankeyEdge(2, 2, 3, 50m) };

        var nodes2 = new[] { new SankeyNode(3), new SankeyNode(1), new SankeyNode(2) };
        var edges2 = new[] { new SankeyEdge(2, 2, 3, 50m), new SankeyEdge(1, 1, 2, 50m) };

        var outNodes1 = new SankeyNodeValue[3];
        var outEdges1 = new SankeyEdgeValue[2];
        var outNodes2 = new SankeyNodeValue[3];
        var outEdges2 = new SankeyEdgeValue[2];

        SankeyDecompositionEngine.Analyze(nodes1, edges1, ValidMeasure, outNodes1, outEdges1, workspace, out var r1);
        SankeyDecompositionEngine.Analyze(nodes2, edges2, ValidMeasure, outNodes2, outEdges2, workspace, out var r2);

        Assert.Equal(SankeyStatus.Success, r1.Status);
        Assert.Equal(SankeyStatus.Success, r2.Status);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(outNodes1[i], outNodes2[i]);
        }
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(outEdges1[i], outEdges2[i]);
        }
    }

    [Fact]
    public void T17_LargeParallelEdges_ExactDecimalSumWithoutLoss()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2) };
        var edges = new SankeyEdge[4096];
        for (int i = 0; i < 4096; i++)
        {
            edges[i] = new SankeyEdge(i, 1, 2, 1_000_000_000_000m);
        }

        var nodeBuf = new SankeyNodeValue[2];
        var edgeBuf = new SankeyEdgeValue[4096];

        SankeyDecompositionEngine.Analyze(
            nodes,
            edges,
            ValidMeasure,
            nodeBuf,
            edgeBuf,
            workspace,
            out var result);

        Assert.Equal(SankeyStatus.Success, result.Status);
        Assert.Equal(4096, result.EdgeCount);

        decimal expectedTotal = 4_096_000_000_000_000m;
        Assert.Equal(expectedTotal, nodeBuf[0].Outgoing);
        Assert.Equal(expectedTotal, nodeBuf[1].Incoming);
    }

    [Fact]
    public void T24_NullWorkspace_ThrowsArgumentNullException()
    {
        var nodeBuf = new SankeyNodeValue[1];
        var edgeBuf = new SankeyEdgeValue[1];

        Assert.Throws<ArgumentNullException>(() =>
            SankeyDecompositionEngine.Analyze(
                ReadOnlySpan<SankeyNode>.Empty,
                ReadOnlySpan<SankeyEdge>.Empty,
                ValidMeasure,
                nodeBuf,
                edgeBuf,
                null!,
                out _));
    }

    [Fact]
    public void ZeroAllocation_OnHotPath()
    {
        using var workspace = new SankeyWorkspace();
        var nodes = new[] { new SankeyNode(1), new SankeyNode(2), new SankeyNode(3) };
        var edges = new[]
        {
            new SankeyEdge(1, 1, 2, 100m),
            new SankeyEdge(2, 2, 3, 100m)
        };
        var nodeBuf = new SankeyNodeValue[3];
        var edgeBuf = new SankeyEdgeValue[2];

        // Warm up JIT
        for (int i = 0; i < 10; i++)
        {
            SankeyDecompositionEngine.Analyze(nodes, edges, ValidMeasure, nodeBuf, edgeBuf, workspace, out _);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            SankeyDecompositionEngine.Analyze(nodes, edges, ValidMeasure, nodeBuf, edgeBuf, workspace, out _);
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }
}
