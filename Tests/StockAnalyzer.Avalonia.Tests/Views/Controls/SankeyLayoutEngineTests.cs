using System;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis.Sankey;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public sealed class SankeyLayoutEngineTests
{
    [Fact]
    public void T16_ViewportValidation_HandlesSpecialCases()
    {
        using var workspace = new SankeyLayoutWorkspace();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 0m, 100m, 100m, 1, 0)
        };
        var edges = new[] { new SankeyEdgeValue(1, 1, 2, 100m) };

        var nodeBuf = new SankeyNodeGeometry[2];
        var bandBuf = new SankeyBandGeometry[1];

        // 1. Zero width/height -> Suspended
        SankeyLayoutEngine.Layout(nodes, edges, new SankeyViewport(0, 500), nodeBuf, bandBuf, workspace, out var r1);
        Assert.Equal(SankeyLayoutStatus.Suspended, r1.Status);

        SankeyLayoutEngine.Layout(nodes, edges, new SankeyViewport(500, 0), nodeBuf, bandBuf, workspace, out var r2);
        Assert.Equal(SankeyLayoutStatus.Suspended, r2.Status);

        // 2. Negative/NaN/Infinity -> InvalidViewport
        SankeyLayoutEngine.Layout(nodes, edges, new SankeyViewport(-10, 500), nodeBuf, bandBuf, workspace, out var r3);
        Assert.Equal(SankeyLayoutError.InvalidViewport, r3.Error);

        SankeyLayoutEngine.Layout(nodes, edges, new SankeyViewport(double.NaN, 500), nodeBuf, bandBuf, workspace, out var r4);
        Assert.Equal(SankeyLayoutError.InvalidViewport, r4.Error);

        // 3. Excess viewport > 1_000_000 -> InvalidViewport
        SankeyLayoutEngine.Layout(nodes, edges, new SankeyViewport(1_000_001, 500), nodeBuf, bandBuf, workspace, out var r5);
        Assert.Equal(SankeyLayoutError.InvalidViewport, r5.Error);

        // 4. Insufficient width -> InsufficientViewport
        // layerCount = 2, requiredWidth = 2*20 + 24 = 64, margin = 2*16 = 32, total min width = 96
        SankeyLayoutEngine.Layout(nodes, edges, new SankeyViewport(50, 500), nodeBuf, bandBuf, workspace, out var r6);
        Assert.Equal(SankeyLayoutError.InsufficientViewport, r6.Error);
    }

    [Fact]
    public void T04_ProportionalBandwidths_BranchingFlowMatchesRatios()
    {
        using var workspace = new SankeyLayoutWorkspace();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 60m, 0m, 60m, 60m, 1, 0),
            new SankeyNodeValue(3, 40m, 0m, 40m, 40m, 1, 1)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(1, 1, 2, 60m),
            new SankeyEdgeValue(2, 1, 3, 40m)
        };

        var nodeBuf = new SankeyNodeGeometry[3];
        var bandBuf = new SankeyBandGeometry[2];
        var viewport = new SankeyViewport(800, 600);

        SankeyLayoutEngine.Layout(nodes, edges, viewport, nodeBuf, bandBuf, workspace, out var result);

        Assert.Equal(SankeyLayoutStatus.Ready, result.Status);
        Assert.Equal(3, result.NodeCount);
        Assert.Equal(2, result.BandCount);

        // Node 1 height should equal sum of Node 2 and Node 3 heights
        double h1 = nodeBuf[0].Height;
        double h2 = nodeBuf[1].Height;
        double h3 = nodeBuf[2].Height;

        Assert.True(Math.Abs(h1 - (h2 + h3)) <= SankeyConstants.GeometryToleranceDip);

        // Band widths should be exactly 60:40 (3:2) ratio
        double w1 = bandBuf[0].WidthDip;
        double w2 = bandBuf[1].WidthDip;
        Assert.True(Math.Abs((w1 / w2) - (60.0 / 40.0)) <= 1e-6);

        // Band widths should equal node heights
        Assert.True(Math.Abs(w1 - h2) <= SankeyConstants.GeometryToleranceDip);
        Assert.True(Math.Abs(w2 - h3) <= SankeyConstants.GeometryToleranceDip);
    }

    [Fact]
    public void T22_CubicBezierRibbons_HaveExactControlPointsAndTangents()
    {
        using var workspace = new SankeyLayoutWorkspace();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 0m, 100m, 100m, 1, 0)
        };
        var edges = new[] { new SankeyEdgeValue(10, 1, 2, 100m) };

        var nodeBuf = new SankeyNodeGeometry[2];
        var bandBuf = new SankeyBandGeometry[1];
        var viewport = new SankeyViewport(500, 400);

        SankeyLayoutEngine.Layout(nodes, edges, viewport, nodeBuf, bandBuf, workspace, out var result);

        Assert.Equal(SankeyLayoutStatus.Ready, result.Status);
        var node1 = nodeBuf[0];
        var node2 = nodeBuf[1];
        var band = bandBuf[0];

        // Check Upper curve
        double xs = node1.X1;
        double xt = node2.X0;
        double xm = xs + ((xt - xs) / 2.0);

        Assert.Equal(xs, band.Upper.P0.X);
        Assert.Equal(node1.Y0, band.Upper.P0.Y);

        Assert.Equal(xm, band.Upper.P1.X);
        Assert.Equal(node1.Y0, band.Upper.P1.Y);

        Assert.Equal(xm, band.Upper.P2.X);
        Assert.Equal(node2.Y0, band.Upper.P2.Y);

        Assert.Equal(xt, band.Upper.P3.X);
        Assert.Equal(node2.Y0, band.Upper.P3.Y);

        // Check LowerReverse curve
        Assert.Equal(xt, band.LowerReverse.P0.X);
        Assert.Equal(node2.Y1, band.LowerReverse.P0.Y);

        Assert.Equal(xm, band.LowerReverse.P1.X);
        Assert.Equal(node2.Y1, band.LowerReverse.P1.Y);

        Assert.Equal(xm, band.LowerReverse.P2.X);
        Assert.Equal(node1.Y1, band.LowerReverse.P2.Y);

        Assert.Equal(xs, band.LowerReverse.P3.X);
        Assert.Equal(node1.Y1, band.LowerReverse.P3.Y);
    }

    [Fact]
    public void T23_NodeNonOverlapping_WithinSameLayer()
    {
        using var workspace = new SankeyLayoutWorkspace();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 50m, 0m, 50m, 50m, 1, 0),
            new SankeyNodeValue(3, 50m, 0m, 50m, 50m, 1, 1)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(1, 1, 2, 50m),
            new SankeyEdgeValue(2, 1, 3, 50m)
        };

        var nodeBuf = new SankeyNodeGeometry[3];
        var bandBuf = new SankeyBandGeometry[2];
        var viewport = new SankeyViewport(600, 500);

        SankeyLayoutEngine.Layout(nodes, edges, viewport, nodeBuf, bandBuf, workspace, out var result);

        Assert.Equal(SankeyLayoutStatus.Ready, result.Status);

        var layer1Node0 = nodeBuf[1];
        var layer1Node1 = nodeBuf[2];

        // Vertically separated by at least LayerNodeSpacingDip (8.0 DIP)
        Assert.True(layer1Node1.Y0 >= layer1Node0.Y1 + SankeyConstants.LayerNodeSpacingDip - 1e-9);
    }

    [Fact]
    public void T26_CrossingEdges_DeterministicallyLayedOut()
    {
        using var workspace = new SankeyLayoutWorkspace();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 50m, 50m, -50m, 0, 0),
            new SankeyNodeValue(2, 0m, 50m, 50m, -50m, 0, 1),
            new SankeyNodeValue(3, 50m, 0m, 50m, 50m, 1, 0),
            new SankeyNodeValue(4, 50m, 0m, 50m, 50m, 1, 1)
        };
        // Cross: 1 -> 4 and 2 -> 3
        var edges = new[]
        {
            new SankeyEdgeValue(1, 1, 4, 50m),
            new SankeyEdgeValue(2, 2, 3, 50m)
        };

        var nodeBuf = new SankeyNodeGeometry[4];
        var bandBuf = new SankeyBandGeometry[2];
        var viewport = new SankeyViewport(600, 500);

        SankeyLayoutEngine.Layout(nodes, edges, viewport, nodeBuf, bandBuf, workspace, out var result);

        Assert.Equal(SankeyLayoutStatus.Ready, result.Status);
        Assert.Equal(4, result.NodeCount);
        Assert.Equal(2, result.BandCount);
    }

    [Fact]
    public void ZeroAllocation_OnLayoutHotPath()
    {
        using var workspace = new SankeyLayoutWorkspace();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 60m, 0m, 60m, 60m, 1, 0),
            new SankeyNodeValue(3, 40m, 0m, 40m, 40m, 1, 1)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(1, 1, 2, 60m),
            new SankeyEdgeValue(2, 1, 3, 40m)
        };

        var nodeBuf = new SankeyNodeGeometry[3];
        var bandBuf = new SankeyBandGeometry[2];
        var viewport = new SankeyViewport(800, 600);

        // Warm up JIT
        for (int i = 0; i < 10; i++)
        {
            SankeyLayoutEngine.Layout(nodes, edges, viewport, nodeBuf, bandBuf, workspace, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            SankeyLayoutEngine.Layout(nodes, edges, viewport, nodeBuf, bandBuf, workspace, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
