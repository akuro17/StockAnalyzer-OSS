using SkiaSharp;
using StockAnalyzer.Core.Drawing;

namespace StockAnalyzer.Core.Rendering;

/// <summary>
/// Draws the saved drawings of a request on the price panel in the fixed colour and width of the style (the user's colour, width and line pattern are never used), clipped to the
/// panel, and says where each one went. A point is a position in drawn-bar units and a price; both go through <see cref="AiChartPlotTransform"/>, so no pixel coordinate is ever mixed
/// with a price. The geometry follows the application's objects: a trend line is a segment that the extend mode prolongs to the border of the panel, a ray starts at its first point and runs
/// through the second, a channel is the polygon of its base edge and the opposite edge (parallel through the third point, or flat at its price) with a dashed centre line.
/// </summary>
internal static class AiChartSavedDrawingRenderer
{
    private readonly record struct Stroke(double X1, double Y1, double X2, double Y2, bool Dashed);

    private readonly record struct Geometry(IReadOnlyList<AiChartPixelPoint> ControlPoints, IReadOnlyList<Stroke> Strokes);

    public static IReadOnlyList<AiChartDrawingPlacement> Draw(
        SKCanvas canvas,
        AiChartPanelRect panel,
        AiChartPlotTransform transform,
        IReadOnlyList<AiChartDrawing> drawings,
        CancellationToken cancellationToken)
    {
        var placements = new List<AiChartDrawingPlacement>(drawings.Count);
        if (drawings.Count == 0)
        {
            return placements;
        }

        double left = transform.PlotLeftPx;
        double right = left + transform.PlotWidthPx;
        double top = panel.TopPx;
        double bottom = panel.BottomPx;
        double reach = AiChartImageStyle.SavedDrawingReachFactor * ((right - left) + (bottom - top));

        using var solid = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            Color = AiChartImageStyle.Color(AiChartImageStyle.SavedDrawingHex),
            StrokeWidth = AiChartImageStyle.SavedDrawingWidthPx,
            StrokeCap = SKStrokeCap.Butt,
            StrokeJoin = SKStrokeJoin.Round,
        };
        using SKPathEffect? dash = AiChartImageRenderer.DashEffect(AiChartLineStyle.Dash);
        using var dashed = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            Color = solid.Color,
            StrokeWidth = AiChartImageStyle.SavedDrawingWidthPx,
            StrokeCap = SKStrokeCap.Butt,
            PathEffect = dash,
        };

        canvas.Save();
        canvas.ClipRect(new SKRect((float)left, (float)top, (float)right, (float)bottom));
        for (int i = 0; i < drawings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Geometry geometry = Build(drawings[i], transform, left, right, top, bottom, reach);
            bool visible = geometry.Strokes.Any(s => Intersects(left, top, right, bottom, s.X1, s.Y1, s.X2, s.Y2));
            placements.Add(new AiChartDrawingPlacement(i, geometry.ControlPoints, visible));
            if (!visible)
            {
                continue;
            }

            foreach (Stroke stroke in geometry.Strokes)
            {
                canvas.DrawLine((float)stroke.X1, (float)stroke.Y1, (float)stroke.X2, (float)stroke.Y2, stroke.Dashed ? dashed : solid);
            }
        }

        canvas.Restore();
        return placements;
    }

    private static Geometry Build(AiChartDrawing drawing, AiChartPlotTransform transform, double left, double right, double top, double bottom, double reach)
    {
        (double X, double Y)[] p = drawing.Points.Select(point => (transform.CenterXPx(point.BarIndex), transform.YPx(point.Price))).ToArray();
        AiChartPixelPoint[] controls = p.Select(point => new AiChartPixelPoint(AiChartPlotTransform.Snap(point.X), AiChartPlotTransform.Snap(point.Y))).ToArray();
        var strokes = new List<Stroke>();

        switch (drawing.Kind)
        {
            case AiChartDrawingKind.HorizontalLine:
                strokes.Add(new Stroke(left, p[0].Y, right, p[0].Y, false));
                break;
            case AiChartDrawingKind.VerticalLine:
                strokes.Add(new Stroke(p[0].X, top, p[0].X, bottom, false));
                break;
            case AiChartDrawingKind.TrendLine:
                strokes.Add(TrendLine(p[0], p[1], drawing.Extend, reach));
                break;
            case AiChartDrawingKind.Ray:
                strokes.Add(Ray(p[0], p[1], reach));
                break;
            case AiChartDrawingKind.ParallelChannel:
                Channel(strokes, p[0], p[1], p[2], drawing.ChannelMode);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drawing), drawing.Kind, "Unknown drawing kind.");
        }

        return new Geometry(controls, strokes);
    }

    /// <summary>The segment, prolonged at the screen-left end (smaller x, for a vertical line the upper end) and/or the screen-right end as the mode says; the panel clip cuts it at the border.</summary>
    private static Stroke TrendLine((double X, double Y) a, (double X, double Y) b, LineExtendMode mode, double reach)
    {
        bool firstIsLeft = !(a.X > b.X || (a.X == b.X && a.Y > b.Y));
        (double X, double Y) leftEnd = firstIsLeft ? a : b;
        (double X, double Y) rightEnd = firstIsLeft ? b : a;
        double dx = rightEnd.X - leftEnd.X;
        double dy = rightEnd.Y - leftEnd.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (mode == LineExtendMode.None || length == 0)
        {
            return new Stroke(leftEnd.X, leftEnd.Y, rightEnd.X, rightEnd.Y, false);
        }

        double k = reach / length;
        double startT = mode is LineExtendMode.Left or LineExtendMode.Both ? -k : 0;
        double endT = mode is LineExtendMode.Right or LineExtendMode.Both ? 1 + k : 1;
        return new Stroke(leftEnd.X + (startT * dx), leftEnd.Y + (startT * dy), leftEnd.X + (endT * dx), leftEnd.Y + (endT * dy), false);
    }

    private static Stroke Ray((double X, double Y) from, (double X, double Y) through, double reach)
    {
        double dx = through.X - from.X;
        double dy = through.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length == 0)
        {
            return new Stroke(from.X, from.Y, from.X, from.Y, false);
        }

        double k = reach / length;
        return new Stroke(from.X, from.Y, from.X + (k * dx), from.Y + (k * dy), false);
    }

    /// <summary>
    /// The four edges of the channel and its dashed centre line. The opposite edge lies at the x of the first and the second point: on the line through the third point with the slope of
    /// the base edge (a vertical base edge is moved sideways), or flat at the price of the third point.
    /// </summary>
    private static void Channel(List<Stroke> strokes, (double X, double Y) s1, (double X, double Y) s2, (double X, double Y) s3, ChannelMode mode)
    {
        (double X, double Y) o1;
        (double X, double Y) o2;
        if (mode == ChannelMode.FlatOpposite)
        {
            o1 = (s1.X, s3.Y);
            o2 = (s2.X, s3.Y);
        }
        else
        {
            double dx = s2.X - s1.X;
            if (dx == 0)
            {
                double shift = s3.X - s1.X;
                o1 = (s1.X + shift, s1.Y);
                o2 = (s2.X + shift, s2.Y);
            }
            else
            {
                double slope = (s2.Y - s1.Y) / dx;
                o1 = (s1.X, (slope * (s1.X - s3.X)) + s3.Y);
                o2 = (s2.X, (slope * (s2.X - s3.X)) + s3.Y);
            }
        }

        strokes.Add(new Stroke(s1.X, s1.Y, s2.X, s2.Y, false));
        strokes.Add(new Stroke(s2.X, s2.Y, o2.X, o2.Y, false));
        strokes.Add(new Stroke(o2.X, o2.Y, o1.X, o1.Y, false));
        strokes.Add(new Stroke(o1.X, o1.Y, s1.X, s1.Y, false));
        strokes.Add(new Stroke((s1.X + o1.X) / 2, (s1.Y + o1.Y) / 2, (s2.X + o2.X) / 2, (s2.Y + o2.Y) / 2, true));
    }

    /// <summary>Liang-Barsky: true when any part of the segment lies inside the rectangle (the border included).</summary>
    private static bool Intersects(double left, double top, double right, double bottom, double x1, double y1, double x2, double y2)
    {
        double enter = 0;
        double exit = 1;
        double dx = x2 - x1;
        double dy = y2 - y1;
        return ClipEdge(-dx, x1 - left, ref enter, ref exit)
               && ClipEdge(dx, right - x1, ref enter, ref exit)
               && ClipEdge(-dy, y1 - top, ref enter, ref exit)
               && ClipEdge(dy, bottom - y1, ref enter, ref exit);
    }

    private static bool ClipEdge(double p, double q, ref double enter, ref double exit)
    {
        if (p == 0)
        {
            return q >= 0;
        }

        double r = q / p;
        if (p < 0)
        {
            if (r > exit)
            {
                return false;
            }

            enter = Math.Max(enter, r);
        }
        else
        {
            if (r < enter)
            {
                return false;
            }

            exit = Math.Min(exit, r);
        }

        return true;
    }
}
