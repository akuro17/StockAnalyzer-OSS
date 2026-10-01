using System.Collections.Generic;
using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Theme;
using AvaloniaPoint = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>
/// The trade markers inside the visible range, as five geometries by look (long/short entry, long/short exit, forced liquidation) and
/// their screen positions for hover hit testing. A marker sits on the Equity value of its point (never a price), through the same
/// projection as the line. Built once per snapshot; <see cref="Draw"/> only draws.
/// </summary>
internal sealed class EquityMarkerLayer
{
    private const int LongEntryShape = 0;
    private const int LongExitShape = 1;
    private const int ShortEntryShape = 2;
    private const int ShortExitShape = 3;
    private const int ForcedShape = 4;
    private const int MarkerShapeCount = 5;

    private readonly StreamGeometry _longEntry;
    private readonly StreamGeometry _longExit;
    private readonly StreamGeometry _shortEntry;
    private readonly StreamGeometry _shortExit;
    private readonly StreamGeometry _forced;
    private readonly IBrush _longBrush;
    private readonly IBrush _shortBrush;
    private readonly IPen _outlinePen;
    private readonly IPen _forcedPen;
    private readonly Rect _clip;

    private EquityMarkerLayer(
        ImmutableArray<EquityMarkerPlacement> placements,
        StreamGeometry[] geometries,
        IBrush longBrush,
        IBrush shortBrush,
        IPen outlinePen,
        IPen forcedPen,
        Rect clip)
    {
        Placements = placements;
        _longEntry = geometries[LongEntryShape];
        _longExit = geometries[LongExitShape];
        _shortEntry = geometries[ShortEntryShape];
        _shortExit = geometries[ShortExitShape];
        _forced = geometries[ForcedShape];
        _longBrush = longBrush;
        _shortBrush = shortBrush;
        _outlinePen = outlinePen;
        _forcedPen = forcedPen;
        _clip = clip;
    }

    /// <summary>Screen position of every visible marker (index = position in the marker list), for hover hit testing.</summary>
    public ImmutableArray<EquityMarkerPlacement> Placements { get; }

    /// <summary>Null when no marker lies inside the visible range.</summary>
    public static EquityMarkerLayer? Build(
        ImmutableArray<EquityTradeMarker> markers,
        ImmutableArray<EquityPoint> points,
        EquityCurveLayout layout,
        EquityPlotArea plot,
        long startTicks,
        long endTicks,
        ThemeColors theme)
    {
        if (markers.IsDefaultOrEmpty || layout.Points.IsEmpty)
        {
            return null;
        }

        var shapes = new List<AvaloniaPoint>[MarkerShapeCount];
        for (int i = 0; i < shapes.Length; i++) shapes[i] = new List<AvaloniaPoint>();
        ImmutableArray<EquityMarkerPlacement>.Builder placements = ImmutableArray.CreateBuilder<EquityMarkerPlacement>();
        for (int i = 0; i < markers.Length; i++)
        {
            EquityTradeMarker marker = markers[i];
            if (marker.PointIndex < 0 || marker.PointIndex >= points.Length) continue;
            EquityCurveNormalizedPoint normalized = EquityCurveLayout.Normalize(
                points[marker.PointIndex], startTicks, endTicks, layout.YMin, layout.YMax);
            if (normalized.XFraction < 0d || normalized.XFraction > 1d) continue;

            (double x, double y) = plot.ToScreen(normalized);
            placements.Add(new EquityMarkerPlacement(i, x, y));
            shapes[ShapeIndex(marker)].Add(new AvaloniaPoint(x, y));
        }
        if (placements.Count == 0)
        {
            return null;
        }

        double radius = BacktestUiConstants.EquityMarkerRadius;
        IBrush outlineBrush = new ImmutableSolidColorBrush(theme.ChartBackground.ToAvaloniaColor());
        IBrush forcedBrush = new ImmutableSolidColorBrush(theme.SemanticMinus.ToAvaloniaColor());
        var geometries = new StreamGeometry[MarkerShapeCount];
        geometries[LongEntryShape] = BuildGeometry(shapes[LongEntryShape], radius, MarkerGlyph.TriangleUp);
        geometries[LongExitShape] = BuildGeometry(shapes[LongExitShape], radius, MarkerGlyph.Diamond);
        geometries[ShortEntryShape] = BuildGeometry(shapes[ShortEntryShape], radius, MarkerGlyph.TriangleDown);
        geometries[ShortExitShape] = BuildGeometry(shapes[ShortExitShape], radius, MarkerGlyph.Diamond);
        geometries[ForcedShape] = BuildGeometry(shapes[ForcedShape], radius, MarkerGlyph.Cross);
        return new EquityMarkerLayer(
            placements.ToImmutable(),
            geometries,
            new ImmutableSolidColorBrush(theme.Bullish.ToAvaloniaColor()),
            new ImmutableSolidColorBrush(theme.Bearish.ToAvaloniaColor()),
            new Pen(outlineBrush, BacktestUiConstants.EquityHairlineWidth),
            new Pen(forcedBrush, BacktestUiConstants.EquityLineWidth),
            new Rect(plot.Left, plot.Top, plot.Width, plot.Height).Inflate(radius));
    }

