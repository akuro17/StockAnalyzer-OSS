using System;
using System.Buffers;

namespace StockAnalyzer.Core.Analysis.Sankey;

/// <summary>
/// Status outcome of the skip-link dummy-node routing engine (Extension E06).
/// </summary>
public enum SankeyRoutingStatus
{
    Success,
    NoSkipLinks,
    BufferTooSmall,
    Cycle,
    InvalidGraph
}

/// <summary>
/// Overall result of a Sankey skip-link routing run (Extension E06).
/// </summary>
public readonly record struct SankeyRoutingResult(
    SankeyRoutingStatus Status,
    int NodeCount,
    int EdgeCount,
    int DummyNodeCount,
    int SegmentedEdgeCount);

/// <summary>
/// Subdivides non-adjacent layer edges (skip-links) into unit-hop segments through virtual dummy nodes (Extension E06).
/// Ensures that arbitrary DAGs can be decomposed and rendered without ribbon-node collisions.
/// Guarantees zero heap allocation on the hot path via caller-provided output buffers and ArrayPool scratchpads.
/// </summary>
public static class SankeySkipLinkRouter
{
    /// <summary>
    /// Routes long edges across intermediate layers by inserting zero-width dummy nodes.
    /// </summary>
    public static void Route(
        ReadOnlySpan<SankeyNode> nodes,
        ReadOnlySpan<SankeyEdge> edges,
        Span<SankeyNode> routedNodesOutput,
        Span<SankeyEdge> routedEdgesOutput,
        out SankeyRoutingResult result)
    {
        if (nodes.Length > SankeyConstants.MaxNodes || edges.Length > SankeyConstants.MaxEdges)
        {
            result = new SankeyRoutingResult(SankeyRoutingStatus.InvalidGraph, 0, 0, 0, 0);
            return;
        }

        // Empty graph handling
        if (nodes.Length == 0 && edges.Length == 0)
        {
            result = new SankeyRoutingResult(SankeyRoutingStatus.NoSkipLinks, 0, 0, 0, 0);
            return;
        }

        if (routedNodesOutput.Length < nodes.Length || routedEdgesOutput.Length < edges.Length)
        {
            result = new SankeyRoutingResult(SankeyRoutingStatus.BufferTooSmall, 0, 0, 0, 0);
            return;
        }

        int nodeCount = nodes.Length;
        int edgeCount = edges.Length;

        // Scratchpad memory
        int[] nodeIds = ArrayPool<int>.Shared.Rent(nodeCount);
        int[] layers = ArrayPool<int>.Shared.Rent(nodeCount);
        int[] inDegrees = ArrayPool<int>.Shared.Rent(nodeCount);
        int[] minHeap = ArrayPool<int>.Shared.Rent(nodeCount);
        int[] headEdge = ArrayPool<int>.Shared.Rent(nodeCount);
        int[] nextEdge = ArrayPool<int>.Shared.Rent(edgeCount);
        int[] edgeSrc = ArrayPool<int>.Shared.Rent(edgeCount);
        int[] edgeTgt = ArrayPool<int>.Shared.Rent(edgeCount);

        try
        {
            // 1. Validate node IDs
            for (int i = 0; i < nodeCount; i++)
            {
                if (nodes[i].Id < 0 || nodes[i].Id >= SankeyConstants.DummyNodeIdBase)
                {
                    result = new SankeyRoutingResult(SankeyRoutingStatus.InvalidGraph, 0, 0, 0, 0);
                    return;
                }
                nodeIds[i] = nodes[i].Id;
            }

            // Check duplicate node IDs
            for (int i = 0; i < nodeCount; i++)
            {
                for (int j = i + 1; j < nodeCount; j++)
                {
                    if (nodeIds[i] == nodeIds[j])
                    {
                        result = new SankeyRoutingResult(SankeyRoutingStatus.InvalidGraph, 0, 0, 0, 0);
                        return;
                    }
                }
            }

            // 2. Map edges and initialize graph
            for (int i = 0; i < nodeCount; i++)
            {
                inDegrees[i] = 0;
                layers[i] = 0;
                headEdge[i] = -1;
            }

            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
                if (edge.Id < 0 || edge.Id >= SankeyConstants.DummyEdgeIdBase || edge.Weight < 0m)
                {
                    result = new SankeyRoutingResult(SankeyRoutingStatus.InvalidGraph, 0, 0, 0, 0);
                    return;
                }

                if (edge.SourceId == edge.TargetId)
                {
                    result = new SankeyRoutingResult(SankeyRoutingStatus.InvalidGraph, 0, 0, 0, 0);
                    return;
                }

                int srcIdx = FindNodeIndex(nodes, edge.SourceId);
                int tgtIdx = FindNodeIndex(nodes, edge.TargetId);
                if (srcIdx < 0 || tgtIdx < 0)
                {
                    result = new SankeyRoutingResult(SankeyRoutingStatus.InvalidGraph, 0, 0, 0, 0);
                    return;
                }

                edgeSrc[i] = srcIdx;
                edgeTgt[i] = tgtIdx;

                if (edge.Weight > 0m)
                {
                    inDegrees[tgtIdx]++;
                    nextEdge[i] = headEdge[srcIdx];
                    headEdge[srcIdx] = i;
                }
                else
                {
                    nextEdge[i] = -1;
                }
            }

            // 3. Topological layering (Kahn's MinHeap algorithm)
            int heapSize = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                if (inDegrees[i] == 0)
                {
                    MinHeapPush(minHeap, ref heapSize, i, nodeIds);
                }
            }

