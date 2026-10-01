using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using AvaloniaPoint = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Backtest;

/// <summary>
/// Equity curve rendered from a key-addressed immutable managed snapshot. The visible time range is its own
/// <see cref="ViewportManager"/> (independent of the main chart): wheel zooms around the pointer, left-drag pans, double-click shows
/// the whole series again. The viewport is a cache-key element, so pan/zoom rebuild the snapshot and nothing else does.
/// </summary>
public sealed class EquityCurveControl : Control
{
    public static readonly StyledProperty<ImmutableArray<EquityPoint>> EquityPointsProperty =
        AvaloniaProperty.Register<EquityCurveControl, ImmutableArray<EquityPoint>>(
            nameof(EquityPoints), ImmutableArray<EquityPoint>.Empty);

    public static readonly StyledProperty<long> ResultRevisionProperty =
        AvaloniaProperty.Register<EquityCurveControl, long>(nameof(ResultRevision));

    public static readonly StyledProperty<BacktestMetricSemantic> LineSemanticProperty =
        AvaloniaProperty.Register<EquityCurveControl, BacktestMetricSemantic>(
            nameof(LineSemantic), BacktestMetricSemantic.Neutral);

    /// <summary>One flag per equity point (in drawdown or not); only the Drawdown color mode reads it. Its snapshot follows <see cref="ResultRevision"/>.</summary>
    public static readonly StyledProperty<ImmutableArray<bool>> UnderwaterFlagsProperty =
        AvaloniaProperty.Register<EquityCurveControl, ImmutableArray<bool>>(
            nameof(UnderwaterFlags), ImmutableArray<bool>.Empty);

    /// <summary>The user's color style; null keeps the legacy single color derived from <see cref="LineSemantic"/>.</summary>
    public static readonly StyledProperty<BacktestEquityLineStyle?> LineStyleProperty =
        AvaloniaProperty.Register<EquityCurveControl, BacktestEquityLineStyle?>(nameof(LineStyle));

    /// <summary>The trades whose entry/exit are marked on the curve. Its snapshot follows <see cref="ResultRevision"/>, like <see cref="EquityPoints"/>.</summary>
    public static readonly StyledProperty<ImmutableArray<BacktestTradeRow>> TradesProperty =
        AvaloniaProperty.Register<EquityCurveControl, ImmutableArray<BacktestTradeRow>>(
            nameof(Trades), ImmutableArray<BacktestTradeRow>.Empty);

    public static readonly StyledProperty<string?> EmptyStateTextProperty =
        AvaloniaProperty.Register<EquityCurveControl, string?>(nameof(EmptyStateText));

    public static readonly StyledProperty<string?> UnavailableTextProperty =
        AvaloniaProperty.Register<EquityCurveControl, string?>(nameof(UnavailableText));

    public static readonly StyledProperty<double> LabelFontSizeProperty =
        AvaloniaProperty.Register<EquityCurveControl, double>(nameof(LabelFontSize), 11d);

    static EquityCurveControl()
    {
        AffectsRender<EquityCurveControl>(
            EquityPointsProperty,
            ResultRevisionProperty,
            LineSemanticProperty,
            UnderwaterFlagsProperty,
            TradesProperty,
            LineStyleProperty,
            EmptyStateTextProperty,
            UnavailableTextProperty,
            LabelFontSizeProperty);
    }

    public ImmutableArray<BacktestTradeRow> Trades
    {
        get => GetValue(TradesProperty);
        set => SetValue(TradesProperty, value);
    }

    public ImmutableArray<EquityPoint> EquityPoints
    {
        get => GetValue(EquityPointsProperty);
        set => SetValue(EquityPointsProperty, value);
    }

    public long ResultRevision
    {
        get => GetValue(ResultRevisionProperty);
        set => SetValue(ResultRevisionProperty, value);
    }

    public BacktestMetricSemantic LineSemantic
    {
        get => GetValue(LineSemanticProperty);
        set => SetValue(LineSemanticProperty, value);
    }

    public ImmutableArray<bool> UnderwaterFlags
    {
        get => GetValue(UnderwaterFlagsProperty);
        set => SetValue(UnderwaterFlagsProperty, value);
    }

    public BacktestEquityLineStyle? LineStyle
    {
        get => GetValue(LineStyleProperty);
        set => SetValue(LineStyleProperty, value);
    }

    public string? EmptyStateText
    {
        get => GetValue(EmptyStateTextProperty);
        set => SetValue(EmptyStateTextProperty, value);
    }

    public string? UnavailableText
    {
        get => GetValue(UnavailableTextProperty);
        set => SetValue(UnavailableTextProperty, value);
    }

