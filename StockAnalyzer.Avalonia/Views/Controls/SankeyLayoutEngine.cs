using System;
using StockAnalyzer.Core.Analysis.Sankey;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Framework-neutral layout engine for Sankey diagrams.
/// Computes DIP screen coordinates for nodes and cubic Bézier ribbon bands.
/// </summary>
public static class SankeyLayoutEngine
{
    /// <summary>
    /// Computes the visual geometry for a valid Sankey decomposition snapshot into caller-owned buffers.
    /// </summary>
    public static void Layout(
        ReadOnlySpan<SankeyNodeValue> nodes,
        ReadOnlySpan<SankeyEdgeValue> edges,
        in SankeyViewport viewport,
        Span<SankeyNodeGeometry> nodeOutput,
        Span<SankeyBandGeometry> bandOutput,
        SankeyLayoutWorkspace workspace,
        out SankeyLayoutResult result)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!workspace.TryEnter(out var wsError))
        {
            result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, wsError);
            return;
        }

        try
        {
            // 1. Snapshot validation
            if (!ValidateSnapshot(nodes, edges, workspace))
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.InvalidSnapshot);
                return;
            }

            // 2. Viewport validation
            if (double.IsNaN(viewport.WidthDip) || double.IsInfinity(viewport.WidthDip) || viewport.WidthDip < 0.0 ||
                double.IsNaN(viewport.HeightDip) || double.IsInfinity(viewport.HeightDip) || viewport.HeightDip < 0.0 ||
                viewport.WidthDip > SankeyConstants.MaxViewportDip || viewport.HeightDip > SankeyConstants.MaxViewportDip)
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.InvalidViewport);
                return;
            }

            if (viewport.WidthDip == 0.0 || viewport.HeightDip == 0.0)
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.Suspended, 0, 0, SankeyLayoutError.None);
                return;
            }

            // 3. Output buffer capacity check
            if (nodeOutput.Length < nodes.Length || bandOutput.Length < edges.Length)
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.BufferTooSmall);
                return;
            }

            // 4. Filter active nodes (Capacity > 0)
            int activeNodeCount = 0;
            int maxLayer = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Capacity > 0m)
                {
                    workspace.ActiveNodeIndices[activeNodeCount++] = i;
                    if (nodes[i].Layer > maxLayer)
                    {
                        maxLayer = nodes[i].Layer;
                    }
                }
            }

            if (activeNodeCount == 0 || edges.Length == 0)
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.NoPositiveFlow, 0, 0, SankeyLayoutError.None);
                return;
            }

            int layerCount = maxLayer + 1;
            if (layerCount < 2)
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.NoPositiveFlow, 0, 0, SankeyLayoutError.None);
                return;
            }

            // 5. Viewport dimension sufficiency
            double m = SankeyConstants.MarginDip;
            double w = SankeyConstants.NodeWidthDip;
            double p = SankeyConstants.LayerNodeSpacingDip;
            double g = SankeyConstants.MinColumnSpacingDip;

            double usableWidth = viewport.WidthDip - (2.0 * m);
            double usableHeight = viewport.HeightDip - (2.0 * m);

            double requiredWidth = (layerCount * w) + ((layerCount - 1) * g);
            if (usableWidth < requiredWidth)
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.InsufficientViewport);
                return;
            }

            // Initialize layer metrics
            for (int l = 0; l < layerCount; l++)
            {
                workspace.LayerSums[l] = 0m;
                workspace.LayerNodeCounts[l] = 0;
            }

            for (int i = 0; i < activeNodeCount; i++)
            {
                int nodeIdx = workspace.ActiveNodeIndices[i];
                int l = nodes[nodeIdx].Layer;
                workspace.LayerSums[l] += nodes[nodeIdx].Capacity;
                workspace.LayerNodeCounts[l]++;
            }

            for (int l = 0; l < layerCount; l++)
            {
                int nl = workspace.LayerNodeCounts[l];
                if (nl > 0)
                {
                    double requiredHeight = (nl - 1) * p;
                    if (usableHeight <= requiredHeight)
                    {
                        result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.InsufficientViewport);
                        return;
                    }
                }
            }

            // 6. Proportional Scale Factor K (Direct Algebraic Form)
            double scaleK = double.MaxValue;
            for (int l = 0; l < layerCount; l++)
            {
                int nl = workspace.LayerNodeCounts[l];
                if (nl > 0 && workspace.LayerSums[l] > 0m)
                {
                    double availableLayerHeight = usableHeight - ((nl - 1) * p);
                    double kCandidate = availableLayerHeight / (double)workspace.LayerSums[l];
                    if (kCandidate < scaleK)
                    {
                        scaleK = kCandidate;
                    }
                }
            }

            if (!double.IsFinite(scaleK) || scaleK == double.MaxValue || scaleK <= 0.0)
            {
                result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.GeometryPrecision);
                return;
            }

            // 7. Calculate Node Heights and Band Widths
            for (int i = 0; i < nodes.Length; i++)
            {
                double height = scaleK * (double)nodes[i].Capacity;
                workspace.NodeHeights[i] = height;
            }

            for (int i = 0; i < edges.Length; i++)
            {
                double width = scaleK * (double)edges[i].Weight;
                if (!double.IsFinite(width) || width <= 0.0)
                {
                    result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.GeometryPrecision);
                    return;
                }
                workspace.BandWidths[i] = width;
            }

            // 8. Position Nodes
            for (int l = 0; l < layerCount; l++)
            {
                double x0 = m + (l * ((usableWidth - w) / (layerCount - 1)));
                workspace.LayerX0[l] = x0;

                double usedHeight = 0.0;
                for (int i = 0; i < activeNodeCount; i++)
                {
                    int nIdx = workspace.ActiveNodeIndices[i];
                    if (nodes[nIdx].Layer == l)
                    {
                        usedHeight += workspace.NodeHeights[nIdx];
                    }
                }
                usedHeight += (workspace.LayerNodeCounts[l] - 1) * p;
                workspace.LayerUsedHeights[l] = usedHeight;

                double currentY = m + ((usableHeight - usedHeight) / 2.0);
                for (int i = 0; i < activeNodeCount; i++)
                {
                    int nIdx = workspace.ActiveNodeIndices[i];
                    if (nodes[nIdx].Layer == l)
                    {
                        double nodeH = workspace.NodeHeights[nIdx];
                        bool isDummy = SankeyConstants.IsDummyNode(nodes[nIdx].Id);
                        double nodeW = isDummy ? 0.0 : w;
                        double nodeX0 = isDummy ? (x0 + (w / 2.0)) : x0;
                        double nodeX1 = nodeX0 + nodeW;

                        workspace.NodeX0[nIdx] = nodeX0;
                        workspace.NodeX1[nIdx] = nodeX1;
                        workspace.NodeY0[nIdx] = currentY;
                        workspace.NodeY1[nIdx] = currentY + nodeH;
                        workspace.SourceOffsets[nIdx] = currentY;
                        workspace.TargetOffsets[nIdx] = currentY;

                        currentY += nodeH + p;
                    }
                }
            }

            // 9. Position Ribbon Bands
            // Sort edge indices by source order: (sourceNode.Id, targetNode.Order, edge.Id)
            for (int i = 0; i < edges.Length; i++)
            {
                workspace.EdgeIndices[i] = i;
            }
            QuickSortEdgeIndicesBySource(workspace.EdgeIndices, edges, nodes, workspace.EdgeSourceNodeIndex, workspace.EdgeTargetNodeIndex, 0, edges.Length - 1);

            double[] srcTopSpan = workspace.EdgeSourceTop;

            for (int i = 0; i < edges.Length; i++)
            {
                int eIdx = workspace.EdgeIndices[i];
                int srcNodeIdx = workspace.EdgeSourceNodeIndex[eIdx];
                double a = workspace.SourceOffsets[srcNodeIdx];
                double b = a + workspace.BandWidths[eIdx];

                double nodeY1 = workspace.NodeY1[srcNodeIdx];
                if (b > nodeY1)
                {
                    if (b - nodeY1 <= SankeyConstants.GeometryToleranceDip)
                    {
                        b = nodeY1;
                    }
                    else
                    {
                        result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.GeometryPrecision);
                        return;
                    }
                }

                srcTopSpan[eIdx] = a;
                workspace.SourceOffsets[srcNodeIdx] = b;
            }

            // Sort edge indices by target order: (targetNode.Id, sourceNode.Order, edge.Id)
            QuickSortEdgeIndicesByTarget(workspace.EdgeIndices, edges, nodes, workspace.EdgeSourceNodeIndex, workspace.EdgeTargetNodeIndex, 0, edges.Length - 1);

            double[] tgtTopSpan = workspace.EdgeTargetTop;

            for (int i = 0; i < edges.Length; i++)
            {
                int eIdx = workspace.EdgeIndices[i];
                int tgtNodeIdx = workspace.EdgeTargetNodeIndex[eIdx];
                double c = workspace.TargetOffsets[tgtNodeIdx];
                double d = c + workspace.BandWidths[eIdx];

                double nodeY1 = workspace.NodeY1[tgtNodeIdx];
                if (d > nodeY1)
                {
                    if (d - nodeY1 <= SankeyConstants.GeometryToleranceDip)
                    {
                        d = nodeY1;
                    }
                    else
                    {
                        result = new SankeyLayoutResult(SankeyLayoutStatus.Invalid, 0, 0, SankeyLayoutError.GeometryPrecision);
                        return;
                    }
                }

                tgtTopSpan[eIdx] = c;
                workspace.TargetOffsets[tgtNodeIdx] = d;
            }

            // 10. Generate Cubic Curves
            for (int i = 0; i < edges.Length; i++)
            {
                int srcNodeIdx = workspace.EdgeSourceNodeIndex[i];
                int tgtNodeIdx = workspace.EdgeTargetNodeIndex[i];

                double xs = workspace.NodeX1[srcNodeIdx];
                double xt = workspace.NodeX0[tgtNodeIdx];
                double xm = xs + ((xt - xs) / 2.0);

                double a = srcTopSpan[i];
                double b = a + workspace.BandWidths[i];
                double c = tgtTopSpan[i];
                double d = c + workspace.BandWidths[i];

                var upper = new SankeyCubic(
                    new SankeyScreenPoint(xs, a),
                    new SankeyScreenPoint(xm, a),
                    new SankeyScreenPoint(xm, c),
                    new SankeyScreenPoint(xt, c));

                var lowerReverse = new SankeyCubic(
                    new SankeyScreenPoint(xt, d),
                    new SankeyScreenPoint(xm, d),
                    new SankeyScreenPoint(xm, b),
                    new SankeyScreenPoint(xs, b));

                workspace.StagingBands[i] = new SankeyBandGeometry(edges[i].Id, workspace.BandWidths[i], upper, lowerReverse);
            }

            // 11. Publish Geometries
            for (int i = 0; i < activeNodeCount; i++)
            {
                int nIdx = workspace.ActiveNodeIndices[i];
                workspace.StagingNodes[i] = new SankeyNodeGeometry(
                    nodes[nIdx].Id,
                    workspace.NodeX0[nIdx],
                    workspace.NodeY0[nIdx],
                    workspace.NodeX1[nIdx],
                    workspace.NodeY1[nIdx]);
            }

            workspace.StagingNodes.AsSpan(0, activeNodeCount).CopyTo(nodeOutput);
            workspace.StagingBands.AsSpan(0, edges.Length).CopyTo(bandOutput);

            result = new SankeyLayoutResult(SankeyLayoutStatus.Ready, activeNodeCount, edges.Length, SankeyLayoutError.None);
        }
        finally
        {
            workspace.Exit();
        }
    }

    private static bool ValidateSnapshot(
        ReadOnlySpan<SankeyNodeValue> nodes,
        ReadOnlySpan<SankeyEdgeValue> edges,
        SankeyLayoutWorkspace workspace)
    {
        if (nodes.Length > SankeyConstants.MaxNodes || edges.Length > SankeyConstants.MaxEdges)
            return false;

        // Node ID checks & ordering
        for (int i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (node.Id < 0 || node.Layer < 0 || node.Order < 0 || node.Incoming < 0m || node.Outgoing < 0m)
                return false;

            if (node.Capacity != Math.Max(node.Incoming, node.Outgoing) ||
                node.Balance != (node.Incoming - node.Outgoing))
            {
                return false;
            }

            if (i > 0)
            {
                int prevLayer = nodes[i - 1].Layer;
                int currLayer = node.Layer;
                if (currLayer < prevLayer)
                    return false;
                if (currLayer == prevLayer && node.Order <= nodes[i - 1].Order)
                    return false;
            }
        }

        // Map node indices for edge referencing
        for (int i = 0; i < edges.Length; i++)
        {
            var edge = edges[i];
            if (edge.Id < 0 || edge.Weight <= 0m)
                return false;

            if (i > 0 && edge.Id <= edges[i - 1].Id)
                return false;

            int srcIdx = FindNodeIndex(nodes, edge.SourceId);
            int tgtIdx = FindNodeIndex(nodes, edge.TargetId);
            if (srcIdx < 0 || tgtIdx < 0)
                return false;

            if (nodes[tgtIdx].Layer != nodes[srcIdx].Layer + 1)
                return false;

            workspace.EdgeSourceNodeIndex[i] = srcIdx;
            workspace.EdgeTargetNodeIndex[i] = tgtIdx;
        }

        return true;
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

    private static void QuickSortEdgeIndicesBySource(
        int[] indices,
        ReadOnlySpan<SankeyEdgeValue> edges,
        ReadOnlySpan<SankeyNodeValue> nodes,
        int[] edgeSourceNodeIndices,
        int[] edgeTargetNodeIndices,
        int left,
        int right)
    {
        if (left >= right) return;
        int pivotIdx = indices[left + ((right - left) >> 1)];
        int i = left;
        int j = right;
        while (i <= j)
        {
            while (CompareSourceEdgeOrder(indices[i], pivotIdx, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices) < 0) i++;
            while (CompareSourceEdgeOrder(indices[j], pivotIdx, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices) > 0) j--;
            if (i <= j)
            {
                (indices[i], indices[j]) = (indices[j], indices[i]);
                i++;
                j--;
            }
        }
        if (left < j) QuickSortEdgeIndicesBySource(indices, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices, left, j);
        if (i < right) QuickSortEdgeIndicesBySource(indices, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices, i, right);
    }

    private static int CompareSourceEdgeOrder(
        int a,
        int b,
        ReadOnlySpan<SankeyEdgeValue> edges,
        ReadOnlySpan<SankeyNodeValue> nodes,
        int[] edgeSourceNodeIndices,
        int[] edgeTargetNodeIndices)
    {
        int srcA = edgeSourceNodeIndices[a];
        int srcB = edgeSourceNodeIndices[b];
        int cmpSrc = srcA.CompareTo(srcB);
        if (cmpSrc != 0) return cmpSrc;

        int tgtA = edgeTargetNodeIndices[a];
        int tgtB = edgeTargetNodeIndices[b];
        int cmpTgtOrder = nodes[tgtA].Order.CompareTo(nodes[tgtB].Order);
        if (cmpTgtOrder != 0) return cmpTgtOrder;

        return edges[a].Id.CompareTo(edges[b].Id);
    }

    private static void QuickSortEdgeIndicesByTarget(
        int[] indices,
        ReadOnlySpan<SankeyEdgeValue> edges,
        ReadOnlySpan<SankeyNodeValue> nodes,
        int[] edgeSourceNodeIndices,
        int[] edgeTargetNodeIndices,
        int left,
        int right)
    {
        if (left >= right) return;
        int pivotIdx = indices[left + ((right - left) >> 1)];
        int i = left;
        int j = right;
        while (i <= j)
        {
            while (CompareTargetEdgeOrder(indices[i], pivotIdx, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices) < 0) i++;
            while (CompareTargetEdgeOrder(indices[j], pivotIdx, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices) > 0) j--;
            if (i <= j)
            {
                (indices[i], indices[j]) = (indices[j], indices[i]);
                i++;
                j--;
            }
        }
        if (left < j) QuickSortEdgeIndicesByTarget(indices, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices, left, j);
        if (i < right) QuickSortEdgeIndicesByTarget(indices, edges, nodes, edgeSourceNodeIndices, edgeTargetNodeIndices, i, right);
    }

    private static int CompareTargetEdgeOrder(
        int a,
        int b,
        ReadOnlySpan<SankeyEdgeValue> edges,
        ReadOnlySpan<SankeyNodeValue> nodes,
        int[] edgeSourceNodeIndices,
        int[] edgeTargetNodeIndices)
    {
        int tgtA = edgeTargetNodeIndices[a];
        int tgtB = edgeTargetNodeIndices[b];
        int cmpTgt = tgtA.CompareTo(tgtB);
        if (cmpTgt != 0) return cmpTgt;

        int srcA = edgeSourceNodeIndices[a];
        int srcB = edgeSourceNodeIndices[b];
        int cmpSrcOrder = nodes[srcA].Order.CompareTo(nodes[srcB].Order);
        if (cmpSrcOrder != 0) return cmpSrcOrder;

        return edges[a].Id.CompareTo(edges[b].Id);
    }
}
