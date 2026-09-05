namespace StockAnalyzer.Avalonia.Drawing;

using System;
using SkiaSharp;

/// <summary>
/// Shared geometry math for the ellipse-arc family (<see cref="EllipseObject"/>'s Arc-enabled mode,
/// with its independent Radius Lines/Chord Line toggles and true rotation, and the separate
/// <see cref="EllipseAnnulusObject"/> ring family): bounding-rect computation, rotation, parametric-
/// angle conversion between a screen point and its position on the ellipse boundary, and sweep-range
/// containment. Centralizing this math here (SSoT) avoids re-deriving it independently in each
/// ellipse-family object.
/// </summary>
public static class EllipseArcGeometry
{
    private const float GeometryEpsilon = 1e-4f;

    /// <summary>
    /// Computes the bounding rect for two diagonal corner points (used by <see cref="EllipseAnnulusObject"/>'s
    /// outer box), optionally constraining it to a square (equal width/height, centered) when
    /// <paramref name="isCircular"/> is true.
    /// </summary>
    public static SKRect ComputeBoundingRect(SKPoint p1, SKPoint p2, bool isCircular)
    {
        float left = Math.Min(p1.X, p2.X);
        float top = Math.Min(p1.Y, p2.Y);
        float right = Math.Max(p1.X, p2.X);
        float bottom = Math.Max(p1.Y, p2.Y);

        if (!isCircular) return new SKRect(left, top, right, bottom);

        float cx = (left + right) / 2f;
        float cy = (top + bottom) / 2f;
        float r = Math.Min(right - left, bottom - top) / 2f;
        return new SKRect(cx - r, cy - r, cx + r, cy + r);
    }

    /// <summary>
    /// Computes the parametric angle (degrees, [0, 360)) at which <paramref name="point"/> lies on
    /// the ellipse defined by <paramref name="rect"/>, using the standard Skia convention (0° = 3
    /// o'clock / +X axis, increasing clockwise since screen Y grows downward). Only the point's
    /// direction from the rect's center matters; its exact distance is ignored, so a click that
    /// lands near but not exactly on the boundary still resolves to a well-defined angle.
    /// </summary>
    public static float AngleFromPoint(SKRect rect, SKPoint point)
    {
        float cx = rect.MidX;
        float cy = rect.MidY;
        float rx = Math.Max(rect.Width / 2f, 1e-3f);
        float ry = Math.Max(rect.Height / 2f, 1e-3f);

        float nx = (point.X - cx) / rx;
        float ny = (point.Y - cy) / ry;

        float angle = MathF.Atan2(ny, nx) * (180f / MathF.PI);
        return angle < 0 ? angle + 360f : angle;
    }

    /// <summary>
    /// Computes the point on the ellipse boundary at the given parametric angle (degrees).
    /// </summary>
    public static SKPoint PointOnEllipse(SKRect rect, float angleDeg)
    {
        float cx = rect.MidX;
        float cy = rect.MidY;
        float rx = rect.Width / 2f;
        float ry = rect.Height / 2f;
        float rad = angleDeg * (MathF.PI / 180f);
        return new SKPoint(cx + rx * MathF.Cos(rad), cy + ry * MathF.Sin(rad));
    }

    /// <summary>
    /// Returns the positive sweep angle (0, 360] from <paramref name="startAngle"/> to
    /// <paramref name="endAngle"/>, always proceeding in the increasing/clockwise direction.
    /// </summary>
    public static float ComputeSweep(float startAngle, float endAngle)
    {
        float sweep = endAngle - startAngle;
        if (sweep <= 0) sweep += 360f;
        return sweep;
    }

    /// <summary>
    /// Scales <paramref name="rect"/> around its own center by <paramref name="ratio"/>, used to
    /// derive an annulus's inner boundary from its outer bounding rect.
    /// </summary>
    public static SKRect ScaleRectAroundCenter(SKRect rect, float ratio)
    {
        float cx = rect.MidX;
        float cy = rect.MidY;
        float halfWidth = rect.Width / 2f * ratio;
        float halfHeight = rect.Height / 2f * ratio;
        return new SKRect(cx - halfWidth, cy - halfHeight, cx + halfWidth, cy + halfHeight);
    }