    public double LabelFontSize
    {
        get => GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    private IThemeManager? _themeManager;
    private IChartSettingsManager? _chartSettingsManager;
    private TopLevel? _topLevel;
    private ThemeColors _theme = ThemeColors.Dark;
    private long _themeRevision;
    private long _localeRevision;
    private CurveCacheKey? _cacheKey;
    private CurveRenderSnapshot? _snapshot;
    private readonly ViewportManager _viewport = new();
    private EquityDataBounds? _dataBounds;
    private bool _isDragging;
    private double _dragLastX;
    private ImmutableArray<EquityTradeMarker> _markers = ImmutableArray<EquityTradeMarker>.Empty;
    private ImmutableArray<EquityPoint> _markerPoints;
    private ImmutableArray<BacktestTradeRow> _markerTrades;
    private AvaloniaPoint? _lastPointer;
    private EquityHoverView? _hover;
    private HoverPalette? _hoverPalette;
    private HoverVisual? _hoverVisual;
    private bool _snapshotDirty;
    private bool _isRendering;

    private static readonly Typeface LabelTypeface = new(FontFamily.Default);

    public EquityCurveControl()
    {
        _viewport.ViewportChanged += OnViewportChanged;
    }

    internal int SnapshotBuildCount { get; private set; }

    /// <summary>The visible time range as (start, end) UTC ticks; the whole data span when the series is not zoomable (test observability).</summary>
    internal (long StartTicks, long EndTicks) VisibleTicks => _dataBounds is { } bounds
        ? EquityViewportBinder.ResolveVisibleTicks(_viewport.VisibleStartUnit, _viewport.VisibleEndUnit, bounds)
        : default;

    /// <summary>Whether wheel/drag/double-click act on the current series (two or more valid points) (test observability).</summary>
    internal bool IsViewportInteractive => _dataBounds is not null;

    /// <summary>The trade events placed on the current series, parallel to <see cref="RenderedMarkerPlacements"/>' MarkerIndex (test observability).</summary>
    internal ImmutableArray<EquityTradeMarker> Markers => _markers;

    /// <summary>Screen positions of the markers inside the cached snapshot's visible range (test observability).</summary>
    internal ImmutableArray<EquityMarkerPlacement> RenderedMarkerPlacements => _snapshot?.Hover?.Placements ?? ImmutableArray<EquityMarkerPlacement>.Empty;

    /// <summary>Text and top-left origin of every axis label of the cached snapshot (test observability).</summary>
    internal ImmutableArray<(string Text, AvaloniaPoint Origin, double Width)> RenderedAxisLabels => _snapshot?.AxisLabels ?? ImmutableArray<(string, AvaloniaPoint, double)>.Empty;

    /// <summary>What the pointer currently hovers (crosshair point or trade marker), or null (test observability).</summary>
    internal EquityHoverView? ActiveHover => _hover;

    /// <summary>Where the cached hover value panel is drawn, or null when no hover visual is built (test observability).</summary>
    internal Rect? HoverPanelRect => _hoverVisual?.PanelRect;

    /// <summary>Font size the hover value panel is drawn with: Settings &gt; Fonts &gt; Tooltip Font Size (test observability).</summary>
    internal double HoverPanelFontSize => ResolveTooltipFontSize();

    /// <summary>The equity series the currently cached snapshot was built from (test observability, same role as <see cref="SnapshotBuildCount"/>).</summary>
    internal ImmutableArray<EquityPoint> RenderedEquityPoints { get; private set; } = ImmutableArray<EquityPoint>.Empty;

    /// <summary>Number of up/down figures of the cached snapshot; both zero when the curve is drawn as one single-color line (test observability).</summary>
    internal (int Up, int Down) RenderedSegmentFigureCounts { get; private set; }

    /// <summary>Whether the cached snapshot draws the curve as separate up and down geometries (test observability).</summary>
    internal bool RenderedIsSegmented { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _themeManager = App.Current?.Services?.GetService<IThemeManager>();
        if (_themeManager is not null)
        {
            _theme = _themeManager.CurrentTheme;
            _themeManager.PropertyChanged += OnThemeManagerPropertyChanged;
        }
        _chartSettingsManager = App.Current?.Services?.GetService<IChartSettingsManager>();
        if (_chartSettingsManager is not null)
        {
            _chartSettingsManager.SettingsChanged += OnChartSettingsChanged;
            SetCurrentValue(LineStyleProperty, BacktestEquityLineStyle.FromSettings(_chartSettingsManager.Current));
        }
        LocalizationManager.Instance.LocaleChanged += OnLocaleChanged;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
        }
        RefreshSnapshot();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationManager.Instance.LocaleChanged -= OnLocaleChanged;
        if (_chartSettingsManager is not null)
        {
            _chartSettingsManager.SettingsChanged -= OnChartSettingsChanged;
            _chartSettingsManager = null;
        }
        if (_themeManager is not null)
        {
            _themeManager.PropertyChanged -= OnThemeManagerPropertyChanged;
            _themeManager = null;
        }
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        if (_dataBounds is not null && TryCreatePlot(arranged, out EquityPlotArea plot))
        {
            Rect plotBounds = ToRect(plot);
            if (_viewport.ScreenBounds != plotBounds)
            {
                EquityViewportBinder.Resize(_viewport, plotBounds);
            }
        }
        RefreshSnapshot(arranged);
        return arranged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EquityPointsProperty || change.Property == ResultRevisionProperty)
        {
            // A new result is shown whole: pan/zoom of the previous series must not carry over.
            ResetViewportToData();
        }

        if (change.Property == EquityPointsProperty || change.Property == TradesProperty || change.Property == ResultRevisionProperty)
        {
            RebuildMarkers();
        }

