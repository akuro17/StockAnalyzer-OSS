using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using StockAnalyzer.Core.Analysis.Sankey;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// High-performance Avalonia control for interactive Sankey diagram rendering.
/// Employs SkiaSharp hardware-accelerated drawing, cached SKPath hit-testing, and zero-allocation frame updates.
/// </summary>
public sealed class SankeyPlotControl : Control, IDisposable
{
    public static readonly StyledProperty<SankeySnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<SankeyPlotControl, SankeySnapshot?>(nameof(Snapshot));

    public static readonly StyledProperty<SankeyPalette> PaletteProperty =
        AvaloniaProperty.Register<SankeyPlotControl, SankeyPalette>(nameof(Palette), SankeyPalette.DefaultDark);

    public static readonly StyledProperty<int?> FocusedNodeIdProperty =
        AvaloniaProperty.Register<SankeyPlotControl, int?>(nameof(FocusedNodeId));

    public static readonly StyledProperty<int?> HoveredNodeIdProperty =
        AvaloniaProperty.Register<SankeyPlotControl, int?>(nameof(HoveredNodeId));

    public static readonly StyledProperty<int?> HoveredEdgeIdProperty =
        AvaloniaProperty.Register<SankeyPlotControl, int?>(nameof(HoveredEdgeId));

    public static readonly StyledProperty<SankeyLayoutStatus> LayoutStatusProperty =
        AvaloniaProperty.Register<SankeyPlotControl, SankeyLayoutStatus>(nameof(LayoutStatus), SankeyLayoutStatus.Suspended);

    public static readonly StyledProperty<double> MinVisibleBandWidthProperty =
        AvaloniaProperty.Register<SankeyPlotControl, double>(
            nameof(MinVisibleBandWidth),
            SankeyConstants.MinVisibleBandWidthDip);

    private readonly SankeyLayoutWorkspace _layoutWorkspace = new();
    private readonly SankeyNodeGeometry[] _cachedNodes = new SankeyNodeGeometry[SankeyConstants.MaxNodes];
    private readonly string[] _cachedNodeLabels = new string[SankeyConstants.MaxNodes];
    private readonly SankeyBandGeometry[] _cachedBands = new SankeyBandGeometry[SankeyConstants.MaxEdges];
    private readonly SKPath?[] _cachedBandPaths = new SKPath?[SankeyConstants.MaxEdges];
    private int _cachedNodeCount;
    private int _cachedBandCount;