    /// <summary>
    /// Computes how far <paramref name="point"/> sits from the center of <paramref name="rect"/> as
    /// a fraction (clamped to [0.02, 0.98]) of the outer radius along its direction, ignoring angle.
    /// Used to derive an annulus's inner-radius ratio from a single circumference-anchored click.
    /// </summary>
    public static float ComputeRadiusRatio(SKRect rect, SKPoint point)
    {
        float rx = Math.Max(rect.Width / 2f, 1e-3f);
        float ry = Math.Max(rect.Height / 2f, 1e-3f);
        float nx = (point.X - rect.MidX) / rx;
        float ny = (point.Y - rect.MidY) / ry;
        float dist = MathF.Sqrt(nx * nx + ny * ny);
        return Math.Clamp(dist, 0.02f, 0.98f);
    }

    /// <summary>
    /// Computes the true screen-space direction angle (degrees, [0, 360)) from <paramref name="center"/>
    /// to <paramref name="corner"/> — the rotation angle a true rotated ellipse's major axis should
    /// point toward. Unlike <see cref="AngleFromPoint"/> (which normalizes by the rect's own Rx/Ry and
    /// is used for the circumference angle handles), this uses the raw, undistorted screen direction,
    /// since a rotation angle must not depend on the ellipse's own aspect ratio.
    /// </summary>
    public static float ComputeRotationAngle(SKPoint center, SKPoint corner)
    {
        float angle = MathF.Atan2(corner.Y - center.Y, corner.X - center.X) * (180f / MathF.PI);
        return angle < 0 ? angle + 360f : angle;
    }

    /// <summary>
    /// Computes the semi-major (<paramref name="rx"/>) and semi-minor (<paramref name="ry"/>) axes for
    /// a rotated ellipse from the full center-to-corner distance. <paramref name="rx"/> always equals
    /// this distance — the corner is always on the ellipse boundary along the major axis, a core
    /// invariant of this tool's center+corner click model. Three cases decide <paramref name="ry"/>:
    /// <paramref name="isCircular"/> forces ry = rx = distance; otherwise
    /// <paramref name="dynamicAspectRatioReferenceCorner"/> (non-null) makes ry = referenceDistance^2 /
    /// distance, where referenceDistance is the center-to-corner distance at the moment this feature
    /// was turned on — dragging the corner closer to the center then grows ry (a taller ellipse) and
    /// dragging it farther shrinks ry (a wider ellipse), while rx * ry stays fixed at
    /// referenceDistance^2 (the reference circle's area) regardless of where the corner is dragged;
    /// null (the default) instead scales ry by the fixed <paramref name="aspectRatio"/> ratio, which
    /// does not preserve area as aspectRatio changes. <paramref name="ellipticityActivationCorner"/>
    /// (non-null, only consulted when <paramref name="dynamicAspectRatioReferenceCorner"/> is null)
    /// keeps ry == rx (still circular) for as long as <paramref name="corner"/> exactly matches it —
    /// the corner's position at the moment Ellipse Mode was turned on — so switching the mode on does
    /// not itself change the shape's appearance; ry only starts following
    /// <paramref name="aspectRatio"/> once the corner is actually dragged away from that position. Pair
    /// with <see cref="ComputeRotationAngle"/> (the rotation angle) to build the ellipse's local
    /// (unrotated) bounding rect centered on <paramref name="center"/>. Shared by
    /// <see cref="EllipseObject"/>'s own geometry resolution and <see cref="ChartInteractionController"/>'s
    /// circumference-handle drag snapping, so both stay consistent with each other.
    /// </summary>
    public static void ComputeRotatedSemiAxes(SKPoint center, SKPoint corner, bool isCircular, double aspectRatio, SKPoint? dynamicAspectRatioReferenceCorner, SKPoint? ellipticityActivationCorner, out float rx, out float ry)
    {
        float dx = corner.X - center.X;
        float dy = corner.Y - center.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);

        if (isCircular)
        {
            rx = distance;
            ry = distance;
            return;
        }

        rx = distance;