            int poppedCount = 0;
            while (heapSize > 0)
            {
                int uIdx = MinHeapPop(minHeap, ref heapSize, nodeIds);
                poppedCount++;

                int eIdx = headEdge[uIdx];
                while (eIdx != -1)
                {
                    int vIdx = edgeTgt[eIdx];
                    if (layers[uIdx] + 1 > layers[vIdx])
                    {
                        layers[vIdx] = layers[uIdx] + 1;
                    }

                    inDegrees[vIdx]--;
                    if (inDegrees[vIdx] == 0)
                    {
                        MinHeapPush(minHeap, ref heapSize, vIdx, nodeIds);
                    }

                    eIdx = nextEdge[eIdx];
                }
            }

            if (poppedCount < nodeCount)
            {
                result = new SankeyRoutingResult(SankeyRoutingStatus.Cycle, 0, 0, 0, 0);
                return;
            }

            // 4. Count required dummy nodes and segmented edges
            int dummyNodeCount = 0;
            int segmentedEdgeCount = 0;
            int unitEdgeCount = 0;

            for (int i = 0; i < edgeCount; i++)
            {
                if (edges[i].Weight <= 0m)
                {
                    unitEdgeCount++;
                    continue;
                }

                int span = layers[edgeTgt[i]] - layers[edgeSrc[i]];
                if (span <= 1)
                {
                    unitEdgeCount++;
                }
                else
                {
                    dummyNodeCount += span - 1;
                    segmentedEdgeCount += span;
                }
            }

            // If no skip links exist, simple passthrough
            if (dummyNodeCount == 0)
            {
                nodes.CopyTo(routedNodesOutput);
                edges.CopyTo(routedEdgesOutput);
                result = new SankeyRoutingResult(
                    SankeyRoutingStatus.NoSkipLinks,
                    nodeCount,
                    edgeCount,
                    0,
                    0);
                return;
            }

            int totalRoutedNodes = nodeCount + dummyNodeCount;
            int totalRoutedEdges = unitEdgeCount + segmentedEdgeCount;

            if (routedNodesOutput.Length < totalRoutedNodes || routedEdgesOutput.Length < totalRoutedEdges)
            {
                result = new SankeyRoutingResult(SankeyRoutingStatus.BufferTooSmall, 0, 0, 0, 0);
                return;
            }

            // 5. Generate routed nodes and edges
            nodes.CopyTo(routedNodesOutput.Slice(0, nodeCount));

            int nextDummyNodeId = SankeyConstants.DummyNodeIdBase;
            int outNodeIdx = nodeCount;
            int outEdgeIdx = 0;

            for (int i = 0; i < edgeCount; i++)
            {
                var edge = edges[i];
                if (edge.Weight <= 0m)
                {
                    routedEdgesOutput[outEdgeIdx++] = edge;
                    continue;
                }

                int srcIdx = edgeSrc[i];
                int tgtIdx = edgeTgt[i];
                int span = layers[tgtIdx] - layers[srcIdx];

                if (span <= 1)
                {
                    routedEdgesOutput[outEdgeIdx++] = edge;
                }
                else
                {
                    // Subdivide long edge across intermediate layers
                    int prevNodeId = nodes[srcIdx].Id;
                    int origEdgeId = edge.Id;

                    for (int h = 0; h < span - 1; h++)
                    {
                        int currDummyNodeId = nextDummyNodeId++;
                        routedNodesOutput[outNodeIdx++] = new SankeyNode(currDummyNodeId);

                        int dummyEdgeId = SankeyConstants.DummyEdgeIdBase + (origEdgeId * SankeyConstants.MaxNodes) + h;
                        routedEdgesOutput[outEdgeIdx++] = new SankeyEdge(dummyEdgeId, prevNodeId, currDummyNodeId, edge.Weight);
                        prevNodeId = currDummyNodeId;
                    }

                    int lastEdgeId = SankeyConstants.DummyEdgeIdBase + (origEdgeId * SankeyConstants.MaxNodes) + (span - 1);
                    routedEdgesOutput[outEdgeIdx++] = new SankeyEdge(lastEdgeId, prevNodeId, nodes[tgtIdx].Id, edge.Weight);
                }
            }

            result = new SankeyRoutingResult(
                SankeyRoutingStatus.Success,
                outNodeIdx,
                outEdgeIdx,
                dummyNodeCount,
                segmentedEdgeCount);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(nodeIds);
            ArrayPool<int>.Shared.Return(layers);
            ArrayPool<int>.Shared.Return(inDegrees);
            ArrayPool<int>.Shared.Return(minHeap);
            ArrayPool<int>.Shared.Return(headEdge);
            ArrayPool<int>.Shared.Return(nextEdge);
            ArrayPool<int>.Shared.Return(edgeSrc);
            ArrayPool<int>.Shared.Return(edgeTgt);
        }
    }

    private static int FindNodeIndex(ReadOnlySpan<SankeyNode> nodes, int targetId)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Id == targetId)
                return i;
        }
        return -1;
    }

    private static void MinHeapPush(int[] heap, ref int heapSize, int itemIdx, int[] nodeIds)
    {
        int i = heapSize++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (nodeIds[heap[parent]] <= nodeIds[itemIdx])
                break;
            heap[i] = heap[parent];
            i = parent;
        }
        heap[i] = itemIdx;
    }

    private static int MinHeapPop(int[] heap, ref int heapSize, int[] nodeIds)
    {
        int result = heap[0];
        int lastItem = heap[--heapSize];
        if (heapSize > 0)
        {
            int i = 0;
            while ((i << 1) + 1 < heapSize)
            {
                int left = (i << 1) + 1;
                int right = left + 1;
                int best = (right < heapSize && nodeIds[heap[right]] < nodeIds[heap[left]]) ? right : left;
                if (nodeIds[lastItem] <= nodeIds[heap[best]])
                    break;
                heap[i] = heap[best];
                i = best;
            }
            heap[i] = lastItem;
        }
        return result;
    }
}
