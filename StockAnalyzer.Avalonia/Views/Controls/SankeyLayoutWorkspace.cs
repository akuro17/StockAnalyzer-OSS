using System;
using System.Buffers;
using System.Threading;
using StockAnalyzer.Core.Analysis.Sankey;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Caller-owned workspace managing pre-allocated, zero-allocation buffers for Sankey layout geometry.
/// </summary>
public sealed class SankeyLayoutWorkspace : IDisposable
{
    private int _isBusy;
    private int _isDisposed;

    // Node-related rented buffers (capacity = SankeyConstants.MaxNodes)
    internal readonly int[] ActiveNodeIndices;
    internal readonly double[] NodeHeights;
    internal readonly double[] NodeX0;
    internal readonly double[] NodeY0;
    internal readonly double[] NodeX1;
    internal readonly double[] NodeY1;
    internal readonly double[] SourceOffsets;
    internal readonly double[] TargetOffsets;
    internal readonly SankeyNodeGeometry[] StagingNodes;

    // Layer-related rented buffers (capacity = SankeyConstants.MaxNodes)
    internal readonly decimal[] LayerSums;
    internal readonly int[] LayerNodeCounts;
    internal readonly double[] LayerUsedHeights;
    internal readonly double[] LayerX0;

    // Edge-related rented buffers (capacity = SankeyConstants.MaxEdges)
    internal readonly int[] EdgeIndices;
    internal readonly int[] EdgeSourceNodeIndex;
    internal readonly int[] EdgeTargetNodeIndex;
    internal readonly double[] BandWidths;
    internal readonly double[] EdgeSourceTop;
    internal readonly double[] EdgeTargetTop;
    internal readonly SankeyBandGeometry[] StagingBands;

    public SankeyLayoutWorkspace()
    {
        ActiveNodeIndices = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        NodeHeights = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        NodeX0 = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        NodeY0 = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        NodeX1 = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        NodeY1 = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        SourceOffsets = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        TargetOffsets = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        StagingNodes = ArrayPool<SankeyNodeGeometry>.Shared.Rent(SankeyConstants.MaxNodes);

        LayerSums = ArrayPool<decimal>.Shared.Rent(SankeyConstants.MaxNodes);
        LayerNodeCounts = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxNodes);
        LayerUsedHeights = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);
        LayerX0 = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxNodes);

        EdgeIndices = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeSourceNodeIndex = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeTargetNodeIndex = ArrayPool<int>.Shared.Rent(SankeyConstants.MaxEdges);
        BandWidths = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeSourceTop = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxEdges);
        EdgeTargetTop = ArrayPool<double>.Shared.Rent(SankeyConstants.MaxEdges);
        StagingBands = ArrayPool<SankeyBandGeometry>.Shared.Rent(SankeyConstants.MaxEdges);
    }

    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;
    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;

    internal bool TryEnter(out SankeyLayoutError error)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            error = SankeyLayoutError.WorkspaceDisposed;
            return false;
        }

        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
        {
            error = SankeyLayoutError.WorkspaceBusy;
            return false;
        }

        if (Volatile.Read(ref _isDisposed) != 0)
        {
            Interlocked.Exchange(ref _isBusy, 0);
            error = SankeyLayoutError.WorkspaceDisposed;
            return false;
        }

        error = SankeyLayoutError.None;
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
            throw new InvalidOperationException("Cannot dispose SankeyLayoutWorkspace while layout calculation is in progress.");
        }

        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            if (Volatile.Read(ref _isBusy) != 0)
            {
                throw new InvalidOperationException("Cannot dispose SankeyLayoutWorkspace while layout calculation is in progress.");
            }

            ArrayPool<int>.Shared.Return(ActiveNodeIndices);
            ArrayPool<double>.Shared.Return(NodeHeights);
            ArrayPool<double>.Shared.Return(NodeX0);
            ArrayPool<double>.Shared.Return(NodeY0);
            ArrayPool<double>.Shared.Return(NodeX1);
            ArrayPool<double>.Shared.Return(NodeY1);
            ArrayPool<double>.Shared.Return(SourceOffsets);
            ArrayPool<double>.Shared.Return(TargetOffsets);
            ArrayPool<SankeyNodeGeometry>.Shared.Return(StagingNodes);

            ArrayPool<decimal>.Shared.Return(LayerSums);
            ArrayPool<int>.Shared.Return(LayerNodeCounts);
            ArrayPool<double>.Shared.Return(LayerUsedHeights);
            ArrayPool<double>.Shared.Return(LayerX0);

            ArrayPool<int>.Shared.Return(EdgeIndices);
            ArrayPool<int>.Shared.Return(EdgeSourceNodeIndex);
            ArrayPool<int>.Shared.Return(EdgeTargetNodeIndex);
            ArrayPool<double>.Shared.Return(BandWidths);
            ArrayPool<double>.Shared.Return(EdgeSourceTop);
            ArrayPool<double>.Shared.Return(EdgeTargetTop);
            ArrayPool<SankeyBandGeometry>.Shared.Return(StagingBands);
        }
    }
}
