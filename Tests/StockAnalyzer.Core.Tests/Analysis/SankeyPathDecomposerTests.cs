using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis.Sankey;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class SankeyPathDecomposerTests
{
    [Fact]
    public void EmptyEdges_ReturnsSuccessWithZeroPaths()
    {
        var headers = new SankeyPathHeader[10];
        var edgeIds = new int[10];

        SankeyPathDecomposer.Decompose(
            ReadOnlySpan<SankeyNodeValue>.Empty,
            ReadOnlySpan<SankeyEdgeValue>.Empty,
            headers,
            edgeIds,
            out var result);

        Assert.Equal(SankeyPathDecompositionStatus.Success, result.Status);
        Assert.Equal(0, result.PathCount);
        Assert.Equal(0, result.TotalEdgeReferencesCount);
    }

    [Fact]
    public void T05_UnbalancedInterior_ReturnsUnbalancedInteriorStatus()
    {
        // Node 2 has Incoming 100 != Outgoing 60 (Balance = 40)
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 60m, 100m, 40m, 1, 0),
            new SankeyNodeValue(3, 60m, 0m, 60m, 60m, 2, 0)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(1, 1, 2, 100m),
            new SankeyEdgeValue(2, 2, 3, 60m)
        };

        var headers = new SankeyPathHeader[2];
        var edgeIds = new int[4];

        SankeyPathDecomposer.Decompose(nodes, edges, headers, edgeIds, out var result);

        Assert.Equal(SankeyPathDecompositionStatus.UnbalancedInterior, result.Status);
        Assert.Equal(0, result.PathCount);
    }

    [Fact]
    public void T06_FullyConservedChain_DecomposesIntoSinglePath()
    {
        // 1 -> 2 (100m) -> 3 (100m)
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 100m, 100m, 0m, 1, 0),
            new SankeyNodeValue(3, 100m, 0m, 100m, 100m, 2, 0)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(10, 1, 2, 100m),
            new SankeyEdgeValue(20, 2, 3, 100m)
        };

        var headers = new SankeyPathHeader[2];
        var edgeIds = new int[4];

        SankeyPathDecomposer.Decompose(nodes, edges, headers, edgeIds, out var result);

        Assert.Equal(SankeyPathDecompositionStatus.Success, result.Status);
        Assert.Equal(1, result.PathCount);
        Assert.Equal(2, result.TotalEdgeReferencesCount);

        var path = headers[0];
        Assert.Equal(0, path.StartOffset);
        Assert.Equal(2, path.EdgeCount);
        Assert.Equal(100m, path.Weight);

        Assert.Equal(10, edgeIds[0]);
        Assert.Equal(20, edgeIds[1]);
    }

    [Fact]
    public void ComplexConservedNetwork_ExactRecompositionOracle()
    {
        // Sources: 1 (Outgoing 100), 2 (Outgoing 50)
        // Transfers: 3 (In 50, Out 50), 4 (In 100, Out 100)
        // Sinks: 5 (Incoming 70), 6 (Incoming 80)
        // Total Flow = 150m
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 0m, 50m, 50m, -50m, 0, 1),
            new SankeyNodeValue(3, 50m, 50m, 50m, 0m, 1, 0),
            new SankeyNodeValue(4, 100m, 100m, 100m, 0m, 1, 1),
            new SankeyNodeValue(5, 70m, 0m, 70m, 70m, 2, 0),
            new SankeyNodeValue(6, 80m, 0m, 80m, 80m, 2, 1)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(1, 1, 3, 30m),
            new SankeyEdgeValue(2, 1, 4, 70m),
            new SankeyEdgeValue(3, 2, 3, 20m),
            new SankeyEdgeValue(4, 2, 4, 30m),
            new SankeyEdgeValue(5, 3, 5, 50m),
            new SankeyEdgeValue(6, 4, 5, 20m),
            new SankeyEdgeValue(7, 4, 6, 80m)
        };

        var headers = new SankeyPathHeader[edges.Length];
        var edgeIds = new int[edges.Length * nodes.Length];

        SankeyPathDecomposer.Decompose(nodes, edges, headers, edgeIds, out var result);

        Assert.Equal(SankeyPathDecompositionStatus.Success, result.Status);
        Assert.True(result.PathCount > 0);

        // Recomposition Oracle: For every edge e, the sum of path weights containing e equals edge.Weight
        var edgeWeightSums = new Dictionary<int, decimal>();
        for (int i = 0; i < edges.Length; i++)
        {
            edgeWeightSums[edges[i].Id] = 0m;
        }

        for (int p = 0; p < result.PathCount; p++)
        {
            var header = headers[p];
            for (int e = 0; e < header.EdgeCount; e++)
            {
                int edgeId = edgeIds[header.StartOffset + e];
                edgeWeightSums[edgeId] += header.Weight;
            }
        }

        for (int i = 0; i < edges.Length; i++)
        {
            decimal reconstructed = edgeWeightSums[edges[i].Id];
            Assert.Equal(edges[i].Weight, reconstructed);
        }
    }

    [Fact]
    public void BufferTooSmall_ReturnsBufferTooSmallStatus()
    {
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 0m, 100m, 100m, 1, 0)
        };
        var edges = new[] { new SankeyEdgeValue(1, 1, 2, 100m) };

        // Zero-length buffer
        SankeyPathDecomposer.Decompose(
            nodes,
            edges,
            Span<SankeyPathHeader>.Empty,
            Span<int>.Empty,
            out var result);

        Assert.Equal(SankeyPathDecompositionStatus.BufferTooSmall, result.Status);
    }

    [Fact]
    public void Constants_MaxPathCountAndEdgeReferences_AreConsistentWithLimits()
    {
        Assert.Equal(SankeyConstants.MaxEdges, SankeyConstants.MaxPathCount);
        Assert.Equal(SankeyConstants.MaxEdges * (SankeyConstants.MaxNodes - 1), SankeyConstants.MaxPathEdgeReferences);
        Assert.Equal(0.0001, SankeyConstants.GeometryToleranceDip);
    }
}
