using System;

namespace StockAnalyzer.Core.Analysis.Sankey;

/// <summary>
/// Core flow decomposition engine for Sankey diagrams.
/// Implements deterministic topological layering, flow aggregation, and strict validation.
/// </summary>
public static class SankeyDecompositionEngine
{
    /// <summary>
    /// Analyzes the input graph and decomposes it into topological layers and node/edge metrics.
    /// </summary>
    /// <param name="nodes">Set of unique input nodes.</param>
    /// <param name="edges">Set of input directed edges.</param>
    /// <param name="measure">Metadata describing the flow measure, unit, and provenance.</param>
    /// <param name="nodeOutput">Caller-owned buffer to receive decomposed node values.</param>
    /// <param name="edgeOutput">Caller-owned buffer to receive normalized edge values.</param>
    /// <param name="workspace">Caller-owned workspace for scratchpad allocations.</param>
    /// <param name="result">Decomposition run outcome.</param>
    public static void Analyze(
        ReadOnlySpan<SankeyNode> nodes,
        ReadOnlySpan<SankeyEdge> edges,
        in SankeyMeasure measure,
        Span<SankeyNodeValue> nodeOutput,
        Span<SankeyEdgeValue> edgeOutput,
        SankeyWorkspace workspace,
        out SankeyRunResult result)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!workspace.TryEnter(out var wsError))
        {
            result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, wsError, -1);
            return;
        }

        try
        {
            // 1. Graph limits
            if (nodes.Length > SankeyConstants.MaxNodes)
            {
                result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.LimitExceeded, -1);
                return;
            }

            if (edges.Length > SankeyConstants.MaxEdges)
            {
                result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.LimitExceeded, -1);
                return;
            }

            // 2. Measure validation
            if (!ValidateMeasure(in measure))
            {
                result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.InvalidMeasure, -1);
                return;
            }

            // 3. Node ID validation (negative IDs checked in input order)
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Id < 0)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.InvalidNodeId, nodes[i].Id);
                    return;
                }
                workspace.NodeIds[i] = nodes[i].Id;
            }

            // Duplicate node IDs (checked in ascending ID order)
            QuickSortInts(workspace.NodeIds, 0, nodes.Length - 1);
            for (int i = 1; i < nodes.Length; i++)
            {
                if (workspace.NodeIds[i] == workspace.NodeIds[i - 1])
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.DuplicateNodeId, workspace.NodeIds[i]);
                    return;
                }
            }

            // 4. Edge ID validation (negative IDs checked in input order)
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i].Id < 0)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.InvalidEdgeId, edges[i].Id);
                    return;
                }
                workspace.SortedInputEdges[i] = edges[i];
            }

            // Duplicate edge IDs (checked in ascending ID order)
            QuickSortEdgesById(workspace.SortedInputEdges, 0, edges.Length - 1);
            for (int i = 1; i < edges.Length; i++)
            {
                if (workspace.SortedInputEdges[i].Id == workspace.SortedInputEdges[i - 1].Id)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.DuplicateEdgeId, workspace.SortedInputEdges[i].Id);
                    return;
                }
            }

            // 5. Edge reference & weight validation (evaluated in EdgeId ascending order)
            for (int i = 0; i < edges.Length; i++)
            {
                var edge = workspace.SortedInputEdges[i];

                if (BinarySearchNodeId(workspace.NodeIds, nodes.Length, edge.SourceId) < 0)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.MissingSource, edge.Id);
                    return;
                }

                if (BinarySearchNodeId(workspace.NodeIds, nodes.Length, edge.TargetId) < 0)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.MissingTarget, edge.Id);
                    return;
                }

                if (edge.SourceId == edge.TargetId)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.SelfLoop, edge.Id);
                    return;
                }

                if (edge.Weight < 0m || edge.Weight > SankeyConstants.MaxWeight)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.InvalidWeight, edge.Id);
                    return;
                }

                if (edge.Weight % SankeyConstants.WeightQuantum != 0m)
                {
                    result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.WeightPrecision, edge.Id);
                    return;
                }
            }

            // 6. Output buffer size check
            if (nodeOutput.Length < nodes.Length || edgeOutput.Length < edges.Length)
            {
                result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.BufferTooSmall, -1);
                return;
            }

            // 7. Empty graph handling
            if (nodes.Length == 0 && edges.Length == 0)
            {
                result = new SankeyRunResult(SankeyStatus.Empty, 0, 0, 0, SankeyError.None, -1);
                return;
            }

            // 8. Positive edges filtering
            int posEdgeCount = 0;
            for (int i = 0; i < edges.Length; i++)
            {
                var edge = workspace.SortedInputEdges[i];
                if (edge.Weight > 0m)
                {
                    workspace.SortedInputEdges[posEdgeCount++] = edge;
                }
            }

            // All edges zero or no edges
            if (posEdgeCount == 0)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    workspace.StagingNodes[i] = new SankeyNodeValue(
                        workspace.NodeIds[i],
                        0m,
                        0m,
                        0m,
                        0m,
                        0,
                        i);
                }

                workspace.StagingNodes.AsSpan(0, nodes.Length).CopyTo(nodeOutput);
                result = new SankeyRunResult(SankeyStatus.Success, nodes.Length, 0, 1, SankeyError.None, -1);
                return;
            }

            // 9. Aggregation, in-degrees, and adjacency list
            int nodeCount = nodes.Length;
            for (int i = 0; i < nodeCount; i++)
            {
                workspace.Incomings[i] = 0m;
                workspace.Outgoings[i] = 0m;
                workspace.InDegrees[i] = 0;
                workspace.Layers[i] = 0;
                workspace.HeadEdge[i] = -1;
            }

            checked
            {
                for (int i = 0; i < posEdgeCount; i++)
                {
                    var edge = workspace.SortedInputEdges[i];
                    int srcIdx = BinarySearchNodeId(workspace.NodeIds, nodeCount, edge.SourceId);
                    int tgtIdx = BinarySearchNodeId(workspace.NodeIds, nodeCount, edge.TargetId);

                    workspace.Incomings[tgtIdx] += edge.Weight;
                    workspace.Outgoings[srcIdx] += edge.Weight;
                    workspace.InDegrees[tgtIdx]++;

                    workspace.EdgeSourceNodeIndex[i] = srcIdx;
                    workspace.EdgeTargetNodeIndex[i] = tgtIdx;
                    workspace.EdgeWeights[i] = edge.Weight;
                    workspace.EdgeOriginalIds[i] = edge.Id;
                }
            }

            // Build forward adjacency list in EdgeId ascending order (prepend backwards)
            for (int i = posEdgeCount - 1; i >= 0; i--)
            {
                int srcIdx = workspace.EdgeSourceNodeIndex[i];
                workspace.NextEdge[i] = workspace.HeadEdge[srcIdx];
                workspace.HeadEdge[srcIdx] = i;
            }

            // 10. Kahn's algorithm with Min-Heap ordered by NodeId
            int heapSize = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                if (workspace.InDegrees[i] == 0)
                {
                    MinHeapPush(workspace.MinHeap, ref heapSize, i, workspace.NodeIds);
                }
            }

            int poppedCount = 0;
            while (heapSize > 0)
            {
                int uIdx = MinHeapPop(workspace.MinHeap, ref heapSize, workspace.NodeIds);
                poppedCount++;

                int edgeIdx = workspace.HeadEdge[uIdx];
                while (edgeIdx != -1)
                {
                    int vIdx = workspace.EdgeTargetNodeIndex[edgeIdx];
                    if (workspace.Layers[uIdx] + 1 > workspace.Layers[vIdx])
                    {
                        workspace.Layers[vIdx] = workspace.Layers[uIdx] + 1;
                    }

                    workspace.InDegrees[vIdx]--;
                    if (workspace.InDegrees[vIdx] == 0)
                    {
                        MinHeapPush(workspace.MinHeap, ref heapSize, vIdx, workspace.NodeIds);
                    }

                    edgeIdx = workspace.NextEdge[edgeIdx];
                }
            }

            if (poppedCount < nodeCount)
            {
                result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.Cycle, -1);
                return;
            }

            // 11. Adjacent Layer constraint (checked in EdgeId ascending order)
            for (int i = 0; i < posEdgeCount; i++)
            {
                int srcIdx = workspace.EdgeSourceNodeIndex[i];
                int tgtIdx = workspace.EdgeTargetNodeIndex[i];

                if (workspace.Layers[tgtIdx] != workspace.Layers[srcIdx] + 1)
                {
                    result = new SankeyRunResult(
                        SankeyStatus.Invalid,
                        0,
                        0,
                        0,
                        SankeyError.NonAdjacentLayer,
                        workspace.EdgeOriginalIds[i]);
                    return;
                }
            }

            // 12. Determine Layer count and sort nodes by (Layer, NodeId)
            int maxLayer = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                workspace.NodeSortIndices[i] = i;
                if (workspace.Layers[i] > maxLayer)
                {
                    maxLayer = workspace.Layers[i];
                }
            }
            int layerCount = maxLayer + 1;

            QuickSortNodeIndices(workspace.NodeSortIndices, workspace.Layers, workspace.NodeIds, 0, nodeCount - 1);

            // 13. Publish staging nodes
            int currentLayer = -1;
            int currentOrder = 0;
            checked
            {
                for (int i = 0; i < nodeCount; i++)
                {
                    int nodeIdx = workspace.NodeSortIndices[i];
                    int layer = workspace.Layers[nodeIdx];
                    if (layer != currentLayer)
                    {
                        currentLayer = layer;
                        currentOrder = 0;
                    }
                    else
                    {
                        currentOrder++;
                    }

                    workspace.Orders[nodeIdx] = currentOrder;

                    decimal incoming = workspace.Incomings[nodeIdx];
                    decimal outgoing = workspace.Outgoings[nodeIdx];
                    decimal capacity = Math.Max(incoming, outgoing);
                    decimal balance = incoming - outgoing;

                    workspace.StagingNodes[i] = new SankeyNodeValue(
                        workspace.NodeIds[nodeIdx],
                        incoming,
                        outgoing,
                        capacity,
                        balance,
                        layer,
                        currentOrder);
                }

                // 14. Publish staging edges
                for (int i = 0; i < posEdgeCount; i++)
                {
                    var edge = workspace.SortedInputEdges[i];
                    workspace.StagingEdges[i] = new SankeyEdgeValue(edge.Id, edge.SourceId, edge.TargetId, edge.Weight);
                }
            }

            // Copy to caller output buffers
            workspace.StagingNodes.AsSpan(0, nodeCount).CopyTo(nodeOutput);
            workspace.StagingEdges.AsSpan(0, posEdgeCount).CopyTo(edgeOutput);

            result = new SankeyRunResult(
                SankeyStatus.Success,
                nodeCount,
                posEdgeCount,
                layerCount,
                SankeyError.None,
                -1);
        }
        catch (OverflowException)
        {
            result = new SankeyRunResult(SankeyStatus.Invalid, 0, 0, 0, SankeyError.NumericOverflow, -1);
        }
        finally
        {
            workspace.Exit();
        }
    }

    private static bool ValidateMeasure(in SankeyMeasure measure)
    {
        if (!Enum.IsDefined(measure.Kind) || !Enum.IsDefined(measure.Provenance))
            return false;

        if (measure.UnitCode is null ||
            measure.UnitCode.Length < 1 ||
            measure.UnitCode.Length > SankeyConstants.MaxMetadataLength ||
            char.IsWhiteSpace(measure.UnitCode[0]) ||
            char.IsWhiteSpace(measure.UnitCode[^1]) ||
            string.IsNullOrWhiteSpace(measure.UnitCode))
        {
            return false;
        }

        if (measure.DefinitionId is null ||
            measure.DefinitionId.Length < 1 ||
            measure.DefinitionId.Length > SankeyConstants.MaxMetadataLength ||
            char.IsWhiteSpace(measure.DefinitionId[0]) ||
            char.IsWhiteSpace(measure.DefinitionId[^1]) ||
            string.IsNullOrWhiteSpace(measure.DefinitionId))
        {
            return false;
        }

        return true;
    }

    private static int BinarySearchNodeId(int[] sortedNodeIds, int length, int targetId)
    {
        int low = 0;
        int high = length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int midVal = sortedNodeIds[mid];
            if (midVal == targetId)
                return mid;
            if (midVal < targetId)
                low = mid + 1;
            else
                high = mid - 1;
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

    private static void QuickSortInts(int[] array, int left, int right)
    {
        if (left >= right) return;
        int pivot = array[left + ((right - left) >> 1)];
        int i = left;
        int j = right;
        while (i <= j)
        {
            while (array[i] < pivot) i++;
            while (array[j] > pivot) j--;
            if (i <= j)
            {
                (array[i], array[j]) = (array[j], array[i]);
                i++;
                j--;
            }
        }
        if (left < j) QuickSortInts(array, left, j);
        if (i < right) QuickSortInts(array, i, right);
    }

    private static void QuickSortEdgesById(SankeyEdge[] array, int left, int right)
    {
        if (left >= right) return;
        int pivot = array[left + ((right - left) >> 1)].Id;
        int i = left;
        int j = right;
        while (i <= j)
        {
            while (array[i].Id < pivot) i++;
            while (array[j].Id > pivot) j--;
            if (i <= j)
            {
                (array[i], array[j]) = (array[j], array[i]);
                i++;
                j--;
            }
        }
        if (left < j) QuickSortEdgesById(array, left, j);
        if (i < right) QuickSortEdgesById(array, i, right);
    }

    private static void QuickSortNodeIndices(int[] indices, int[] layers, int[] nodeIds, int left, int right)
    {
        if (left >= right) return;
        int pivotIdx = indices[left + ((right - left) >> 1)];
        int pivotLayer = layers[pivotIdx];
        int pivotId = nodeIds[pivotIdx];

        int i = left;
        int j = right;
        while (i <= j)
        {
            while (CompareNodeKeys(indices[i], pivotLayer, pivotId, layers, nodeIds) < 0) i++;
            while (CompareNodeKeys(indices[j], pivotLayer, pivotId, layers, nodeIds) > 0) j--;
            if (i <= j)
            {
                (indices[i], indices[j]) = (indices[j], indices[i]);
                i++;
                j--;
            }
        }
        if (left < j) QuickSortNodeIndices(indices, layers, nodeIds, left, j);
        if (i < right) QuickSortNodeIndices(indices, layers, nodeIds, i, right);
    }

    private static int CompareNodeKeys(int idx, int pivotLayer, int pivotId, int[] layers, int[] nodeIds)
    {
        int layerCmp = layers[idx].CompareTo(pivotLayer);
        return layerCmp != 0 ? layerCmp : nodeIds[idx].CompareTo(pivotId);
    }
}