    private readonly SKPaint _nodePaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _linkPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, TextSize = (float)SankeyConstants.LabelFontSizeDip };
    private readonly SKPaint _focusPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)SankeyConstants.FocusBorderWidthDip };
    private readonly SKPaint _bgPaint = new() { Style = SKPaintStyle.Fill };

    private readonly SankeyDrawOperation _drawOperation;
    private long _lastLayoutRevision = -1;
    private Size _lastLayoutSize;

    public SankeyPlotControl()
    {
        Focusable = true;
        _drawOperation = new SankeyDrawOperation(this);

        UpdatePaintColors(Palette);
    }

    public SankeySnapshot? Snapshot
    {
        get => GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public SankeyPalette Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public int? FocusedNodeId
    {
        get => GetValue(FocusedNodeIdProperty);
        set => SetValue(FocusedNodeIdProperty, value);
    }

    public int? HoveredNodeId
    {
        get => GetValue(HoveredNodeIdProperty);
        set => SetValue(HoveredNodeIdProperty, value);
    }

    public int? HoveredEdgeId
    {
        get => GetValue(HoveredEdgeIdProperty);
        set => SetValue(HoveredEdgeIdProperty, value);
    }

    public SankeyLayoutStatus LayoutStatus
    {
        get => GetValue(LayoutStatusProperty);
        private set => SetValue(LayoutStatusProperty, value);
    }

    public double MinVisibleBandWidth
    {
        get => GetValue(MinVisibleBandWidthProperty);
        set => SetValue(MinVisibleBandWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SnapshotProperty || change.Property == BoundsProperty || change.Property == MinVisibleBandWidthProperty)
        {
            UpdateLayoutGeometry();
            InvalidateVisual();
        }
        else if (change.Property == PaletteProperty)
        {
            UpdatePaintColors(Palette);
            InvalidateVisual();
        }
        else if (change.Property == FocusedNodeIdProperty)
        {
            InvalidateVisual();
        }
    }

    private void UpdatePaintColors(SankeyPalette palette)
    {
        _bgPaint.Color = new SKColor(palette.BackgroundArgb);
        _nodePaint.Color = new SKColor(palette.NodeArgb);
        _linkPaint.Color = new SKColor(palette.LinkArgb);
        _textPaint.Color = new SKColor(palette.TextArgb);
        _focusPaint.Color = new SKColor(palette.FocusArgb);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateLayoutGeometry(finalSize);
        return size;
    }

    public void UpdateLayoutGeometry(Size? explicitSize = null)
    {
        var snapshot = Snapshot;
        var size = explicitSize ?? Bounds.Size;

        if (snapshot == null || snapshot.Nodes.Length == 0 || size.Width <= 0 || size.Height <= 0)
        {
            _cachedNodeCount = 0;
            _cachedBandCount = 0;
            LayoutStatus = SankeyLayoutStatus.Suspended;
            return;
        }

        if (_lastLayoutRevision == snapshot.Revision && _lastLayoutSize == size)
        {
            return;
        }

        var viewport = new SankeyViewport(size.Width, size.Height);
        SankeyLayoutEngine.Layout(
            snapshot.Nodes,
            snapshot.Edges,
            viewport,
            _cachedNodes,
            _cachedBands,
            _layoutWorkspace,
            out var layoutResult);

        LayoutStatus = layoutResult.Status;
        if (layoutResult.Status == SankeyLayoutStatus.Ready)
        {
            _cachedNodeCount = layoutResult.NodeCount;
            _cachedBandCount = layoutResult.BandCount;

            double minWidth = MinVisibleBandWidth;

            // Pre-cache node label strings with InvariantCulture to avoid allocations in RenderSkia
            for (int i = 0; i < _cachedNodeCount; i++)
            {
                _cachedNodeLabels[i] = _cachedNodes[i].NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // Rebuild cached SKPath instances for bands (lazily allocated on demand)
            for (int i = 0; i < _cachedBandCount; i++)
            {
                var band = _cachedBands[i];
                var path = _cachedBandPaths[i] ??= new SKPath();
                path.Reset();
                path.MoveTo((float)band.Upper.P0.X, (float)band.Upper.P0.Y);
                path.CubicTo(
                    (float)band.Upper.P1.X, (float)band.Upper.P1.Y,
                    (float)band.Upper.P2.X, (float)band.Upper.P2.Y,
                    (float)band.Upper.P3.X, (float)band.Upper.P3.Y);

                if (band.WidthDip < minWidth)
                {
                    // Extension E07: For sub-pixel micro flows, expand lower boundary by delta to ensure minimum visible width
                    float delta = (float)(minWidth - band.WidthDip);
                    path.LineTo((float)band.LowerReverse.P0.X, (float)(band.LowerReverse.P0.Y + delta));
                    path.CubicTo(
                        (float)band.LowerReverse.P1.X, (float)(band.LowerReverse.P1.Y + delta),
                        (float)band.LowerReverse.P2.X, (float)(band.LowerReverse.P2.Y + delta),
                        (float)band.LowerReverse.P3.X, (float)(band.LowerReverse.P3.Y + delta));
                }
                else
                {
                    path.LineTo((float)band.LowerReverse.P0.X, (float)band.LowerReverse.P0.Y);
                    path.CubicTo(
                        (float)band.LowerReverse.P1.X, (float)band.LowerReverse.P1.Y,
                        (float)band.LowerReverse.P2.X, (float)band.LowerReverse.P2.Y,
                        (float)band.LowerReverse.P3.X, (float)band.LowerReverse.P3.Y);
                }
                path.Close();
            }
        }
        else
        {
            _cachedNodeCount = 0;
            _cachedBandCount = 0;
        }

        _lastLayoutRevision = snapshot.Revision;
        _lastLayoutSize = size;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_cachedNodeCount == 0 && _cachedBandCount == 0)
            return;

        _drawOperation.Bounds = new Rect(Bounds.Size);
        context.Custom(_drawOperation);
    }

    private void RenderSkia(SKCanvas canvas, Rect bounds)
    {
        // 1. Background
        if (_bgPaint.Color.Alpha > 0)
        {
            canvas.DrawRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height), _bgPaint);
        }

        // 2. Ribbon Bands in EdgeId order
        for (int i = 0; i < _cachedBandCount; i++)
        {
            if (_cachedBandPaths[i] is { } path)
            {
                canvas.DrawPath(path, _linkPaint);
            }
        }

        // 3. Node Rectangles
        int? focusedId = FocusedNodeId;
        for (int i = 0; i < _cachedNodeCount; i++)
        {
            var node = _cachedNodes[i];
            if (SankeyConstants.IsDummyNode(node.NodeId))
                continue;

            var rect = new SKRect((float)node.X0, (float)node.Y0, (float)node.X1, (float)node.Y1);
            canvas.DrawRect(rect, _nodePaint);

            // 4. Node Label (if node dimensions are sufficient)
            if (node.Width > SankeyConstants.MinLabelNodeWidthDip &&
                node.Height > (SankeyConstants.LabelFontSizeDip + SankeyConstants.MinLabelNodeHeightPaddingDip))
            {
                canvas.Save();
                canvas.ClipRect(rect);
                canvas.DrawText(
                    _cachedNodeLabels[i],
                    (float)(node.X0 + SankeyConstants.LabelPaddingXDip),
                    (float)(node.Y0 + SankeyConstants.LabelFontSizeDip + SankeyConstants.LabelPaddingYDip),
                    _textPaint);
                canvas.Restore();
            }

            // 5. Focus Border
            if (focusedId.HasValue && node.NodeId == focusedId.Value)
            {
                canvas.DrawRect(rect, _focusPaint);
            }
        }
    }

    public int? HitTestNode(Point p)
    {
        for (int i = 0; i < _cachedNodeCount; i++)
        {
            var node = _cachedNodes[i];
            if (SankeyConstants.IsDummyNode(node.NodeId))
                continue;

            if (p.X >= node.X0 && p.X <= node.X1 && p.Y >= node.Y0 && p.Y <= node.Y1)
            {
                return node.NodeId;
            }
        }
        return null;
    }

    public int? HitTestBand(Point p)
    {
        float x = (float)p.X;
        float y = (float)p.Y;
        float radius = (float)SankeyConstants.MinHitTestRadiusDip;

        for (int i = _cachedBandCount - 1; i >= 0; i--)
        {
            var path = _cachedBandPaths[i];
            if (path is null)
                continue;

            var bounds = path.Bounds;
            // Expand AABB check by hit-test radius for effortless targeting of micro-flows
            if (x >= bounds.Left - radius && x <= bounds.Right + radius &&
                y >= bounds.Top - radius && y <= bounds.Bottom + radius)
            {
                if (path.Contains(x, y))
                {
                    return SankeyConstants.GetOriginalEdgeId(_cachedBands[i].EdgeId);
                }

                // If pointer is within tolerance radius of a thin band, test nearby offsets
                if (_cachedBands[i].WidthDip <= 2.0 * SankeyConstants.MinHitTestRadiusDip)
                {
                    if (path.Contains(x, y - radius) || path.Contains(x, y + radius) ||
                        path.Contains(x - radius, y) || path.Contains(x + radius, y))
                    {
                        return SankeyConstants.GetOriginalEdgeId(_cachedBands[i].EdgeId);
                    }
                }
            }
        }
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);

        int? hitNode = HitTestNode(pt);
        if (hitNode.HasValue)
        {
            if (HoveredNodeId != hitNode || HoveredEdgeId != null)
            {
                HoveredNodeId = hitNode;
                HoveredEdgeId = null;
                InvalidateVisual();
            }
            return;
        }

        int? hitBand = HitTestBand(pt);
        if (hitBand.HasValue)
        {
            if (HoveredNodeId != null || HoveredEdgeId != hitBand)
            {
                HoveredNodeId = null;
                HoveredEdgeId = hitBand;
                InvalidateVisual();
            }
            return;
        }

        if (HoveredNodeId != null || HoveredEdgeId != null)
        {
            HoveredNodeId = null;
            HoveredEdgeId = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (HoveredNodeId != null || HoveredEdgeId != null)
        {
            HoveredNodeId = null;
            HoveredEdgeId = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var pt = e.GetPosition(this);
        int? hitNode = HitTestNode(pt);
        if (FocusedNodeId != hitNode)
        {
            FocusedNodeId = hitNode;
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            if (FocusedNodeId != null)
            {
                FocusedNodeId = null;
                InvalidateVisual();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down || e.Key == Key.Tab)
        {
            if (_cachedNodeCount > 0)
            {
                int currentIdx = -1;
                if (FocusedNodeId.HasValue)
                {
                    for (int i = 0; i < _cachedNodeCount; i++)
                    {
                        if (_cachedNodes[i].NodeId == FocusedNodeId.Value)
                        {
                            currentIdx = i;
                            break;
                        }
                    }
                }

                int nextIdx = (currentIdx + 1) % _cachedNodeCount;
                int stepCount = 0;
                while (stepCount < _cachedNodeCount && SankeyConstants.IsDummyNode(_cachedNodes[nextIdx].NodeId))
                {
                    nextIdx = (nextIdx + 1) % _cachedNodeCount;
                    stepCount++;
                }

                if (stepCount < _cachedNodeCount)
                {
                    FocusedNodeId = _cachedNodes[nextIdx].NodeId;
                    InvalidateVisual();
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Up)
        {
            if (_cachedNodeCount > 0)
            {
                int currentIdx = _cachedNodeCount;
                if (FocusedNodeId.HasValue)
                {
                    for (int i = 0; i < _cachedNodeCount; i++)
                    {
                        if (_cachedNodes[i].NodeId == FocusedNodeId.Value)
                        {
                            currentIdx = i;
                            break;
                        }
                    }
                }

                int prevIdx = (currentIdx - 1 + _cachedNodeCount) % _cachedNodeCount;
                int stepCount = 0;
                while (stepCount < _cachedNodeCount && SankeyConstants.IsDummyNode(_cachedNodes[prevIdx].NodeId))
                {
                    prevIdx = (prevIdx - 1 + _cachedNodeCount) % _cachedNodeCount;
                    stepCount++;
                }

                if (stepCount < _cachedNodeCount)
                {
                    FocusedNodeId = _cachedNodes[prevIdx].NodeId;
                    InvalidateVisual();
                    e.Handled = true;
                }
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Reset cached paths to release intermediate geometry memory
        for (int i = 0; i < _cachedBandCount; i++)
        {
            _cachedBandPaths[i]?.Reset();
        }
        _cachedNodeCount = 0;
        _cachedBandCount = 0;
        _lastLayoutRevision = -1;
    }

    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _layoutWorkspace.Dispose();
        _nodePaint.Dispose();
        _linkPaint.Dispose();
        _textPaint.Dispose();
        _focusPaint.Dispose();
        _bgPaint.Dispose();

        for (int i = 0; i < _cachedBandPaths.Length; i++)
        {
            _cachedBandPaths[i]?.Dispose();
            _cachedBandPaths[i] = null;
        }
    }

    private sealed class SankeyDrawOperation : ICustomDrawOperation
    {
        private readonly SankeyPlotControl _owner;

        public SankeyDrawOperation(SankeyPlotControl owner) => _owner = owner;

        public Rect Bounds { get; set; }

        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

        public override bool Equals(object? obj) => ReferenceEquals(this, obj);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                return;

            using var lease = feature.Lease();
            _owner.RenderSkia(lease.SkCanvas, Bounds);
        }
    }
}
