using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Theme;
using AvaloniaPoint = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Backtest;

/// <summary>Non-interactive equity curve rendered from a key-addressed immutable managed snapshot.</summary>
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
            EmptyStateTextProperty,
            UnavailableTextProperty,
            LabelFontSizeProperty);
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
    private TopLevel? _topLevel;
    private ThemeColors _theme = ThemeColors.Dark;
    private long _themeRevision;
    private long _localeRevision;
    private CurveCacheKey? _cacheKey;
    private CurveRenderSnapshot? _snapshot;

    internal int SnapshotBuildCount { get; private set; }

    /// <summary>The equity series the currently cached snapshot was built from (test observability, same role as <see cref="SnapshotBuildCount"/>).</summary>
    internal ImmutableArray<EquityPoint> RenderedEquityPoints { get; private set; } = ImmutableArray<EquityPoint>.Empty;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _themeManager = App.Current?.Services?.GetService<IThemeManager>();
        if (_themeManager is not null)
        {
            _theme = _themeManager.CurrentTheme;
            _themeManager.PropertyChanged += OnThemeManagerPropertyChanged;
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
        RefreshSnapshot(arranged);
        return arranged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EquityPointsProperty ||
            change.Property == ResultRevisionProperty ||
            change.Property == LineSemanticProperty ||
            change.Property == EmptyStateTextProperty ||
            change.Property == UnavailableTextProperty ||
            change.Property == LabelFontSizeProperty)
        {
            RefreshSnapshot();
        }
    }

    private void OnThemeManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _theme = _themeManager?.CurrentTheme ?? ThemeColors.Dark;
            _themeRevision++;
            RefreshSnapshot();
            InvalidateVisual();
        });
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
        RefreshSnapshot();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _snapshot?.Draw(context);
    }

    private void RefreshSnapshot(Size? sizeOverride = null)
    {
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
            LineSemantic);

        if (_cacheKey != key)
        {
            _snapshot = BuildSnapshot(key);
            RenderedEquityPoints = EquityPoints;
            _cacheKey = key;
            SnapshotBuildCount++;
        }
    }

    private CurveRenderSnapshot? BuildSnapshot(CurveCacheKey key)
    {
        if (!IsFinitePositive(key.Bounds.Width) || !IsFinitePositive(key.Bounds.Height) ||
            !IsFinitePositive(key.RenderScaling) || !IsFinitePositive(key.LabelFontSize))
        {
            return null;
        }

        double plotLeft = BacktestUiConstants.PlotAxisMarginLeft;
        double plotTop = BacktestUiConstants.PlotAxisMarginTop;
        double plotRight = key.Bounds.Width - BacktestUiConstants.PlotAxisMarginRight;
        double plotBottom = key.Bounds.Height - BacktestUiConstants.PlotAxisMarginBottom;
        double plotWidth = plotRight - plotLeft;
        double plotHeight = plotBottom - plotTop;
        if (!IsFinitePositive(plotWidth) || !IsFinitePositive(plotHeight)) return null;

        CultureInfo culture = CultureInfo.CurrentCulture;
        EquityCurveLayout layout = EquityCurveLayout.Build(EquityPoints, culture);
        IBrush background = new ImmutableSolidColorBrush(_theme.ChartBackground.ToAvaloniaColor());
        IBrush labelBrush = new ImmutableSolidColorBrush(_theme.AxisText.ToAvaloniaColor());
        IBrush messageBrush = new ImmutableSolidColorBrush(_theme.ShellSecondaryText.ToAvaloniaColor());
        IBrush gridBrush = new ImmutableSolidColorBrush(_theme.GridLine.ToAvaloniaColor());
        IBrush lineBrush = new ImmutableSolidColorBrush(ResolveLineColor(key.LineSemantic).ToAvaloniaColor());
        var gridPen = new Pen(gridBrush, 1d);
        var linePen = new Pen(lineBrush, BacktestUiConstants.EquityLineWidth);
        var typeface = new Typeface(FontFamily.Default);

        if (layout.State is EquityCurveDisplayState.Empty or EquityCurveDisplayState.Unavailable)
        {
            string? message = layout.State == EquityCurveDisplayState.Empty ? EmptyStateText : UnavailableText;
            FormattedText? formatted = string.IsNullOrEmpty(message)
                ? null
                : new FormattedText(message, culture, FlowDirection.LeftToRight, typeface, key.LabelFontSize, messageBrush);
            AvaloniaPoint messagePoint = formatted is null
                ? default
                : new AvaloniaPoint((key.Bounds.Width - formatted.Width) / 2d, (key.Bounds.Height - formatted.Height) / 2d);
            return CurveRenderSnapshot.Message(key.Bounds, background, formatted, messagePoint);
        }

        var gridLines = ImmutableArray.Create(
            new LineSegment(new AvaloniaPoint(plotLeft, plotTop), new AvaloniaPoint(plotRight, plotTop)),
            new LineSegment(new AvaloniaPoint(plotLeft, plotTop + (plotHeight / 2d)), new AvaloniaPoint(plotRight, plotTop + (plotHeight / 2d))),
            new LineSegment(new AvaloniaPoint(plotLeft, plotBottom), new AvaloniaPoint(plotRight, plotBottom)));

        var labels = ImmutableArray.CreateBuilder<TextPlacement>(5);
        AddYLabel(labels, layout.YMaxLabel, plotLeft, plotTop, key.LabelFontSize, culture, typeface, labelBrush);
        AddYLabel(labels, layout.YMidLabel, plotLeft, plotTop + (plotHeight / 2d), key.LabelFontSize, culture, typeface, labelBrush);
        AddYLabel(labels, layout.YMinLabel, plotLeft, plotBottom, key.LabelFontSize, culture, typeface, labelBrush);
        AddXLabels(labels, layout, plotLeft, plotRight, plotBottom, key.LabelFontSize, culture, typeface, labelBrush);

        StreamGeometry? geometry = null;
        AvaloniaPoint? singlePoint = null;
        if (layout.State == EquityCurveDisplayState.SinglePoint)
        {
            EquityCurveNormalizedPoint point = layout.Points[0];
            singlePoint = new AvaloniaPoint(plotLeft + point.XFraction * plotWidth, plotBottom - point.YFraction * plotHeight);
        }
        else
        {
            geometry = new StreamGeometry();
            using StreamGeometryContext geometryContext = geometry.Open();
            for (int i = 0; i < layout.Points.Length; i++)
            {
                EquityCurveNormalizedPoint point = layout.Points[i];
                var pixel = new AvaloniaPoint(plotLeft + point.XFraction * plotWidth, plotBottom - point.YFraction * plotHeight);
                if (i == 0) geometryContext.BeginFigure(pixel, isFilled: false);
                else geometryContext.LineTo(pixel);
            }
            geometryContext.EndFigure(isClosed: false);
        }

        return CurveRenderSnapshot.Chart(
            key.Bounds,
            background,
            gridPen,
            linePen,
            lineBrush,
            gridLines,
            geometry,
            singlePoint,
            labels.MoveToImmutable());
    }

    private static void AddYLabel(
        ImmutableArray<TextPlacement>.Builder labels,
        string text,
        double plotLeft,
        double y,
        double fontSize,
        CultureInfo culture,
        Typeface typeface,
        IBrush brush)
    {
        var formatted = new FormattedText(text, culture, FlowDirection.LeftToRight, typeface, fontSize, brush);
        labels.Add(new TextPlacement(
            formatted,
            new AvaloniaPoint(
                plotLeft - formatted.Width - BacktestUiConstants.ChartLabelGap,
                y - (fontSize / BacktestUiConstants.ChartLabelBaselineFactor))));
    }

    private static void AddXLabels(
        ImmutableArray<TextPlacement>.Builder labels,
        EquityCurveLayout layout,
        double plotLeft,
        double plotRight,
        double plotBottom,
        double fontSize,
        CultureInfo culture,
        Typeface typeface,
        IBrush brush)
    {
        var start = new FormattedText(layout.ViewportStartLabel, culture, FlowDirection.LeftToRight, typeface, fontSize, brush);
        double y = plotBottom + BacktestUiConstants.ChartLabelGap;
        if (layout.ViewportStartUtc == layout.ViewportEndUtc)
        {
            labels.Add(new TextPlacement(start, new AvaloniaPoint(((plotLeft + plotRight) / 2d) - (start.Width / 2d), y)));
            return;
        }

        labels.Add(new TextPlacement(start, new AvaloniaPoint(plotLeft, y)));
        var end = new FormattedText(layout.ViewportEndLabel, culture, FlowDirection.LeftToRight, typeface, fontSize, brush);
        labels.Add(new TextPlacement(end, new AvaloniaPoint(plotRight - end.Width, y)));
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
        BacktestMetricSemantic LineSemantic);

    private readonly record struct LineSegment(AvaloniaPoint Start, AvaloniaPoint End);
    private readonly record struct TextPlacement(FormattedText Text, AvaloniaPoint Origin);

    private sealed class CurveRenderSnapshot
    {
        private readonly Size _bounds;
        private readonly IBrush _background;
        private readonly FormattedText? _message;
        private readonly AvaloniaPoint _messagePoint;
        private readonly IPen? _gridPen;
        private readonly IPen? _linePen;
        private readonly IBrush? _lineBrush;
        private readonly ImmutableArray<LineSegment> _gridLines;
        private readonly StreamGeometry? _geometry;
        private readonly AvaloniaPoint? _singlePoint;
        private readonly ImmutableArray<TextPlacement> _labels;

        private CurveRenderSnapshot(
            Size bounds,
            IBrush background,
            FormattedText? message,
            AvaloniaPoint messagePoint,
            IPen? gridPen,
            IPen? linePen,
            IBrush? lineBrush,
            ImmutableArray<LineSegment> gridLines,
            StreamGeometry? geometry,
            AvaloniaPoint? singlePoint,
            ImmutableArray<TextPlacement> labels)
        {
            _bounds = bounds;
            _background = background;
            _message = message;
            _messagePoint = messagePoint;
            _gridPen = gridPen;
            _linePen = linePen;
            _lineBrush = lineBrush;
            _gridLines = gridLines;
            _geometry = geometry;
            _singlePoint = singlePoint;
            _labels = labels;
        }

        public static CurveRenderSnapshot Message(Size bounds, IBrush background, FormattedText? message, AvaloniaPoint messagePoint) =>
            new(bounds, background, message, messagePoint, null, null, null, ImmutableArray<LineSegment>.Empty, null, null, ImmutableArray<TextPlacement>.Empty);

        public static CurveRenderSnapshot Chart(
            Size bounds,
            IBrush background,
            IPen gridPen,
            IPen linePen,
            IBrush lineBrush,
            ImmutableArray<LineSegment> gridLines,
            StreamGeometry? geometry,
            AvaloniaPoint? singlePoint,
            ImmutableArray<TextPlacement> labels) =>
            new(bounds, background, null, default, gridPen, linePen, lineBrush, gridLines, geometry, singlePoint, labels);

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
            if (_singlePoint is { } point)
            {
                context.DrawEllipse(_lineBrush, null, point, BacktestUiConstants.EquityPointRadius, BacktestUiConstants.EquityPointRadius);
            }
            else if (_geometry is not null)
            {
                context.DrawGeometry(null, _linePen, _geometry);
            }
            for (int i = 0; i < _labels.Length; i++)
            {
                context.DrawText(_labels[i].Text, _labels[i].Origin);
            }
        }
    }
}
