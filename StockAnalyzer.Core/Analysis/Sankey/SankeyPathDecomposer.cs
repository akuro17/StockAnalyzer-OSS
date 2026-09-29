using System;
using System.Buffers;

namespace StockAnalyzer.Core.Analysis.Sankey;

/// <summary>
/// Status outcome of residual flow path decomposition (Extension E01).
/// </summary>
public enum SankeyPathDecompositionStatus
{
    Success,
    UnbalancedInterior,
    BufferTooSmall,
    InvariantViolation,
    InvalidGraph
}

/// <summary>
/// Header describing an individual decomposed source-to-sink path.
/// </summary>
public readonly record struct SankeyPathHeader(
    int StartOffset,
    int EdgeCount,
    decimal Weight);

/// <summary>
/// Overall result of a Sankey path decomposition run.
/// </summary>
public readonly record struct SankeyPathDecompositionResult(
    SankeyPathDecompositionStatus Status,
    int PathCount,
    int TotalEdgeReferencesCount);

/// <summary>
/// Decomposes a flow-conserving DAG into a set of weighted source-to-sink paths (Extension E01).
/// Implements deterministic DFS over positive residual flows.
/// </summary>
public static class SankeyPathDecomposer
{
    /// <summary>
    /// Decomposes verified DAG edge flows into an exact weighted sum of source-to-sink paths.
    /// Guarantees strict bit-level determinism: source nodes are processed in NodeId ascending order,
    /// and outgoing residual branches are traversed in EdgeId ascending order.
    /// Callers should dimension <paramref name="pathHeadersOutput"/> to at least <paramref name="edges"/>.Length
    /// (up to <see cref="SankeyConstants.MaxPathCount"/>) and <paramref name="edgeIdsOutput"/> conservatively
    /// up to <see cref="SankeyConstants.MaxPathEdgeReferences"/>.
    /// </summary>
    public static void Decompose(
        ReadOnlySpan<SankeyNodeValue> nodes,
        ReadOnlySpan<SankeyEdgeValue> edges,
        Span<SankeyPathHeader> pathHeadersOutput,
        Span<int> edgeIdsOutput,
        out SankeyPathDecompositionResult result)
    {
        // 1. Verify interior flow conservation (Incoming == Outgoing for all transfer nodes)
        decimal totalSourceOut = 0m;
        decimal totalSinkIn = 0m;
        for (int i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            bool isTransfer = node.Incoming > 0m && node.Outgoing > 0m;
            if (isTransfer && node.Incoming != node.Outgoing)
            {
                result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.UnbalancedInterior, 0, 0);
                return;
            }

            if (node.Incoming == 0m && node.Outgoing > 0m)
            {
                totalSourceOut += node.Outgoing;
            }
            else if (node.Incoming > 0m && node.Outgoing == 0m)
            {
                totalSinkIn += node.Incoming;
            }
        }

