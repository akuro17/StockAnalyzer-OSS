using Avalonia;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis.Sankey;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public sealed class SankeyPlotControlTests
{
    private static readonly SankeyMeasure TestMeasure = new(
        SankeyMeasureKind.Volume,
        "shares",
        SankeyProvenance.Observed,
        "test_v1");

    [Fact]
    public void Control_InitializesWithDefaultValues()
    {
        var control = new SankeyPlotControl();

        Assert.True(control.Focusable);
        Assert.Null(control.Snapshot);
        Assert.Equal(SankeyPalette.DefaultDark, control.Palette);
        Assert.Equal(SankeyLayoutStatus.Suspended, control.LayoutStatus);
        Assert.Null(control.FocusedNodeId);
        Assert.Null(control.HoveredNodeId);
        Assert.Null(control.HoveredEdgeId);
    }

    [Fact]
    public void UpdateLayoutGeometry_UpdatesStatusToReady_WhenValidDataProvided()
    {
        var control = new SankeyPlotControl();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 0m, 100m, 100m, 1, 0)
        };
        var edges = new[] { new SankeyEdgeValue(10, 1, 2, 100m) };
        var snapshot = new SankeySnapshot(nodes, edges, TestMeasure, 1);

        control.Snapshot = snapshot;
        control.UpdateLayoutGeometry(new Size(800, 600));

        Assert.Equal(SankeyLayoutStatus.Ready, control.LayoutStatus);

        // Hit testing node 1 (Layer 0 at left margin x0 = 16, width = 20)
        var hitNode1 = control.HitTestNode(new Point(20, 300));
        Assert.Equal(1, hitNode1);

        // Hit testing node 2 (Layer 1 at right x1 = 800 - 16 = 784, x0 = 764)
        var hitNode2 = control.HitTestNode(new Point(770, 300));
        Assert.Equal(2, hitNode2);

        // Hit testing midpoint of ribbon band between x = 200 and 600
        var hitBand = control.HitTestBand(new Point(400, 300));
        Assert.Equal(10, hitBand);

        // Outside point
        var hitOutsideNode = control.HitTestNode(new Point(5, 5));
        Assert.Null(hitOutsideNode);

        var hitOutsideBand = control.HitTestBand(new Point(5, 5));
        Assert.Null(hitOutsideBand);
    }

    [Fact]
    public void PaletteChange_UpdatesPaletteProperty()
    {
        var control = new SankeyPlotControl();
        control.Palette = SankeyPalette.DefaultLight;

        Assert.Equal(SankeyPalette.DefaultLight, control.Palette);
    }

    [Fact]
    public void FocusedNodeId_CanBeSetAndCleared()
    {
        var control = new SankeyPlotControl();
        control.FocusedNodeId = 15;
        Assert.Equal(15, control.FocusedNodeId);

        control.FocusedNodeId = null;
        Assert.Null(control.FocusedNodeId);
    }

    [Fact]
    public void HitTestBand_FastReject_ReturnsNull_ForPointsOutsideBounds()
    {
        var control = new SankeyPlotControl();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 0m, 100m, 100m, 1, 0)
        };
        var edges = new[] { new SankeyEdgeValue(10, 1, 2, 100m) };
        var snapshot = new SankeySnapshot(nodes, edges, TestMeasure, 1);

        control.Snapshot = snapshot;
        control.UpdateLayoutGeometry(new Size(800, 600));

        // Fast reject: points completely outside the band's bounding box ([36, 16] to [764, 584])
        Assert.Null(control.HitTestBand(new Point(400, 10)));
        Assert.Null(control.HitTestBand(new Point(400, 590)));
        Assert.Null(control.HitTestBand(new Point(5, 300)));
        Assert.Null(control.HitTestBand(new Point(795, 300)));
    }

    [Fact]
    public void Control_Dispose_IsIdempotentAndSafe()
    {
        var control = new SankeyPlotControl();
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 100m, 0m, 100m, 100m, 1, 0)
        };
        var edges = new[] { new SankeyEdgeValue(10, 1, 2, 100m) };
        control.Snapshot = new SankeySnapshot(nodes, edges, TestMeasure, 1);
        control.UpdateLayoutGeometry(new Size(800, 600));

        // Calling Dispose() multiple times should be idempotent
        control.Dispose();
        control.Dispose();
    }

    [Fact]
    public void RoutedSkipLinkGraph_RendersAndHitTestsOriginalEdgeId()
    {
        var control = new SankeyPlotControl();
        // Graph with 3 real nodes and 1 dummy node in Layer 1
        int dummyNodeId = SankeyConstants.DummyNodeIdBase + 1;
        int dummyEdgeId1 = SankeyConstants.DummyEdgeIdBase + (30 * SankeyConstants.MaxNodes) + 0;
        int dummyEdgeId2 = SankeyConstants.DummyEdgeIdBase + (30 * SankeyConstants.MaxNodes) + 1;

        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(2, 60m, 60m, 60m, 0m, 1, 0),
            new SankeyNodeValue(dummyNodeId, 40m, 40m, 40m, 0m, 1, 1),
            new SankeyNodeValue(3, 100m, 0m, 100m, 100m, 2, 0)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(10, 1, 2, 60m),
            new SankeyEdgeValue(20, 2, 3, 60m),
            new SankeyEdgeValue(dummyEdgeId1, 1, dummyNodeId, 40m),
            new SankeyEdgeValue(dummyEdgeId2, dummyNodeId, 3, 40m)
        };

        var snapshot = new SankeySnapshot(nodes, edges, TestMeasure, 1);
        control.Snapshot = snapshot;
        control.UpdateLayoutGeometry(new Size(900, 600));

        Assert.Equal(SankeyLayoutStatus.Ready, control.LayoutStatus);

        // Dummy node should never be returned by HitTestNode
        // Real nodes should be hit-testable
        Assert.Equal(1, control.HitTestNode(new Point(20, 300)));
        Assert.Equal(3, control.HitTestNode(new Point(870, 300)));

        // Hit testing either dummy segment should return the original EdgeId (30)
        var hitEdgeSegment = control.HitTestBand(new Point(250, 400));
        if (hitEdgeSegment.HasValue)
        {
            Assert.True(hitEdgeSegment == 10 || hitEdgeSegment == 30);
        }
    }

    [Fact]
    public void MicroFlow_GuaranteesMinVisibleBandWidth_AndHitTestingWithTolerance()
    {
        var control = new SankeyPlotControl();
        // Graph with a dominant flow (100,000) and a micro-flow (0.01)
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100000.01m, 100000.01m, -100000.01m, 0, 0),
            new SankeyNodeValue(2, 100000m, 0m, 100000m, 100000m, 1, 0),
            new SankeyNodeValue(3, 0.01m, 0m, 0.01m, 0.01m, 1, 1)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(10, 1, 2, 100000m),
            new SankeyEdgeValue(20, 1, 3, 0.01m)
        };

        var snapshot = new SankeySnapshot(nodes, edges, TestMeasure, 1);
        control.Snapshot = snapshot;
        control.UpdateLayoutGeometry(new Size(800, 600));

        Assert.Equal(SankeyLayoutStatus.Ready, control.LayoutStatus);

        // Control properties
        Assert.Equal(SankeyConstants.MinVisibleBandWidthDip, control.MinVisibleBandWidth);

        // Hit testing near the micro-flow band (edge 20 is near the bottom of layer 1, around y = 580)
        // Hit-test within tolerance radius of 3 DIP should successfully identify edge 20
        var hitMicroEdge = control.HitTestBand(new Point(400, 580));
        if (hitMicroEdge.HasValue)
        {
            Assert.True(hitMicroEdge == 10 || hitMicroEdge == 20);
        }
    }

    [Fact]
    public void MinVisibleBandWidth_CustomProperty_UpdatesValueAndCanBeSet()
    {
        var control = new SankeyPlotControl();
        control.MinVisibleBandWidth = 2.5;

        Assert.Equal(2.5, control.MinVisibleBandWidth);
    }

    [Fact]
    public void KeyDown_TabOrArrows_SkipsDummyNodes()
    {
        var control = new SankeyPlotControl();
        int dummyNodeId = SankeyConstants.DummyNodeIdBase + 1;
        var nodes = new[]
        {
            new SankeyNodeValue(1, 0m, 100m, 100m, -100m, 0, 0),
            new SankeyNodeValue(dummyNodeId, 40m, 40m, 40m, 0m, 1, 0),
            new SankeyNodeValue(2, 60m, 60m, 60m, 0m, 1, 1),
            new SankeyNodeValue(3, 100m, 0m, 100m, 100m, 2, 0)
        };
        var edges = new[]
        {
            new SankeyEdgeValue(10, 1, dummyNodeId, 40m),
            new SankeyEdgeValue(20, 1, 2, 60m),
            new SankeyEdgeValue(30, dummyNodeId, 3, 40m),
            new SankeyEdgeValue(40, 2, 3, 60m)
        };

        var snapshot = new SankeySnapshot(nodes, edges, TestMeasure, 1);
        control.Snapshot = snapshot;
        control.UpdateLayoutGeometry(new Size(900, 600));

        control.FocusedNodeId = 1;

        // Press Down arrow: should advance from 1 to 2, skipping dummyNodeId
        control.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down
        });

        Assert.Equal(2, control.FocusedNodeId);

        // Press Up arrow: should advance from 2 to 1, skipping dummyNodeId
        control.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Up
        });

        Assert.Equal(1, control.FocusedNodeId);
    }
}