        if (dynamicAspectRatioReferenceCorner is { } refCorner)
        {
            float refDx = refCorner.X - center.X;
            float refDy = refCorner.Y - center.Y;
            float referenceDistance = MathF.Sqrt(refDx * refDx + refDy * refDy);
            ry = distance > GeometryEpsilon ? (referenceDistance * referenceDistance) / distance : referenceDistance;
        }
        else if (ellipticityActivationCorner is { } activationCorner &&
                 MathF.Abs(corner.X - activationCorner.X) <= GeometryEpsilon &&
                 MathF.Abs(corner.Y - activationCorner.Y) <= GeometryEpsilon)
        {
            ry = distance;
        }
        else
        {
            ry = (float)(distance * aspectRatio);
        }
    }

    /// <summary>
    /// Rotates <paramref name="point"/> by <paramref name="angleDeg"/> around <paramref name="center"/>,
    /// using the same screen-space convention as <see cref="AngleFromPoint"/>/<see cref="PointOnEllipse"/>
    /// (0° = +X axis, increasing clockwise since screen Y grows downward). Passing the negated angle
    /// performs the inverse rotation, e.g. to map a screen point into a rotated ellipse's local
    /// (unrotated) frame before applying the existing axis-aligned geometry helpers. Built on
    /// <see cref="RotateVector"/> (the point's offset from <paramref name="center"/>, rotated, then
    /// re-added to <paramref name="center"/>) so the two share a single rotation-matrix implementation.
    /// </summary>
    public static SKPoint RotatePoint(SKPoint point, SKPoint center, float angleDeg)
    {
        var rotated = RotateVector(new SKPoint(point.X - center.X, point.Y - center.Y), angleDeg);
        return new SKPoint(center.X + rotated.X, center.Y + rotated.Y);
    }

    /// <summary>
    /// Rotates <paramref name="vector"/> by <paramref name="angleDeg"/> around the origin (no center
    /// offset) — the same rotation matrix and screen-space convention <see cref="RotatePoint"/> applies
    /// to points, extracted here as the single SSoT implementation both share. Use this directly (rather
    /// than re-deriving the rotation formula inline) whenever a direction/offset vector, rather than an
    /// absolute point, needs to be carried from a shape's local (unrotated) frame into true screen space
    /// or back — e.g. rotating a tangent-line direction alongside its already-rotated boundary point.
    /// </summary>
    public static SKPoint RotateVector(SKPoint vector, float angleDeg)
    {
        float rad = angleDeg * (MathF.PI / 180f);
        float cos = MathF.Cos(rad);
        float sin = MathF.Sin(rad);
        return new SKPoint(
            vector.X * cos - vector.Y * sin,
            vector.X * sin + vector.Y * cos);
    }

    /// <summary>
    /// Samples <paramref name="path"/> at a fixed resolution and returns true if any sampled segment
    /// comes within <paramref name="tolerance"/> of (<paramref name="x"/>, <paramref name="y"/>).
    /// Shared stroke/boundary hit-test technique for ellipse-family objects whose path may include
    /// straight edges (radii, chords) in addition to arcs, matching the sampling approach already
    /// used by <see cref="NurbsConicArcObject"/> for path-shaped objects without a closed-form test.
    /// </summary>
    public static bool IsNearPathBoundary(SKPath path, float x, float y, double tolerance)
    {
        using var measure = new SKPathMeasure(path, forceClosed: false);
        double tolSquared = tolerance * tolerance;

        do
        {
            float length = measure.Length;
            if (length <= 0f) continue;

            const int sampleCount = 48;
            float step = length / sampleCount;
            if (!measure.GetPosition(0f, out var prev)) continue;

            for (int i = 1; i <= sampleCount; i++)
            {
                if (!measure.GetPosition(step * i, out var curr)) break;
                if (DistanceSquaredToSegment(x, y, prev, curr) <= tolSquared) return true;
                prev = curr;
            }
        } while (measure.NextContour());

        return false;
    }

    /// <summary>
    /// Projects the corner point onto the ellipse boundary in chart space (Time.Ticks/Price), entirely
    /// without an <see cref="ICoordinateTransform"/>. Used as the default position for
    /// <see cref="EllipseObject"/>'s circumference control points (Points[2]/[3]) at construction time
    /// — a raw copy of the corner sits outside the boundary (distance sqrt(Rx^2+Ry^2) from center) and
    /// would render/hit-test as an invisible handle stacked exactly on the corner handle instead of on
    /// the circumference.
    /// </summary>
    public static ChartPoint ComputeChartSpaceBoundaryPoint(ChartPoint center, ChartPoint corner)
    {
        double dTicks = corner.Time.Ticks - center.Time.Ticks;
        double dPrice = (double)(corner.Price - center.Price);
        double rx = Math.Abs(dTicks);
        double ry = Math.Abs(dPrice);
        if (rx < 1e-9 || ry < 1e-9) return corner; // degenerate box: nothing meaningful to project

        double angle = Math.Atan2(dPrice / ry, dTicks / rx);
        long ticks = center.Time.Ticks + (long)Math.Round(rx * Math.Cos(angle));
        decimal price = center.Price + (decimal)(ry * Math.Sin(angle));
        ticks = Math.Clamp(ticks, 0, DateTime.MaxValue.Ticks);
        return new ChartPoint(new DateTime(ticks), price);
    }

    /// <summary>
    /// Same projection as <see cref="ComputeChartSpaceBoundaryPoint"/>, but toward the point
    /// diametrically opposite the corner (reflected through the center) instead of toward the corner
    /// itself — i.e., the direction the corner would point if rotated 180 degrees around the center.
    /// Used as the default position for <see cref="EllipseObject"/>'s circumference control points
    /// (Points[2]/[3]): projecting toward the corner's own direction would place their default marker
    /// exactly on top of the corner handle (same angle, see <see cref="ComputeChartSpaceBoundaryPoint"/>'s
    /// remarks), which is indistinguishable from not being shown, and — once one of the two is dragged
    /// away — leaves the other one still coincident with the corner and impossible to grab. The default
    /// still keeps both points coincident with each other (so Arc mode's initial sweep is still a full
    /// 360 degrees, unchanged), just on the opposite side of the ellipse from the corner.
    /// </summary>
    public static ChartPoint ComputeChartSpaceOppositeBoundaryPoint(ChartPoint center, ChartPoint corner)
    {
        long reflectedTicks = Math.Clamp(2 * center.Time.Ticks - corner.Time.Ticks, 0, DateTime.MaxValue.Ticks);
        decimal reflectedPrice = 2 * center.Price - corner.Price;
        var reflectedCorner = new ChartPoint(new DateTime(reflectedTicks), reflectedPrice);
        return ComputeChartSpaceBoundaryPoint(center, reflectedCorner);
    }

    /// <summary>
    /// Scales the chart-space vector from <paramref name="center"/> to <paramref name="corner"/> by a
    /// scalar <paramref name="scale"/>, entirely without an <see cref="ICoordinateTransform"/>. Exact,
    /// not an approximation, regardless of the transform's (possibly anisotropic per-axis) scale —
    /// Euclidean screen distance commutes with uniform scalar multiplication of a 2D chart-space vector,
    /// so scaling the chart-space offset by k always reproduces the same k-times scaling of the true
    /// screen-space distance. Used to derive <see cref="EllipseObject.DynamicAspectRatioReferenceCorner"/>
    /// at an equivalent-area radius rather than the raw corner distance.
    /// </summary>
    public static ChartPoint ScaleCornerInChartSpace(ChartPoint center, ChartPoint corner, double scale)
    {
        if (Math.Abs(scale - 1.0) < 1e-9) return corner;

        long ticks = Math.Clamp(
            center.Time.Ticks + (long)Math.Round((corner.Time.Ticks - center.Time.Ticks) * scale),
            0, DateTime.MaxValue.Ticks);
        decimal price = center.Price + (corner.Price - center.Price) * (decimal)scale;
        return new ChartPoint(new DateTime(ticks), price);
    }

    private static double DistanceSquaredToSegment(float px, float py, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float l2 = dx * dx + dy * dy;
        if (l2 <= 1e-12f)
        {
            float ddx = px - a.X;
            float ddy = py - a.Y;
            return ddx * ddx + ddy * ddy;
        }

        float t = ((px - a.X) * dx + (py - a.Y) * dy) / l2;
        t = Math.Clamp(t, 0f, 1f);
        float projX = a.X + t * dx;
        float projY = a.Y + t * dy;
        float distX = px - projX;
        float distY = py - projY;
        return distX * distX + distY * distY;
    }
}
