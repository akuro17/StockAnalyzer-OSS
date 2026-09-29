using System;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis.Sankey;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Default implementation of ISankeyDataSource managing decomposition analysis and immutable snapshot publishing.
/// </summary>
public sealed class SankeyDataSource : ISankeyDataSource, IDisposable
{
    private readonly SankeyWorkspace _workspace = new();
    private readonly SankeyNodeValue[] _nodeBuffer = new SankeyNodeValue[SankeyConstants.MaxNodes];
    private readonly SankeyEdgeValue[] _edgeBuffer = new SankeyEdgeValue[SankeyConstants.MaxEdges];
    private readonly SankeyNode[] _routedNodeBuffer = new SankeyNode[SankeyConstants.MaxNodes];
    private readonly SankeyEdge[] _routedEdgeBuffer = new SankeyEdge[SankeyConstants.MaxEdges];
    private long _revisionCounter;

    public SankeySnapshot? Current { get; private set; }

    public event EventHandler? Changed;

    public void SetGraph(ReadOnlySpan<SankeyNode> nodes, ReadOnlySpan<SankeyEdge> edges, in SankeyMeasure measure)
    {
        ReadOnlySpan<SankeyNode> effectiveNodes = nodes;
        ReadOnlySpan<SankeyEdge> effectiveEdges = edges;

        SankeySkipLinkRouter.Route(
            nodes,
            edges,
            _routedNodeBuffer,
            _routedEdgeBuffer,
            out var routingResult);

        if (routingResult.Status == SankeyRoutingStatus.Success)
        {
            effectiveNodes = _routedNodeBuffer.AsSpan(0, routingResult.NodeCount);
            effectiveEdges = _routedEdgeBuffer.AsSpan(0, routingResult.EdgeCount);
        }

        SankeyDecompositionEngine.Analyze(
            effectiveNodes,
            effectiveEdges,
            measure,
            _nodeBuffer,
            _edgeBuffer,
            _workspace,
            out var runResult);

        if (runResult.Status == SankeyStatus.Success)
        {
            var nodeCopy = new SankeyNodeValue[runResult.NodeCount];
            var edgeCopy = new SankeyEdgeValue[runResult.EdgeCount];
            Array.Copy(_nodeBuffer, nodeCopy, runResult.NodeCount);
            Array.Copy(_edgeBuffer, edgeCopy, runResult.EdgeCount);

            long rev = ++_revisionCounter;
            Current = new SankeySnapshot(nodeCopy, edgeCopy, measure, rev);
        }
        else if (runResult.Status == SankeyStatus.Empty)
        {
            Current = SankeySnapshot.Empty;
        }
        else
        {
            Current = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