        if (change.Property == EquityPointsProperty ||
            change.Property == TradesProperty ||
            change.Property == ResultRevisionProperty ||
            change.Property == LineSemanticProperty ||
            change.Property == UnderwaterFlagsProperty ||
            change.Property == LineStyleProperty ||
            change.Property == EmptyStateTextProperty ||
            change.Property == UnavailableTextProperty ||
            change.Property == LabelFontSizeProperty)
        {
            RefreshSnapshot();
        }
    }

    private void ResetViewportToData()
    {
        EndDrag();
        if (!EquityViewportBinder.TryResolveDataBounds(EquityPoints, out EquityDataBounds bounds))
        {
            _dataBounds = null;
            return;
        }

        _dataBounds = bounds;
        Rect plotBounds = TryCreatePlot(Bounds.Size, out EquityPlotArea plot) ? ToRect(plot) : default;
        EquityViewportBinder.Reset(_viewport, bounds, plotBounds);
    }

    private void RebuildMarkers()
    {
        if (_markerPoints == EquityPoints && _markerTrades == Trades)
        {
            return;
        }
        _markerPoints = EquityPoints;
        _markerTrades = Trades;
        _markers = EquityTradeMarkers.Build(EquityPoints, Trades);
        _hover = null;
        _hoverVisual = null;
    }

    // Pan and zoom events arrive faster than frames: only note that the view moved. The next paint rebuilds the snapshot once.
    private void OnViewportChanged(object? sender, EventArgs e)
    {
        _snapshotDirty = true;
        InvalidateVisual();
    }

    // ---- hover (crosshair + value panel, trade marker panel): overlay state only, never part of the snapshot or its cache key ----

    /// <summary>Re-resolves what the last known pointer position hovers; null when the pointer is gone, dragging, or over nothing.</summary>
    private void UpdateHover()
    {
        EquityHoverView? next = _isDragging || _lastPointer is not { } pointer ? null : ResolveHover(pointer);
        if (next != _hover)
        {
            _hover = next;
            _hoverVisual = null;
            if (!_isRendering)
            {
                InvalidateVisual();
            }
        }
    }

    private EquityHoverView? ResolveHover(AvaloniaPoint pointer)
    {
        if (_snapshot?.Hover is not { } context || !context.Plot.Contains(pointer.X, pointer.Y))
        {
            return null;
        }

        EquityMarkerPlacement? marker = EquityTradeMarkers.FindNearest(
            context.Placements, pointer.X, pointer.Y, BacktestUiConstants.EquityMarkerHitTolerance);
        if (marker is { } hit)
        {
            return new EquityHoverView(EquityHoverKind.Marker, hit.MarkerIndex, new AvaloniaPoint(hit.X, hit.Y));
        }

        ImmutableArray<EquityPoint> points = EquityPoints;
        int index = 0;
        if (points.Length > 1)
        {
            // A pointer-to-time conversion is only as exact as one pixel: allow that much outside the data span.
            double pixelsPerUnit = _viewport.PixelsPerUnit;
            long tolerance = pixelsPerUnit > 0d ? (long)Math.Ceiling(1d / pixelsPerUnit) : 0L;
            long ticks = (long)Math.Clamp(Math.Round(_viewport.ScreenXToUnit(pointer.X)), DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
            index = EquityNearestPoint.FindIndex(points, ticks, tolerance);
        }
        else if (points.Length == 0)
        {
            return null;
        }
        if (index < 0)
        {
            return null;
        }

        EquityCurveNormalizedPoint normalized = EquityCurveLayout.Normalize(
            points[index], context.StartTicks, context.EndTicks, context.YMin, context.YMax);
        (double x, double y) = context.Plot.ToScreen(normalized);
        // The nearest point can be a bracketing neighbour just outside the plot (a range between two points): there is nothing to snap to.
        return x < context.Plot.Left || x > context.Plot.Right
            ? null
            : new EquityHoverView(EquityHoverKind.Point, index, new AvaloniaPoint(x, y));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_dataBounds is null || !TryCreatePlot(Bounds.Size, out EquityPlotArea plot))
        {
            return;
        }

        AvaloniaPoint position = e.GetPosition(this);
        double factor = EquityViewportBinder.ZoomFactor(e.Delta.Y);
        if (!plot.Contains(position.X, position.Y) || factor == 1d)
        {
            return;
        }

        double start = _viewport.VisibleStartUnit;
        double end = _viewport.VisibleEndUnit;
        _viewport.Zoom(factor, position.X);
        // A wheel step that cannot zoom any further (whole series already shown) is left to the page's own scrolling.
        e.Handled = start != _viewport.VisibleStartUnit || end != _viewport.VisibleEndUnit;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_dataBounds is not { } bounds || !TryCreatePlot(Bounds.Size, out EquityPlotArea plot))
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || !plot.Contains(point.Position.X, point.Position.Y))
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            EndDrag();
            EquityViewportBinder.ShowAll(_viewport, bounds);
            OnViewportChanged(this, EventArgs.Empty);
            return;
        }

        _isDragging = true;
        _dragLastX = point.Position.X;
        UpdateHover();
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _lastPointer = e.GetPosition(this);
        if (!_isDragging)
        {
            UpdateHover();
            return;
        }

        double x = e.GetPosition(this).X;
        double delta = x - _dragLastX;
        _dragLastX = x;
        if (delta != 0d)
        {
            _viewport.Pan(delta);
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            EndDrag();
            e.Pointer.Capture(null);
            e.Handled = true;
            _lastPointer = e.GetPosition(this);
            UpdateHover();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastPointer = null;
        UpdateHover();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag() => _isDragging = false;

    private static bool TryCreatePlot(Size size, out EquityPlotArea plot)
    {
        plot = new EquityPlotArea(
            BacktestUiConstants.PlotAxisMarginLeft,
            BacktestUiConstants.PlotAxisMarginTop,
            size.Width - BacktestUiConstants.PlotAxisMarginRight,
            size.Height - BacktestUiConstants.PlotAxisMarginBottom);
        return plot.IsDrawable;
    }

    private static Rect ToRect(EquityPlotArea plot) => new(plot.Left, plot.Top, plot.Width, plot.Height);

    private void OnThemeManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _theme = _themeManager?.CurrentTheme ?? ThemeColors.Dark;
            _themeRevision++;
            _hoverPalette = null;
            _hoverVisual = null;
            RefreshSnapshot();
            InvalidateVisual();
        });
    }

    // SettingsChanged also fires for unrelated settings and from arbitrary threads: marshal to the UI thread and rebuild only
    // when the resolved style really differs.
    private void OnChartSettingsChanged() => Dispatcher.UIThread.Post(ApplyChartSettings);

    private void ApplyChartSettings()
    {
        if (_chartSettingsManager is null) return;
        BacktestEquityLineStyle style = BacktestEquityLineStyle.FromSettings(_chartSettingsManager.Current);
        if (!Equals(style, LineStyle))
        {
            SetCurrentValue(LineStyleProperty, style);
        }
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        RefreshSnapshot();
        InvalidateVisual();
    }

    private void OnLocaleChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnLocaleChanged(sender, e));
            return;
        }
        _localeRevision++;
        _hoverVisual = null;
        RefreshSnapshot();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_snapshotDirty)
        {
            _isRendering = true;
            try
            {
                RefreshSnapshot();
            }
            finally
            {
                _isRendering = false;
            }
        }
        _snapshot?.Draw(context);
        if (_hover is { } hover && _snapshot?.Hover is { } hoverContext)
        {
            DrawHover(context, hover, hoverContext);
        }
    }

    /// <summary>The brushes and pens of the hover overlay (the main chart's crosshair look), cached per theme.</summary>
    private sealed record HoverPalette(IBrush Box, IBrush Text, IPen BoxPen, IPen DashedPen);

    /// <summary>
    /// Everything the hover overlay draws for one hover state, built once when the hover changes (not per paint): the axis label boxes of a
    /// point hover and the value panel, with their final positions. Rebuilt when the control size or the tooltip font size changes.
    /// </summary>
    private sealed record HoverVisual(
        EquityHoverView Hover,
        Size Bounds,
        double PanelFontSize,
        FormattedText? TimeText,
        AvaloniaPoint TimeOrigin,
        FormattedText? EquityText,
        AvaloniaPoint EquityOrigin,
        FormattedText PanelText,
        Rect PanelRect);

    private HoverPalette GetHoverPalette()
    {
        if (_hoverPalette is null)
        {
            IBrush lineBrush = new ImmutableSolidColorBrush(_theme.CrosshairLineAlpha.ToAvaloniaColor());
            IBrush textBrush = new ImmutableSolidColorBrush(_theme.CrosshairText.ToAvaloniaColor());
            _hoverPalette = new HoverPalette(
                new ImmutableSolidColorBrush(_theme.ChartBackground.ToAvaloniaColor()),
                textBrush,
                new Pen(textBrush, BacktestUiConstants.EquityHairlineWidth),
                new Pen(
                    lineBrush,
                    BacktestUiConstants.EquityHairlineWidth,
                    new DashStyle(new[] { BacktestUiConstants.EquityCrosshairDashLength, BacktestUiConstants.EquityCrosshairDashLength }, 0d)));
        }
        return _hoverPalette;
    }

    /// <summary>
    /// The hover overlay on top of the cached snapshot: a point hover draws a dashed crosshair at the snapped point, its time/Equity labels
    /// on the axes and the value panel; a marker hover rings the marker and shows the trade panel. Only draws what
    /// <see cref="GetHoverVisual"/> prepared.
    /// </summary>
    private void DrawHover(DrawingContext context, EquityHoverView hover, EquityHoverContext hoverContext)
    {
        HoverVisual? visual = GetHoverVisual(hover, hoverContext);
        if (visual is null)
        {
            return;
        }

        HoverPalette palette = GetHoverPalette();
        EquityPlotArea plot = hoverContext.Plot;
        if (hover.Kind == EquityHoverKind.Point)
        {
            context.DrawLine(palette.DashedPen, new AvaloniaPoint(hover.Anchor.X, plot.Top), new AvaloniaPoint(hover.Anchor.X, plot.Bottom));
            context.DrawLine(palette.DashedPen, new AvaloniaPoint(plot.Left, hover.Anchor.Y), new AvaloniaPoint(plot.Right, hover.Anchor.Y));
            DrawLabelBox(context, palette, visual.TimeText!, visual.TimeOrigin);
            DrawLabelBox(context, palette, visual.EquityText!, visual.EquityOrigin);
        }
        else
        {
            double ring = BacktestUiConstants.EquityMarkerRadius + BacktestUiConstants.EquityHairlineWidth;
            context.DrawEllipse(null, palette.BoxPen, hover.Anchor, ring, ring);
        }

        context.DrawRectangle(
            palette.Box, palette.BoxPen, visual.PanelRect, BacktestUiConstants.EquityHoverCornerRadius, BacktestUiConstants.EquityHoverCornerRadius);
        context.DrawText(
            visual.PanelText,
            new AvaloniaPoint(visual.PanelRect.X + BacktestUiConstants.EquityHoverPadding, visual.PanelRect.Y + BacktestUiConstants.EquityHoverPadding));
    }

    private HoverVisual? GetHoverVisual(EquityHoverView hover, EquityHoverContext hoverContext)
    {
        double panelFontSize = ResolveTooltipFontSize();
        if (_hoverVisual is { } cached && cached.Hover == hover && cached.Bounds == Bounds.Size && cached.PanelFontSize == panelFontSize)
        {
            return cached;
        }

        _hoverVisual = BuildHoverVisual(hover, hoverContext, panelFontSize);
        return _hoverVisual;
    }

    private HoverVisual? BuildHoverVisual(EquityHoverView hover, EquityHoverContext hoverContext, double panelFontSize)
    {
        HoverPalette palette = GetHoverPalette();
        EquityPlotArea plot = hoverContext.Plot;
        double controlWidth = Bounds.Width;
        double controlHeight = Bounds.Height;
        double padding = BacktestUiConstants.EquityHoverPadding;
        FormattedText? timeText = null;
        FormattedText? equityText = null;
        AvaloniaPoint timeOrigin = default;
        AvaloniaPoint equityOrigin = default;
        ImmutableArray<string> lines;

        if (hover.Kind == EquityHoverKind.Point)
        {
            if (hover.Index < 0 || hover.Index >= EquityPoints.Length)
            {
                return null;
            }
            EquityPoint point = EquityPoints[hover.Index];

            // The time label stays left of the Y label column; the Equity label sits in that column and is cut to its width.
            timeText = LabelText(EquityHoverText.AxisTime(point.Timestamp), LabelFontSize, palette.Text);
            double timeX = Math.Clamp(hover.Anchor.X - (timeText.Width / 2d), 0d, Math.Max(0d, plot.Right - timeText.Width));
            timeOrigin = new AvaloniaPoint(timeX, plot.Bottom + BacktestUiConstants.ChartLabelGap);

            double equityX = plot.Right + BacktestUiConstants.ChartLabelGap;
            string equityLabel = EquityTextFit.Truncate(
                EquityHoverText.AxisEquity(point.Equity), Math.Max(0d, controlWidth - equityX), text => LabelText(text, LabelFontSize, palette.Text).Width);
            equityText = LabelText(equityLabel, LabelFontSize, palette.Text);
            equityOrigin = new AvaloniaPoint(
                equityX, Math.Clamp(hover.Anchor.Y - (equityText.Height / 2d), 0d, Math.Max(0d, controlHeight - equityText.Height)));

            lines = EquityHoverText.PointLines(point, LocalizationManager.Instance.Get);
        }
        else
        {
            if (hover.Index < 0 || hover.Index >= _markers.Length)
            {
                return null;
            }
            EquityTradeMarker marker = _markers[hover.Index];
            ImmutableArray<BacktestTradeRow> trades = Trades;
            if (marker.TradeIndex < 0 || marker.TradeIndex >= trades.Length)
            {
                return null;
            }
            lines = EquityHoverText.MarkerLines(trades[marker.TradeIndex], marker.Kind, marker.IsForcedLiquidation, LocalizationManager.Instance.Get);
        }

        // A line wider than the control is cut with an ellipsis instead of leaving the control.
        double maxLineWidth = Math.Max(0d, controlWidth - (2d * padding));
        var fitted = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            fitted[i] = EquityTextFit.Truncate(lines[i], maxLineWidth, text => LabelText(text, panelFontSize, palette.Text).Width);
        }
        FormattedText panelText = LabelText(string.Join('\n', fitted), panelFontSize, palette.Text);
        double panelWidth = panelText.Width + (2d * padding);
        double panelHeight = panelText.Height + (2d * padding);
        // To the lower right of the pointer; flipped to the other side where it would leave the control.
        double panelX = hover.Anchor.X + BacktestUiConstants.EquityHoverPointerOffset;
        if (panelX + panelWidth > controlWidth) panelX = hover.Anchor.X - BacktestUiConstants.EquityHoverPointerOffset - panelWidth;
        double panelY = hover.Anchor.Y + BacktestUiConstants.EquityHoverPointerOffset;
        if (panelY + panelHeight > controlHeight) panelY = hover.Anchor.Y - BacktestUiConstants.EquityHoverPointerOffset - panelHeight;
        panelX = Math.Clamp(panelX, 0d, Math.Max(0d, controlWidth - panelWidth));
        panelY = Math.Clamp(panelY, 0d, Math.Max(0d, controlHeight - panelHeight));

        return new HoverVisual(
            hover, Bounds.Size, panelFontSize, timeText, timeOrigin, equityText, equityOrigin, panelText,
            new Rect(panelX, panelY, panelWidth, panelHeight));
    }

    private static FormattedText LabelText(string text, double fontSize, IBrush brush) =>
        new(text, EquityLabelFormats.Culture, FlowDirection.LeftToRight, LabelTypeface, fontSize, brush);

    private static void DrawLabelBox(DrawingContext context, HoverPalette palette, FormattedText text, AvaloniaPoint origin)
    {
        double padding = BacktestUiConstants.EquityHoverPadding / 2d;
        var rect = new Rect(origin.X - padding, origin.Y - padding, text.Width + (2d * padding), text.Height + (2d * padding));
        context.DrawRectangle(palette.Box, palette.BoxPen, rect, BacktestUiConstants.EquityHoverCornerRadius, BacktestUiConstants.EquityHoverCornerRadius);
        context.DrawText(text, origin);
    }

    // Settings > Fonts > Tooltip Font Size lives in the application resources (FontSettingsManager.UpdateAppResource); it is compared on
    // every paint (no allocation) so a change rebuilds the cached panel.
    private double ResolveTooltipFontSize() =>
        this.TryFindResource(TooltipFontSizeResourceKey, ActualThemeVariant, out object? value) && value is double size && IsFinitePositive(size)
            ? size
            : FontDefaults.Tooltip;

    private const string TooltipFontSizeResourceKey = "TooltipFontSize";

    private void RefreshSnapshot(Size? sizeOverride = null)
    {
        _snapshotDirty = false;
        double renderScaling = _topLevel?.RenderScaling ?? VisualRoot?.RenderScaling ?? 1d;
        string localeRevision = CultureInfo.CurrentCulture.Name;
        var key = new CurveCacheKey(
            ResultRevision,
            sizeOverride ?? Bounds.Size,
            renderScaling,
            _themeRevision,
            LabelFontSize,
            localeRevision,
            _localeRevision,
            EmptyStateText,
            UnavailableText,
            LineSemantic,
            LineStyle,
            _dataBounds is null ? 0d : _viewport.VisibleStartUnit,
            _dataBounds is null ? 0d : _viewport.VisibleEndUnit);

        if (_cacheKey != key)
        {
            _snapshot = BuildSnapshot(key);
            RenderedEquityPoints = EquityPoints;
            RenderedSegmentFigureCounts = _snapshot?.SegmentFigureCounts ?? default;
            RenderedIsSegmented = _snapshot?.IsSegmented ?? false;
            _cacheKey = key;
            SnapshotBuildCount++;
            // Anchors and marker positions belong to the old snapshot: resolve the pointer against the new one.
            UpdateHover();
        }
    }

    private CurveRenderSnapshot? BuildSnapshot(CurveCacheKey key)
    {
        if (!IsFinitePositive(key.Bounds.Width) || !IsFinitePositive(key.Bounds.Height) ||
            !IsFinitePositive(key.RenderScaling) || !IsFinitePositive(key.LabelFontSize))
        {
            return null;
        }

        if (!TryCreatePlot(key.Bounds, out EquityPlotArea plot)) return null;

        // Numbers and dates are invariant (EquityLabelFormats); only the localized empty/unavailable message uses the UI culture.
        long visibleStartTicks = 0;
        long visibleEndTicks = 0;
        bool hasVisibleRange = _dataBounds is not null;
        EquityCurveLayout layout;
        if (_dataBounds is { } dataBounds)
        {
            (visibleStartTicks, visibleEndTicks) =
                EquityViewportBinder.ResolveVisibleTicks(key.VisibleStartUnit, key.VisibleEndUnit, dataBounds);
            // _dataBounds proves the series valid (EquitySeriesRule), once per result: a pan costs only the visible slice.
            layout = EquityCurveLayout.BuildValidated(EquityPoints, visibleStartTicks, visibleEndTicks);
        }
        else
        {
            layout = EquityCurveLayout.Build(EquityPoints);
        }
        IBrush background = new ImmutableSolidColorBrush(_theme.ChartBackground.ToAvaloniaColor());
        IBrush labelBrush = new ImmutableSolidColorBrush(_theme.AxisText.ToAvaloniaColor());
        IBrush messageBrush = new ImmutableSolidColorBrush(_theme.ShellSecondaryText.ToAvaloniaColor());
        IBrush gridBrush = new ImmutableSolidColorBrush(_theme.GridLine.ToAvaloniaColor());
        BacktestEquityLineStyle? style = key.LineStyle;
        IBrush lineBrush = new ImmutableSolidColorBrush((style?.LineColor ?? ResolveLineColor(key.LineSemantic)).ToAvaloniaColor());
        var gridPen = new Pen(gridBrush, BacktestUiConstants.EquityHairlineWidth);
        var linePen = new Pen(lineBrush, BacktestUiConstants.EquityLineWidth);
        Typeface typeface = LabelTypeface;

        if (layout.State is EquityCurveDisplayState.Empty or EquityCurveDisplayState.Unavailable)
        {
            string? message = layout.State == EquityCurveDisplayState.Empty ? EmptyStateText : UnavailableText;
            FormattedText? formatted = string.IsNullOrEmpty(message)
                ? null
                : new FormattedText(message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, key.LabelFontSize, messageBrush);
            AvaloniaPoint messagePoint = formatted is null
                ? default
                : new AvaloniaPoint((key.Bounds.Width - formatted.Width) / 2d, (key.Bounds.Height - formatted.Height) / 2d);
            return CurveRenderSnapshot.Message(key.Bounds, background, formatted, messagePoint);
        }

        var gridLines = ImmutableArray.CreateBuilder<EquityGridLine>();
        var labels = ImmutableArray.CreateBuilder<EquityTextPlacement>();
        var labelStyle = new EquityLabelStyle(typeface, key.LabelFontSize, labelBrush);
        EquityAxisLabels.AddYAxis(gridLines, labels, layout, plot, key.Bounds.Width, labelStyle);
        if (layout.State == EquityCurveDisplayState.SinglePoint || !hasVisibleRange ||
            !EquityAxisLabels.AddXAxis(gridLines, labels, plot, visibleStartTicks, visibleEndTicks, labelStyle))
        {
            // One point has no time span to graduate, and a range too short for any calendar step keeps the two edge labels.
            EquityAxisLabels.AddEdgeLabels(labels, layout, plot, labelStyle);
        }

        StreamGeometry? geometry = null;
        SegmentGeometries? segments = null;
        AvaloniaPoint? singlePoint = null;
        if (layout.State == EquityCurveDisplayState.SinglePoint)
        {
            singlePoint = ToPixel(plot, layout.Points[0]);
        }
        else if (style is not null && layout.Points.Length >= 2 &&
                 EquityLineSegmentRule.IsSegmented(style.Mode, EquityPoints.Length, UnderwaterFlags))
        {
            segments = BuildSegmentGeometries(layout, style, EquityPoints, UnderwaterFlags, plot);
        }
        else
        {
            geometry = new StreamGeometry();
            using StreamGeometryContext geometryContext = geometry.Open();
            for (int i = 0; i < layout.Points.Length; i++)
            {
                AvaloniaPoint pixel = ToPixel(plot, layout.Points[i]);
                if (i == 0) geometryContext.BeginFigure(pixel, isFilled: false);
                else geometryContext.LineTo(pixel);
            }
            geometryContext.EndFigure(isClosed: false);
        }

        // The curve is clipped to the plot (a zoomed view draws neighbours outside it), a little wider than the plot so the line's own
        // thickness and the single-point dot are not cut at the plot edge.
        Rect curveClip = ToRect(plot).Inflate(Math.Max(BacktestUiConstants.EquityLineWidth, BacktestUiConstants.EquityPointRadius));
        // A one-point or non-zoomable series has no viewport range: its own time span is the range its fractions are relative to.
        long projectionStart = hasVisibleRange ? visibleStartTicks : EquityPoints[0].Timestamp.Ticks;
        long projectionEnd = hasVisibleRange ? visibleEndTicks : EquityPoints[^1].Timestamp.Ticks;
        EquityMarkerLayer? markerLayer = EquityMarkerLayer.Build(_markers, EquityPoints, layout, plot, projectionStart, projectionEnd, _theme);
        var hover = new EquityHoverContext(
            plot, projectionStart, projectionEnd, layout.YMin, layout.YMax,
            markerLayer?.Placements ?? ImmutableArray<EquityMarkerPlacement>.Empty);
        return CurveRenderSnapshot.Chart(
            key.Bounds,
            background,
            gridPen,
            linePen,
            lineBrush,
            gridLines.ToImmutable(),
            curveClip,
            geometry,
            segments,
            singlePoint,
            // A one-point series (start == end) draws a single X label, so the builder is not full: MoveToImmutable would throw.
            labels.ToImmutable(),
            markerLayer,
            hover);
    }

    private static AvaloniaPoint ToPixel(EquityPlotArea plot, EquityCurveNormalizedPoint point)
    {
        (double x, double y) = plot.ToScreen(point);
        return new AvaloniaPoint(x, y);
    }

    /// <summary>
    /// Two geometries (up, down), one figure per maximal run of same-class segments. Drawn up first, then down, so the down color wins
    /// on the shared vertex between runs. The class of segment k comes only from points k-1 and k (no look-ahead).
    /// </summary>
    private static SegmentGeometries BuildSegmentGeometries(
        EquityCurveLayout layout,
        BacktestEquityLineStyle style,
        ImmutableArray<EquityPoint> points,
        ImmutableArray<bool> flags,
        EquityPlotArea plot)
    {
        var up = new StreamGeometry();
        var down = new StreamGeometry();
        int upFigures = 0;
        int downFigures = 0;
        using (StreamGeometryContext upContext = up.Open())
        using (StreamGeometryContext downContext = down.Open())
        {
            StreamGeometryContext? open = null;
            bool runIsUp = false;
            for (int k = 1; k < layout.Points.Length; k++)
            {
                // Drawn point k is source point FirstIndex + k, so the rule (and its flags) read the same absolute points at any zoom.
                bool isUp = EquityLineSegmentRule.IsUpSegment(style.Mode, points, flags, layout.FirstIndex + k);
                if (open is null || isUp != runIsUp)
                {
                    open?.EndFigure(isClosed: false);
                    open = isUp ? upContext : downContext;
                    runIsUp = isUp;
                    if (isUp) upFigures++;
                    else downFigures++;
                    open.BeginFigure(ToPixel(plot, layout.Points[k - 1]), isFilled: false);
                }
                open.LineTo(ToPixel(plot, layout.Points[k]));
            }
            open?.EndFigure(isClosed: false);
        }

        return new SegmentGeometries(
            up,
            down,
            new Pen(new ImmutableSolidColorBrush(style.UpColor.ToAvaloniaColor()), BacktestUiConstants.EquityLineWidth),
            new Pen(new ImmutableSolidColorBrush(style.DownColor.ToAvaloniaColor()), BacktestUiConstants.EquityLineWidth),
            upFigures,
            downFigures);
    }

    private StockAnalyzer.Core.Models.IndicatorColor ResolveLineColor(BacktestMetricSemantic semantic) => semantic switch
    {
        BacktestMetricSemantic.Plus => _theme.SemanticPlus,
        BacktestMetricSemantic.Minus => _theme.SemanticMinus,
        _ => _theme.SemanticNeutral,
    };

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0d;

    private readonly record struct CurveCacheKey(
        long ResultRevision,
        Size Bounds,
        double RenderScaling,
        long ThemeRevision,
        double LabelFontSize,
        string LocaleRevision,
        long LocaleChangeRevision,
        string? EmptyStateText,
        string? UnavailableText,
        BacktestMetricSemantic LineSemantic,
        BacktestEquityLineStyle? LineStyle,
        double VisibleStartUnit,
        double VisibleEndUnit);

    private sealed record SegmentGeometries(
        StreamGeometry Up,
        StreamGeometry Down,
        IPen UpPen,
        IPen DownPen,
        int UpFigureCount,
        int DownFigureCount);

    private sealed class CurveRenderSnapshot
    {
        private readonly Size _bounds;
        private readonly IBrush _background;
        private readonly FormattedText? _message;
        private readonly AvaloniaPoint _messagePoint;
        private readonly IPen? _gridPen;
        private readonly IPen? _linePen;
        private readonly IBrush? _lineBrush;
        private readonly ImmutableArray<EquityGridLine> _gridLines;
        private readonly Rect _curveClip;
        private readonly StreamGeometry? _geometry;
        private readonly SegmentGeometries? _segments;
        private readonly AvaloniaPoint? _singlePoint;
        private readonly ImmutableArray<EquityTextPlacement> _labels;
        private readonly EquityMarkerLayer? _markers;

        private CurveRenderSnapshot(
            Size bounds,
            IBrush background,
            FormattedText? message,
            AvaloniaPoint messagePoint,
            IPen? gridPen,
            IPen? linePen,
            IBrush? lineBrush,
            ImmutableArray<EquityGridLine> gridLines,
            Rect curveClip,
            StreamGeometry? geometry,
            SegmentGeometries? segments,
            AvaloniaPoint? singlePoint,
            ImmutableArray<EquityTextPlacement> labels,
            EquityMarkerLayer? markers,
            EquityHoverContext? hover)
        {
            _bounds = bounds;
            _background = background;
            _message = message;
            _messagePoint = messagePoint;
            _gridPen = gridPen;
            _linePen = linePen;
            _lineBrush = lineBrush;
            _gridLines = gridLines;
            _curveClip = curveClip;
            _geometry = geometry;
            _segments = segments;
            _singlePoint = singlePoint;
            _labels = labels;
            _markers = markers;
            Hover = hover;
        }

        /// <summary>The geometry the hover overlay resolves the pointer against (plot, visible range, Y range, marker positions); null for a message snapshot.</summary>
        public EquityHoverContext? Hover { get; }

        public static CurveRenderSnapshot Message(Size bounds, IBrush background, FormattedText? message, AvaloniaPoint messagePoint) =>
            new(bounds, background, message, messagePoint, null, null, null, ImmutableArray<EquityGridLine>.Empty, default, null, null, null, ImmutableArray<EquityTextPlacement>.Empty, null, null);

        public ImmutableArray<(string Text, AvaloniaPoint Origin, double Width)> AxisLabels
        {
            get
            {
                var result = ImmutableArray.CreateBuilder<(string Text, AvaloniaPoint Origin, double Width)>(_labels.Length);
                for (int i = 0; i < _labels.Length; i++)
                {
                    result.Add((_labels[i].Content, _labels[i].Origin, _labels[i].Text.Width));
                }
                return result.MoveToImmutable();
            }
        }

        public bool IsSegmented => _segments is not null;

        public (int Up, int Down) SegmentFigureCounts => _segments is null ? default : (_segments.UpFigureCount, _segments.DownFigureCount);

        public static CurveRenderSnapshot Chart(
            Size bounds,
            IBrush background,
            IPen gridPen,
            IPen linePen,
            IBrush lineBrush,
            ImmutableArray<EquityGridLine> gridLines,
            Rect curveClip,
            StreamGeometry? geometry,
            SegmentGeometries? segments,
            AvaloniaPoint? singlePoint,
            ImmutableArray<EquityTextPlacement> labels,
            EquityMarkerLayer? markers,
            EquityHoverContext hover) =>
            new(bounds, background, null, default, gridPen, linePen, lineBrush, gridLines, curveClip, geometry, segments, singlePoint, labels, markers, hover);

        public void Draw(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(_bounds));
            if (_message is not null)
            {
                context.DrawText(_message, _messagePoint);
                return;
            }

            for (int i = 0; i < _gridLines.Length; i++)
            {
                context.DrawLine(_gridPen!, _gridLines[i].Start, _gridLines[i].End);
            }
            using (context.PushClip(_curveClip))
            {
                if (_singlePoint is { } point)
                {
                    context.DrawEllipse(_lineBrush, null, point, BacktestUiConstants.EquityPointRadius, BacktestUiConstants.EquityPointRadius);
                }
                else if (_segments is not null)
                {
                    context.DrawGeometry(null, _segments.UpPen, _segments.Up);
                    context.DrawGeometry(null, _segments.DownPen, _segments.Down);
                }
                else if (_geometry is not null)
                {
                    context.DrawGeometry(null, _linePen, _geometry);
                }
            }
            _markers?.Draw(context);
            for (int i = 0; i < _labels.Length; i++)
            {
                context.DrawText(_labels[i].Text, _labels[i].Origin);
            }
        }
    }
}

/// <summary>What the pointer hovers on the equity chart.</summary>
internal enum EquityHoverKind
{
    /// <summary>The crosshair snapped to an equity point (<see cref="EquityHoverView.Index"/> is the index into the equity series).</summary>
    Point,

    /// <summary>A trade marker (<see cref="EquityHoverView.Index"/> is the index into the marker list).</summary>
    Marker,
}

internal readonly record struct EquityHoverView(EquityHoverKind Kind, int Index, AvaloniaPoint Anchor);

/// <summary>The geometry of a built snapshot that hover needs: the plot, the time/Y range its fractions are relative to, and the marker positions.</summary>
internal sealed record EquityHoverContext(
    EquityPlotArea Plot, long StartTicks, long EndTicks, decimal YMin, decimal YMax, ImmutableArray<EquityMarkerPlacement> Placements);