    public void Draw(DrawingContext context)
    {
        using (context.PushClip(_clip))
        {
            context.DrawGeometry(_longBrush, _outlinePen, _longExit);
            context.DrawGeometry(_shortBrush, _outlinePen, _shortExit);
            context.DrawGeometry(_longBrush, _outlinePen, _longEntry);
            context.DrawGeometry(_shortBrush, _outlinePen, _shortEntry);
            context.DrawGeometry(null, _forcedPen, _forced);
        }
    }

    private enum MarkerGlyph
    {
        TriangleUp,
        TriangleDown,
        Diamond,
        Cross,
    }

    /// <summary>Entry = triangle in the trade's direction, exit = diamond, in the side's color; a forced liquidation is a cross whatever the side.</summary>
    private static int ShapeIndex(EquityTradeMarker marker)
    {
        if (marker.IsForcedLiquidation) return ForcedShape;
        bool isLong = marker.Side == TradeSide.Long;
        return marker.Kind == EquityMarkerKind.Entry
            ? (isLong ? LongEntryShape : ShortEntryShape)
            : (isLong ? LongExitShape : ShortExitShape);
    }

    private static StreamGeometry BuildGeometry(List<AvaloniaPoint> centers, double radius, MarkerGlyph glyph)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        foreach (AvaloniaPoint c in centers)
        {
            switch (glyph)
            {
                case MarkerGlyph.TriangleUp:
                    context.BeginFigure(new AvaloniaPoint(c.X, c.Y - radius), isFilled: true);
                    context.LineTo(new AvaloniaPoint(c.X - radius, c.Y + radius));
                    context.LineTo(new AvaloniaPoint(c.X + radius, c.Y + radius));
                    context.EndFigure(isClosed: true);
                    break;
                case MarkerGlyph.TriangleDown:
                    context.BeginFigure(new AvaloniaPoint(c.X, c.Y + radius), isFilled: true);
                    context.LineTo(new AvaloniaPoint(c.X - radius, c.Y - radius));
                    context.LineTo(new AvaloniaPoint(c.X + radius, c.Y - radius));
                    context.EndFigure(isClosed: true);
                    break;
                case MarkerGlyph.Diamond:
                    context.BeginFigure(new AvaloniaPoint(c.X, c.Y - radius), isFilled: true);
                    context.LineTo(new AvaloniaPoint(c.X + radius, c.Y));
                    context.LineTo(new AvaloniaPoint(c.X, c.Y + radius));
                    context.LineTo(new AvaloniaPoint(c.X - radius, c.Y));
                    context.EndFigure(isClosed: true);
                    break;
                default:
                    context.BeginFigure(new AvaloniaPoint(c.X - radius, c.Y - radius), isFilled: false);
                    context.LineTo(new AvaloniaPoint(c.X + radius, c.Y + radius));
                    context.EndFigure(isClosed: false);
                    context.BeginFigure(new AvaloniaPoint(c.X - radius, c.Y + radius), isFilled: false);
                    context.LineTo(new AvaloniaPoint(c.X + radius, c.Y - radius));
                    context.EndFigure(isClosed: false);
                    break;
            }
        }
        return geometry;
    }
}