        if (totalSourceOut != totalSinkIn)
        {
            result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.UnbalancedInterior, 0, 0);
            return;
        }

        if (edges.Length == 0)
        {
            result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.Success, 0, 0);
            return;
        }

        if (pathHeadersOutput.Length < edges.Length)
        {
            result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.BufferTooSmall, 0, 0);
            return;
        }

        // 2. Scratchpad memory
        decimal[] residuals = ArrayPool<decimal>.Shared.Rent(edges.Length);
        int[] edgeSrcIndices = ArrayPool<int>.Shared.Rent(edges.Length);
        int[] edgeTgtIndices = ArrayPool<int>.Shared.Rent(edges.Length);
        int[] pathEdgeIndices = ArrayPool<int>.Shared.Rent(nodes.Length);
        int[] sourceNodeIndices = ArrayPool<int>.Shared.Rent(nodes.Length);

        try
        {
            // Map node IDs to indices
            for (int i = 0; i < edges.Length; i++)
            {
                residuals[i] = edges[i].Weight;
                int srcIdx = FindNodeIndex(nodes, edges[i].SourceId);
                int tgtIdx = FindNodeIndex(nodes, edges[i].TargetId);
                if (srcIdx < 0 || tgtIdx < 0 || edges[i].Weight <= 0m)
                {
                    result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.InvalidGraph, 0, 0);
                    return;
                }
                edgeSrcIndices[i] = srcIdx;
                edgeTgtIndices[i] = tgtIdx;
            }

            // Identify and sort source nodes by NodeId ascending
            int sourceCount = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Incoming == 0m && nodes[i].Outgoing > 0m)
                {
                    sourceNodeIndices[sourceCount++] = i;
                }
            }

            QuickSortSourceNodes(sourceNodeIndices, nodes, 0, sourceCount - 1);

            int pathCount = 0;
            int totalEdgeRefs = 0;

            // 3. Iterative DFS path extraction
            while (true)
            {
                // Check if any positive residual flow remains
                bool hasResidual = false;
                for (int i = 0; i < edges.Length; i++)
                {
                    if (residuals[i] > 0m)
                    {
                        hasResidual = true;
                        break;
                    }
                }

                if (!hasResidual)
                {
                    break; // All edge flow fully decomposed!
                }

                // Find lowest NodeId source node with available residual outgoing flow
                int activeSourceIdx = -1;
                for (int s = 0; s < sourceCount; s++)
                {
                    int sNodeIdx = sourceNodeIndices[s];
                    if (HasOutgoingResidual(sNodeIdx, edges.Length, edgeSrcIndices, residuals))
                    {
                        activeSourceIdx = sNodeIdx;
                        break;
                    }
                }

                if (activeSourceIdx < 0)
                {
                    // Positive flow exists but cannot be routed from any source
                    result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.InvariantViolation, 0, 0);
                    return;
                }

                // Trace forward DFS from activeSourceIdx to sink
                int currentNodeIdx = activeSourceIdx;
                int pathLen = 0;

                while (nodes[currentNodeIdx].Outgoing > 0m)
                {
                    if (pathLen >= nodes.Length)
                    {
                        result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.InvariantViolation, 0, 0);
                        return;
                    }

                    // Choose outgoing edge with residuals > 0 and lowest EdgeId
                    int bestEdgeIdx = -1;
                    int bestEdgeId = int.MaxValue;
                    for (int e = 0; e < edges.Length; e++)
                    {
                        if (edgeSrcIndices[e] == currentNodeIdx && residuals[e] > 0m)
                        {
                            if (edges[e].Id < bestEdgeId)
                            {
                                bestEdgeId = edges[e].Id;
                                bestEdgeIdx = e;
                            }
                        }
                    }

                    if (bestEdgeIdx < 0)
                    {
                        result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.InvariantViolation, 0, 0);
                        return;
                    }

                    pathEdgeIndices[pathLen++] = bestEdgeIdx;
                    currentNodeIdx = edgeTgtIndices[bestEdgeIdx];
                }

                // Sink reached: calculate bottleneck residual capacity
                decimal minResidual = decimal.MaxValue;
                for (int p = 0; p < pathLen; p++)
                {
                    decimal r = residuals[pathEdgeIndices[p]];
                    if (r < minResidual)
                    {
                        minResidual = r;
                    }
                }

                if (minResidual <= 0m)
                {
                    result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.InvariantViolation, 0, 0);
                    return;
                }

                // Subtract bottleneck residual along path
                for (int p = 0; p < pathLen; p++)
                {
                    residuals[pathEdgeIndices[p]] -= minResidual;
                }

                // Check buffer capacity for edge references
                if (totalEdgeRefs + pathLen > edgeIdsOutput.Length)
                {
                    result = new SankeyPathDecompositionResult(SankeyPathDecompositionStatus.BufferTooSmall, 0, 0);
                    return;
                }

                pathHeadersOutput[pathCount] = new SankeyPathHeader(totalEdgeRefs, pathLen, minResidual);
                for (int p = 0; p < pathLen; p++)
                {
                    edgeIdsOutput[totalEdgeRefs + p] = edges[pathEdgeIndices[p]].Id;
                }

                totalEdgeRefs += pathLen;
                pathCount++;
            }

            result = new SankeyPathDecompositionResult(
                SankeyPathDecompositionStatus.Success,
                pathCount,
                totalEdgeRefs);
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(residuals);
            ArrayPool<int>.Shared.Return(edgeSrcIndices);
            ArrayPool<int>.Shared.Return(edgeTgtIndices);
            ArrayPool<int>.Shared.Return(pathEdgeIndices);
            ArrayPool<int>.Shared.Return(sourceNodeIndices);
        }
    }

    private static bool HasOutgoingResidual(
        int nodeIdx,
        int edgeCount,
        int[] edgeSrcIndices,
        decimal[] residuals)
    {
        for (int i = 0; i < edgeCount; i++)
        {
            if (edgeSrcIndices[i] == nodeIdx && residuals[i] > 0m)
            {
                return true;
            }
        }
        return false;
    }

    private static int FindNodeIndex(ReadOnlySpan<SankeyNodeValue> nodes, int targetId)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Id == targetId)
                return i;
        }
        return -1;
    }

    private static void QuickSortSourceNodes(int[] indices, ReadOnlySpan<SankeyNodeValue> nodes, int left, int right)
    {
        if (left >= right) return;
        int pivotId = nodes[indices[left + ((right - left) >> 1)]].Id;
        int i = left;
        int j = right;
        while (i <= j)
        {
            while (nodes[indices[i]].Id < pivotId) i++;
            while (nodes[indices[j]].Id > pivotId) j--;
            if (i <= j)
            {
                (indices[i], indices[j]) = (indices[j], indices[i]);
                i++;
                j--;
            }
        }
        if (left < j) QuickSortSourceNodes(indices, nodes, left, j);
        if (i < right) QuickSortSourceNodes(indices, nodes, i, right);
    }
}
