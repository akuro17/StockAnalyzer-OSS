using System;
using StockAnalyzer.Core.Analysis.Sankey;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class SankeyWorkspaceTests
{
    [Fact]
    public void Workspace_InitializesWithProperCapacity()
    {
        using var workspace = new SankeyWorkspace();

        Assert.NotNull(workspace.NodeIds);
        Assert.NotNull(workspace.SortedInputEdges);
        Assert.True(workspace.NodeIds.Length >= SankeyConstants.MaxNodes);
        Assert.True(workspace.SortedInputEdges.Length >= SankeyConstants.MaxEdges);
        Assert.False(workspace.IsBusy);
        Assert.False(workspace.IsDisposed);
    }

    [Fact]
    public void Workspace_Dispose_IsIdempotent()
    {
        var workspace = new SankeyWorkspace();
        workspace.Dispose();
        Assert.True(workspace.IsDisposed);

        // Second dispose should be a safe no-op
        var ex = Record.Exception(() => workspace.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Workspace_ConcurrentAcquire_YieldsWorkspaceBusy()
    {
        using var workspace = new SankeyWorkspace();

        Assert.True(workspace.TryEnter(out var err1));
        Assert.Equal(SankeyError.None, err1);
        Assert.True(workspace.IsBusy);

        // Concurrent acquire
        Assert.False(workspace.TryEnter(out var err2));
        Assert.Equal(SankeyError.WorkspaceBusy, err2);

        workspace.Exit();
        Assert.False(workspace.IsBusy);

        // Subsequent acquire succeeds
        Assert.True(workspace.TryEnter(out var err3));
        Assert.Equal(SankeyError.None, err3);
        workspace.Exit();
    }

    [Fact]
    public void Workspace_Disposed_ReturnsWorkspaceDisposed()
    {
        var workspace = new SankeyWorkspace();
        workspace.Dispose();

        Assert.False(workspace.TryEnter(out var err));
        Assert.Equal(SankeyError.WorkspaceDisposed, err);
    }

    [Fact]
    public void Workspace_DisposeWhileBusy_ThrowsInvalidOperationException()
    {
        var workspace = new SankeyWorkspace();
        Assert.True(workspace.TryEnter(out _));

        Assert.Throws<InvalidOperationException>(() => workspace.Dispose());

        workspace.Exit();
        workspace.Dispose();
    }
}
