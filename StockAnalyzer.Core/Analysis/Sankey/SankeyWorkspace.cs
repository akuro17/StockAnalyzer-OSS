using System;
using System.Buffers;
using System.Threading;

namespace StockAnalyzer.Core.Analysis.Sankey;

/// <summary>
/// Caller-owned workspace managing pre-allocated, zero-allocation buffers for Sankey decomposition.
/// </summary>
public sealed class SankeyWorkspace : IDisposable
{
    private int _isBusy;
    private int _isDisposed;

    // Node-related rented arrays (capacity = SankeyConstants.MaxNodes)
    internal readonly int[] NodeIds;
    internal readonly decimal[] Incomings;
    internal readonly decimal[] Outgoings;
    internal readonly int[] InDegrees;
    internal readonly int[] Layers;
    internal readonly int[] Orders;
    internal readonly int[] NodeSortIndices;
    internal readonly int[] MinHeap;
    internal readonly int[] HeadEdge;
    internal readonly SankeyNodeValue[] StagingNodes;

    // Edge-related rented arrays (capacity = SankeyConstants.MaxEdges)
    internal readonly int[] NextEdge;
    internal readonly int[] EdgeTargetNodeIndex;
    internal readonly int[] EdgeSourceNodeIndex;
    internal readonly decimal[] EdgeWeights;
    internal readonly int[] EdgeOriginalIds;
    internal readonly SankeyEdge[] SortedInputEdges;
    internal readonly SankeyEdgeValue[] StagingEdges;

    public SankeyWorkspace()
    {
        NodeIds = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        Incomings = ArrayPool<decimal>.Shared.Rent(SankeyConstants.MaxNodes);
        Outgoings = ArrayPool<decimal>.Shared.Rent(SankeyConstants.MaxNodes);
        InDegrees = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        Layers = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        Orders = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        NodeSortIndices = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        MinHeap = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        HeadEdge = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        StagingNodes = ArrayPool<SankeyNodeValue>.Shared.Rent(SankeyConstants.MaxNodes);

        NextEdge = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeTargetNodeIndex = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeSourceNodeIndex = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeWeights = ArrayPool<decimal>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeOriginalIds = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxEdges);
        SortedInputEdges = ArrayPool<SankeyEdge>.Shared.Rent(SankeyConstants.MaxEdges);
        StagingEdges = ArrayPool<SankeyEdgeValue>.Shared.Rent(SankeyConstants.MaxEdges);
    }

    /// <summary>
    /// Indicates whether the workspace has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>
    /// Indicates whether the workspace is currently executing an analysis.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;

    internal bool TryEnter(out SankeyError error)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            error = SankeyError.WorkspaceDisposed;
            return false;
        }

        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
        {
            error = SankeyError.WorkspaceBusy;
            return false;
        }

        if (Volatile.Read(ref _isDisposed) != 0)
        {
            Interlocked.Exchange(ref _isBusy, 0);
            error = SankeyError.WorkspaceDisposed;
            return false;
        }

        error = SankeyError.None;
        return true;
    }

    internal void Exit()
    {
        Interlocked.Exchange(ref _isBusy, 0);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _isBusy) != 0)
        {
            throw new InvalidOperationException("Cannot dispose SankeyWorkspace while an analysis is in progress.");
        }

        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            if (Volatile.Read(ref _isBusy) != 0)
            {
                throw new InvalidOperationException("Cannot dispose SankeyWorkspace while an analysis is in progress.");
            }

            ArrayPool<int>.Shared.Return(NodeIds);
            ArrayPool<decimal>.Shared.Return(Incomings);
            ArrayPool<decimal>.Shared.Return(Outgoings);
            ArrayPool<int>.Shared.Return(InDegrees);
            ArrayPool<int>.Shared.Return(Layers);
            ArrayPool<int>.Shared.Return(Orders);
            ArrayPool<int>.Shared.Return(NodeSortIndices);
            ArrayPool<int>.Shared.Return(MinHeap);
            ArrayPool<int>.Shared.Return(HeadEdge);
            ArrayPool<SankeyNodeValue>.Shared.Return(StagingNodes);

            ArrayPool<int>.Shared.Return(NextEdge);
            ArrayPool<int>.Shared.Return(EdgeTargetNodeIndex);
            ArrayPool<int>.Shared.Return(EdgeSourceNodeIndex);
            ArrayPool<decimal>.Shared.Return(EdgeWeights);
            ArrayPool<int>.Shared.Return(EdgeOriginalIds);
            ArrayPool<SankeyEdge>.Shared.Return(SortedInputEdges);
            ArrayPool<SankeyEdgeValue>.Shared.Return(StagingEdges);
        }
    }
}
