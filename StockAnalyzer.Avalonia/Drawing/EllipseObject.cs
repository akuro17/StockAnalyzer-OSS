using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Unified ellipse-family object. Points[0] is a fixed center (dragging it translates the whole
/// shape rather than resizing it asymmetrically); Points[1] is a corner point whose distance from the
/// center defines the semi-major axis (Rx) and whose direction defines the rotation angle — the same
/// center+corner click model as <see cref="NurbsEllipseObject"/>, extended with true rotation (the
/// semi-minor axis Ry is Rx scaled by <see cref="AspectRatio"/>, since 2 points alone only provide 2
/// of the 3 degrees of freedom a rotated ellipse needs). Points[2]/[3] (start angle / end angle) are
/// always present from construction, always constrained to the ellipse's circumference (both for
/// their default position and for any subsequent handle drag — see
/// <see cref="ChartInteractionController"/>), and always shown/draggable as selection handles
/// regardless of <see cref="IsArcEnabled"/>. <see cref="IsArcEnabled"/> only switches what gets
/// rendered/hit-tested: false (the default) draws a plain closed, potentially-rotated ellipse/circle
/// with full fill/gradient support (Points[2]/[3] exist as draggable handles but do not affect the
/// shape); true switches to an always-drawn arc curve bounded by Points[2]/[3]'s angles, onto which
/// <see cref="ShowRadiusLines"/> (two straight edges back to the center) and
/// <see cref="ShowChordLine"/> (a straight edge between the two arc endpoints) can each be composed
/// independently. <see cref="InnerRadiusRatio"/> (default 0, ported from
/// <see cref="EllipseAnnulusObject"/>) additionally cuts a ring/annulus out of the shape — a full ring
/// while <see cref="IsArcEnabled"/> is false, or an annular sector bounded by the current arc sweep
/// while it is true. <see cref="ShowTangentLines"/> (default false) independently draws a short line
/// tangent to the boundary at each of Points[2]/[3], regardless of <see cref="IsArcEnabled"/>;
/// <see cref="ExtendTangentLinesToChart"/> (default false) additionally extends those lines all the way
/// to the chart's own left/right edges instead. Shares
/// boundary/angle geometry with the separate ring-family <see cref="EllipseAnnulusObject"/> via
/// <see cref="EllipseArcGeometry"/>.
/// </summary>
public class EllipseObject : IEllipseFamilyObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    private const float GeometryEpsilon = 1e-4f;

    /// <summary>Half-angle (degrees) the two circumference handle *markers* are split apart by when
    /// Points[2]/[3] are still (near-)coincident — see the remarks in <see cref="TryComputeArc"/>.</summary>
    private const float CoincidentHandleMarkerNudgeDegrees = 8f;

    /// <summary>Half-length of each <see cref="ShowTangentLines"/> segment (while
    /// <see cref="ExtendTangentLinesToChart"/> is off), as a fraction of the ellipse's average radius
    /// ((Rx+Ry)/2) — chosen so the line scales naturally with the ellipse's own size (like
    /// <see cref="ShowRadiusLines"/>/<see cref="ShowChordLine"/> already do) rather than using a fixed
    /// screen-pixel length that would look disproportionate at very small or large zoom/shape
    /// sizes.</summary>
    private const float TangentLineHalfLengthRatio = 0.3f;

    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.Ellipse;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>When true (the default), constrains rendering/hit-testing to a circle (equal radii)
    /// instead of an independently-scaled ellipse, without altering the stored center/corner points.
    /// The settings dialog's "Ellipse (Independent Aspect Ratio)" checkbox sets this to false.</summary>
    public bool IsCircular { get; set; } = true;

    /// <summary>False (default) draws a plain closed ellipse/circle from Points[0]/[1] alone; true
    /// uses all 4 points and always draws the circumference arc. The settings dialog is responsible
    /// for adding/removing Points[2]/[3] when this changes, so Points.Count always matches the number
    /// of active handles (see EllipseSettingsPanelDefinition).</summary>
    public bool IsArcEnabled { get; set; } = false;

    /// <summary>When <see cref="IsArcEnabled"/>, additionally draws the two straight radii from the
    /// arc's endpoints back to the ellipse center (independent of <see cref="ShowChordLine"/>).</summary>
    public bool ShowRadiusLines { get; set; } = false;

    /// <summary>When <see cref="IsArcEnabled"/>, additionally draws the straight chord line between
    /// the arc's two endpoints (independent of <see cref="ShowRadiusLines"/>).</summary>
    public bool ShowChordLine { get; set; } = false;

    /// <summary>Ratio of semi-minor to semi-major axis (Ry/Rx, range (0, 1]) used together with true
    /// ellipse rotation (see <see cref="TryComputeEllipseParams"/>): with the corner point providing
    /// only distance and direction (2 degrees of freedom) toward a rotated ellipse, this supplies the
    /// missing third degree of freedom needed for Rx/Ry independently of rotation angle. 1.0 =
    /// circle-like proportions; smaller values = a more elongated ellipse. Ignored while
    /// <see cref="IsCircular"/> is true, or while <see cref="DynamicAspectRatioByDistance"/> is
    /// enabled (it takes over Ry entirely).</summary>
    public double AspectRatio { get; set; } = 0.5;

    /// <summary>When true and <see cref="IsCircular"/> is false, Ry is no longer set from
    /// <see cref="AspectRatio"/> at all — instead it is derived each time from how far the corner
    /// currently is from the center, relative to <see cref="DynamicAspectRatioReferenceCorner"/> (the
    /// corner's position at the moment this was turned on): dragging the corner closer to the center
    /// grows Ry (a taller ellipse), dragging it farther shrinks Ry (a wider ellipse), while Rx * Ry
    /// stays fixed at the reference distance squared — the reference circle's area — regardless of
    /// where the corner ends up. Ignored while <see cref="IsCircular"/> is true.</summary>
    public bool DynamicAspectRatioByDistance { get; set; } = false;

    /// <summary>A point, at the equivalent-area radius of the shape as it looked the moment
    /// <see cref="DynamicAspectRatioByDistance"/> was turned on, in the corner's direction — set by
    /// <see cref="Views.Dialogs.EllipseSettingsPanelDefinition"/> via
    /// <see cref="EllipseArcGeometry.ScaleCornerInChartSpace"/> (chart-space only, no
    /// <see cref="ICoordinateTransform"/> needed). Deliberately NOT a raw copy of <see cref="Points"/>[1]
    /// — that would sit at distance == Rx, and since an equivalent-radius reference always has distance
    /// == Rx == Ry, using the raw corner would snap Ry to Rx (a circle) the instant this is turned on,
    /// discarding whatever elongation the ellipse already had. Null (the default) means the feature has
    /// never been enabled on this object.</summary>
    public ChartPoint? DynamicAspectRatioReferenceCorner { get; set; } = null;

    /// <summary>A raw copy of <see cref="Points"/>[1] captured at the moment <see cref="IsCircular"/>
    /// was turned off (Ellipse Mode enabled) — set by
    /// <see cref="Views.Dialogs.EllipseSettingsPanelDefinition"/>. So long as the corner has not moved
    /// from this position, the shape keeps rendering as a circle even though <see cref="IsCircular"/>
    /// is now false; only once the corner is actually dragged away from it does <see cref="AspectRatio"/>
    /// start taking effect, so enabling Ellipse Mode does not itself change the shape's appearance.
    /// Ignored while <see cref="IsCircular"/> is true or <see cref="DynamicAspectRatioByDistance"/> is
    /// enabled. Null (the default) means Ellipse Mode has never been turned on for this object.</summary>
    public ChartPoint? EllipticityActivationCorner { get; set; } = null;

    /// <summary>Color used only for the two circumference angle handle markers (Points[2]/[3]),
    /// distinguishing them from the center/corner handles (which always use the shared default handle
    /// color) now that they are always shown/draggable regardless of <see cref="IsArcEnabled"/>. Has no
    /// effect on the rendered shape itself.</summary>
    public Color ControlPointColor { get; set; } = Colors.Orange;

    /// <summary>When true (false by default), draws a short line tangent to the ellipse boundary at
    /// each of the two circumference angle handles (Points[2]/[3]) individually, in the direction the
    /// boundary curves at that exact point — NOT a line connecting the two handles to each other (that
    /// is <see cref="ShowChordLine"/>). Independent of <see cref="IsArcEnabled"/>: since Points[2]/[3]
    /// are always shown/draggable regardless of Arc mode (see class remarks), their tangent lines are
    /// drawn in both the plain ellipse/ring shape and the Arc-enabled shape alike. Purely decorative —
    /// not part of hit-testing. <see cref="ExtendTangentLinesToChart"/> independently controls whether
    /// each line stays this short segment or reaches all the way to the chart's own left/right
    /// edges.</summary>
    public bool ShowTangentLines { get; set; } = false;

    /// <summary>When true (false by default; has no effect unless <see cref="ShowTangentLines"/> is
    /// also true), extends each tangent line all the way to the chart's own left and right screen
    /// edges (<see cref="ICoordinateTransform.CanvasWidth"/>), the same "spans the whole chart" extent
    /// <see cref="HorizontalLineObject"/> uses — instead of the short segment
    /// <see cref="ShowTangentLines"/> draws on its own.</summary>
    public bool ExtendTangentLinesToChart { get; set; } = false;

    /// <summary>Fraction (range [0, 0.98]) of the outer Rx/Ry the inner ring boundary is scaled to —
    /// ported from <see cref="EllipseAnnulusObject"/>'s identically-named property, computed here via
    /// the same <see cref="EllipseArcGeometry.ScaleRectAroundCenter"/> helper. 0 (the default) means no
    /// ring at all: the shape renders/hit-tests exactly as before this property existed. Above 0, while
    /// <see cref="IsArcEnabled"/> is false this cuts a full 0-360 degree ring (an annulus) regardless of
    /// where Points[2]/[3] currently sit — they stay independently shown/draggable but do not affect
    /// the ring's extent, matching how they already have no effect on the shape while Arc mode is off;
    /// while <see cref="IsArcEnabled"/> is true it instead cuts an annular SECTOR bounded by the
    /// existing arc sweep (<see cref="ShowRadiusLines"/>/<see cref="ShowChordLine"/> are ignored in
    /// this case — the ring's own two radial seams already close the shape).</summary>
    public double InnerRadiusRatio { get; set; } = 0;

    // Properties for Fill, Gradient, and Blending
    public bool IsFilled { get; set; } = true;
    public DrawingBlendMode BlendMode { get; set; } = DrawingBlendMode.Normal;
    public DrawingGradientType GradientType { get; set; } = DrawingGradientType.None;
    public Color? GradientEndColor { get; set; } = null;
    public byte FillAlpha { get; set; } = 30;
    public byte GradientEndAlpha { get; set; } = 30;

    public EllipseObject(ChartPoint center, ChartPoint corner)
    {
        Points.Add(center);
        Points.Add(corner);

        // Points[2]/[3] (the circumference angle handles) always exist from construction — see the
        // class remarks above. Both default to the same boundary-projected point on the side of the
        // ellipse OPPOSITE the corner (not toward the corner itself, which would place the default
        // marker exactly on top of the corner handle — see ComputeChartSpaceOppositeBoundaryPoint's
        // remarks), collapsing to a 360-degree sweep until the user drags one apart.
        var boundaryPoint = EllipseArcGeometry.ComputeChartSpaceOppositeBoundaryPoint(center, corner);
        Points.Add(boundaryPoint);
        Points.Add(boundaryPoint);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);
    private SKColor ControlPointSkiaColor => new SKColor(ControlPointColor.R, ControlPointColor.G, ControlPointColor.B, ControlPointColor.A);

    /// <summary>
    /// Returns the current screen-space positions of this object's drag handles, in the same order as
    /// <see cref="Points"/> — [Center, Corner, StartAngle, EndAngle] whenever all 4 control points are
    /// present (regardless of <see cref="IsArcEnabled"/> — the circumference handles are always shown/
    /// draggable), or [Center, Corner] for objects persisted before that control-point model existed
    /// (Points.Count == 2; see <see cref="Views.Dialogs.EllipseSettingsPanelDefinition"/>'s migration).
    /// This is the same computation <see cref="Render"/> uses to draw the selection markers (via
    /// <see cref="TryComputeArc"/>, whose angle-handle positions are boundary-projected and rotation-
    /// corrected rather than Points[2]/[3]'s raw stored coordinates). <see cref="ChartInteractionController"/>'s
    /// handle-click hit-test calls this instead of measuring distance to raw <see cref="Points"/>
    /// directly, so the clickable position always matches what is actually rendered — see
    /// SA_UI_INTERACTION.md's handle rendered-position/hit-test-position consistency rule.
    /// </summary>
    public global::Avalonia.Point[] GetSelectionHandleScreenPositions(ICoordinateTransform transform)
    {
        if (transform == null || Points.Count < 2) return Array.Empty<global::Avalonia.Point>();

        if (Points.Count >= 4)
        {
            return TryComputeArc(transform, out _, out _, out _, out _, out _, out var handles, out _, out _)
                ? handles
                : Array.Empty<global::Avalonia.Point>();
        }

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        return IsFinite(s0) && IsFinite(s1) ? new[] { s0, s1 } : Array.Empty<global::Avalonia.Point>();
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        if (IsArcEnabled)
        {
            RenderArc(canvas, transform);
            return;
        }

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        if (!IsFinite(s0) || !IsFinite(s1)) return;

        bool hasValidGeometry = TryComputeEllipseParams(transform, out var centerScreen, out var rotationAngle, out var rx, out var ry);
        var localRect = new SKRect(centerScreen.X - rx, centerScreen.Y - ry, centerScreen.X + rx, centerScreen.Y + ry);

        // Full ring (annulus): Arc mode is off, so this ignores Points[2]/[3]'s sweep entirely (they
        // stay independently shown/draggable but have no effect here, same as when InnerRadiusRatio is
        // 0) and always cuts a complete 0-360 degree ring via the same single-contour path BuildArcPath
        // uses for the Arc-enabled ring/annular-sector case.
        bool isRing = InnerRadiusRatio > GeometryEpsilon && hasValidGeometry;
        using var ringPath = isRing
            ? BuildArcPath(localRect, 0f, 360f, showRadiusLines: false, showChordLine: false, EllipseArcGeometry.ScaleRectAroundCenter(localRect, (float)InnerRadiusRatio))
            : null;

        // Resolved once, up front, so both the short-segment form (drawn below, inside the rotation
        // block) and the chart-width form (drawn after Restore, in true screen space -- see
        // ExtendTangentLinesToChart) can reuse the same handle angles without re-deriving them.
        float tangentHandle2Angle = 0f, tangentHandle3Angle = 0f;
        bool hasTangentGeometry = ShowTangentLines && Points.Count >= 4 &&
            TryComputeArc(transform, out _, out _, out _, out _, out _, out _, out tangentHandle2Angle, out tangentHandle3Angle);

        // Rotation is applied as a canvas transform around the center, so the fill/stroke geometry
        // below is expressed in the ellipse's own local (unrotated) frame — localRect is built with
        // Rx/Ry along the local +X/+Y axes exactly as before rotation support was added, and Skia
        // rotates it (and anything else drawn while the transform is active) into its true screen
        // position for us.
        canvas.Save();
        canvas.RotateDegrees(rotationAngle, centerScreen.X, centerScreen.Y);

        // [Layer 1: Fill Layer]
        if (IsFilled && hasValidGeometry)
        {
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                BlendMode = BlendMode.ToSkBlendMode()
            };

            if (GradientType == DrawingGradientType.None)
            {
                fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                DrawShape(canvas, localRect, ringPath, fillPaint);
            }
            else
            {
                // The gradient's diagonal endpoint must also be expressed in the local (unrotated)
                // frame — using the raw corner screen position here would double-rotate it, since the
                // canvas transform above already rotates whatever coordinates the shader is evaluated
                // with.
                var localCorner = new SKPoint(centerScreen.X + rx, centerScreen.Y + ry);
                using var shader = DrawingShaderFactory.CreateShader(this, localRect, centerScreen.X, centerScreen.Y, localCorner.X, localCorner.Y);
                if (shader != null)
                {
                    fillPaint.Shader = shader;
                    DrawShape(canvas, localRect, ringPath, fillPaint);
                }
                else
                {
                    fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                    DrawShape(canvas, localRect, ringPath, fillPaint);
                }
            }
        }

        // [Layer 2: Stroke Layer]
        if (Thickness > 0)
        {
            using var strokePaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                BlendMode = SKBlendMode.SrcOver,
                IsAntialias = true
            };
            DrawShape(canvas, localRect, ringPath, strokePaint);
        }

        // [Layer 2b: Tangent Lines, short-segment form] (Points[2]/[3] always exist regardless of Arc
        // mode -- see class remarks -- so their tangent lines are drawn here too, independent of
        // IsArcEnabled). Only drawn here when NOT extending to the chart's edges -- that form is drawn
        // after Restore instead, since it needs true (unrotated) screen coordinates.
        if (hasTangentGeometry && !ExtendTangentLinesToChart)
        {
            using var tangentPaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            DrawTangentLines(canvas, localRect, tangentHandle2Angle, tangentHandle3Angle, tangentPaint);
        }

        canvas.Restore();

        // [Layer 2c: Tangent Lines, chart-width form] (true/unrotated screen space, drawn after
        // Restore -- reaches the chart's own left/right edges, so it must be computed in true screen
        // space rather than the ellipse's local rotated frame).
        if (hasTangentGeometry && ExtendTangentLinesToChart)
        {
            using var tangentPaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            DrawTangentLinesFullChartWidth(canvas, localRect, centerScreen, rotationAngle, tangentHandle2Angle, tangentHandle3Angle, transform, tangentPaint);
        }

        // [Layer 3: Selection Handles] (drawn after Restore, in true/unrotated screen space — the
        // handles themselves are literal control-point positions, not part of the rotated shape).
        // The circumference angle handles (Points[2]/[3]) are drawn here too, even though Arc mode is
        // off and they have no effect on the rendered shape yet — they are always shown/draggable (see
        // class remarks) so the user can position them before switching Arc mode on.
        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(canvas, s0, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            SelectionHandleRenderer.Draw(canvas, s1, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);

            if (Points.Count >= 4 && TryComputeArc(transform, out _, out _, out _, out _, out _, out var angleHandles, out _, out _))
            {
                SelectionHandleRenderer.Draw(canvas, angleHandles[2], AnchorPointIndex == 2 ? DrawingThemeContext.AnchorPointColor : ControlPointSkiaColor);
                SelectionHandleRenderer.Draw(canvas, angleHandles[3], AnchorPointIndex == 3 ? DrawingThemeContext.AnchorPointColor : ControlPointSkiaColor);
            }
        }
    }

    /// <summary>Draws <see cref="ShowTangentLines"/>' two independent short tangent-line segments (one
    /// per circumference angle handle), built in the ellipse's local (unrotated) frame -- callers must
    /// invoke this only while the canvas's rotation-as-transform (matching the rest of Render/RenderArc)
    /// is active. See <see cref="DrawTangentLinesFullChartWidth"/> for the
    /// <see cref="ExtendTangentLinesToChart"/> counterpart.</summary>
    private static void DrawTangentLines(SKCanvas canvas, SKRect localRect, float handle2Angle, float handle3Angle, SKPaint paint)
    {
        DrawTangentLine(canvas, localRect, handle2Angle, paint);
        DrawTangentLine(canvas, localRect, handle3Angle, paint);
    }

    /// <summary>Draws a single short line tangent to the ellipse boundary at the given local
    /// (pre-rotation) parametric angle. The tangent direction is the derivative of the parametric
    /// ellipse (rx·cosθ, ry·sinθ) with respect to θ, i.e. (-rx·sinθ, ry·cosθ) -- the same convention
    /// <see cref="EllipseArcGeometry.PointOnEllipse"/> parametrizes the boundary with, so the line
    /// naturally sits flush against the boundary at that point regardless of the ellipse's rotation
    /// (applied afterward, by the canvas transform).</summary>
    private static void DrawTangentLine(SKCanvas canvas, SKRect localRect, float angleDeg, SKPaint paint)
    {
        float rx = localRect.Width / 2f;
        float ry = localRect.Height / 2f;
        float rad = angleDeg * (MathF.PI / 180f);

        float dx = -rx * MathF.Sin(rad);
        float dy = ry * MathF.Cos(rad);
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < GeometryEpsilon) return;

        float halfLength = TangentLineHalfLengthRatio * (rx + ry) / 2f;
        float ux = dx / len * halfLength;
        float uy = dy / len * halfLength;

        var boundary = EllipseArcGeometry.PointOnEllipse(localRect, angleDeg);
        canvas.DrawLine(boundary.X - ux, boundary.Y - uy, boundary.X + ux, boundary.Y + uy, paint);
    }

    /// <summary>Draws <see cref="ExtendTangentLinesToChart"/>'s two independent tangent lines, each
    /// extended all the way to the chart's own left/right screen edges -- callers must invoke this
    /// AFTER any active canvas rotation transform has been restored, since (unlike
    /// <see cref="DrawTangentLines"/>) it works in true/unrotated screen space throughout.</summary>
    private static void DrawTangentLinesFullChartWidth(SKCanvas canvas, SKRect localRect, SKPoint centerScreen, float rotationAngle, float handle2Angle, float handle3Angle, ICoordinateTransform transform, SKPaint paint)
    {
        DrawTangentLineFullChartWidth(canvas, localRect, centerScreen, rotationAngle, handle2Angle, transform, paint);
        DrawTangentLineFullChartWidth(canvas, localRect, centerScreen, rotationAngle, handle3Angle, transform, paint);
    }

    /// <summary>Draws a single tangent line extended until it reaches the chart's own left (x=0) and
    /// right (x=<see cref="ICoordinateTransform.CanvasWidth"/>) screen edges -- the same "spans the
    /// whole chart" extent <see cref="HorizontalLineObject"/> uses. The tangent direction is computed
    /// in the ellipse's local (unrotated) frame first (matching <see cref="DrawTangentLine"/>), then
    /// both the boundary point and the direction vector are rotated into true screen space (via
    /// <paramref name="rotationAngle"/>) before intersecting with the chart's edges, since the line must
    /// reach the chart's TRUE screen boundaries regardless of the ellipse's own rotation. At the
    /// (true-screen-space) vertical-tangent edge case, where a left-to-right extent is undefined, this
    /// instead spans the chart's own top/bottom screen edges as the natural analogous extent.</summary>
    private static void DrawTangentLineFullChartWidth(SKCanvas canvas, SKRect localRect, SKPoint centerScreen, float rotationAngle, float angleDeg, ICoordinateTransform transform, SKPaint paint)
    {
        float rx = localRect.Width / 2f;
        float ry = localRect.Height / 2f;
        float rad = angleDeg * (MathF.PI / 180f);

        float dx = -rx * MathF.Sin(rad);
        float dy = ry * MathF.Cos(rad);
        if (MathF.Sqrt(dx * dx + dy * dy) < GeometryEpsilon) return;

        var localBoundary = EllipseArcGeometry.PointOnEllipse(localRect, angleDeg);
        var trueBoundary = EllipseArcGeometry.RotatePoint(localBoundary, centerScreen, rotationAngle);

        // Rotate the direction VECTOR (not a point -- no center offset) into true screen space too.
        var trueDirection = EllipseArcGeometry.RotateVector(new SKPoint(dx, dy), rotationAngle);
        float trueDx = trueDirection.X;
        float trueDy = trueDirection.Y;

        float chartWidth = (float)transform.CanvasWidth;

        if (MathF.Abs(trueDx) < GeometryEpsilon)
        {
            float chartHeight = (float)transform.CanvasHeight;
            canvas.DrawLine(trueBoundary.X, 0, trueBoundary.X, chartHeight, paint);
            return;
        }

        float tLeft = (0f - trueBoundary.X) / trueDx;
        float tRight = (chartWidth - trueBoundary.X) / trueDx;
        canvas.DrawLine(0, trueBoundary.Y + tLeft * trueDy, chartWidth, trueBoundary.Y + tRight * trueDy, paint);
    }

    /// <summary>Draws <paramref name="ringPath"/> (the ring/annulus contour) if supplied, otherwise
    /// the plain oval — the two render as identically as possible for InnerRadiusRatio == 0 (no
    /// behavior change), since <see cref="SKCanvas.DrawOval"/> and <see cref="SKCanvas.DrawPath"/>
    /// with a matching antialiased oval path are visually equivalent.</summary>
    private static void DrawShape(SKCanvas canvas, SKRect rect, SKPath? ringPath, SKPaint paint)
    {
        if (ringPath != null) canvas.DrawPath(ringPath, paint);
        else canvas.DrawOval(rect, paint);
    }

    /// <summary>
    /// Resolves the current screen-space ellipse center, rotation angle, and semi-axes (Rx = distance
    /// from center to corner; Ry forced equal to Rx when <see cref="IsCircular"/>, driven by corner
    /// distance relative to <see cref="DynamicAspectRatioReferenceCorner"/> when
    /// <see cref="DynamicAspectRatioByDistance"/>, or otherwise scaled by <see cref="AspectRatio"/>)
    /// shared by <see cref="Render"/> and <see cref="HitTest"/>. Returns false if the geometry is
    /// degenerate (near-zero radius) and should not be filled/hit-tested as a solid shape.
    /// </summary>
    private bool TryComputeEllipseParams(ICoordinateTransform transform, out SKPoint centerScreen, out float rotationAngle, out float rx, out float ry)
    {
        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        centerScreen = new SKPoint((float)s0.X, (float)s0.Y);
        var cornerScreen = new SKPoint((float)s1.X, (float)s1.Y);

        SKPoint? referenceCornerScreen = null;
        if (DynamicAspectRatioByDistance && DynamicAspectRatioReferenceCorner is { } referenceCorner)
        {
            var refScreen = transform.ChartToScreen(referenceCorner);
            referenceCornerScreen = new SKPoint((float)refScreen.X, (float)refScreen.Y);
        }

        SKPoint? activationCornerScreen = null;
        if (EllipticityActivationCorner is { } activationCorner)
        {
            var actScreen = transform.ChartToScreen(activationCorner);
            activationCornerScreen = new SKPoint((float)actScreen.X, (float)actScreen.Y);
        }

        rotationAngle = EllipseArcGeometry.ComputeRotationAngle(centerScreen, cornerScreen);
        EllipseArcGeometry.ComputeRotatedSemiAxes(centerScreen, cornerScreen, IsCircular, AspectRatio, referenceCornerScreen, activationCornerScreen, out rx, out ry);

        return rx > GeometryEpsilon && ry > GeometryEpsilon;
    }

    private void RenderArc(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 4) return;
        if (!TryComputeArc(transform, out var localRect, out var startAngle, out var sweep, out var centerScreen, out var rotationAngle, out var handles, out var handle2Angle, out var handle3Angle)) return;

        bool isRing = InnerRadiusRatio > GeometryEpsilon;
        SKRect? innerRect = isRing ? EllipseArcGeometry.ScaleRectAroundCenter(localRect, (float)InnerRadiusRatio) : null;
        using var path = BuildArcPath(localRect, startAngle, sweep, ShowRadiusLines, ShowChordLine, innerRect);

        // Same rotation-as-canvas-transform technique as the plain Ellipse mode: the path above is
        // built entirely in the ellipse's local (unrotated) frame, and Skia rotates it into its true
        // screen position for us.
        canvas.Save();
        canvas.RotateDegrees(rotationAngle, centerScreen.X, centerScreen.Y);

        if (IsFilled && (isRing || ShowRadiusLines || ShowChordLine))
        {
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                BlendMode = BlendMode.ToSkBlendMode(),
                Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha)
            };
            canvas.DrawPath(path, fillPaint);
        }

        if (Thickness > 0)
        {
            using var strokePaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                BlendMode = SKBlendMode.SrcOver,
                IsAntialias = true
            };
            canvas.DrawPath(path, strokePaint);
        }

        if (ShowTangentLines && !ExtendTangentLinesToChart)
        {
            using var tangentPaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            DrawTangentLines(canvas, localRect, handle2Angle, handle3Angle, tangentPaint);
        }

        canvas.Restore();

        if (ShowTangentLines && ExtendTangentLinesToChart)
        {
            using var tangentPaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            DrawTangentLinesFullChartWidth(canvas, localRect, centerScreen, rotationAngle, handle2Angle, handle3Angle, transform, tangentPaint);
        }

        // Handle markers (drawn after Restore, in true/unrotated screen space): TryComputeArc already
        // rotated the boundary-projected angle handles back into global coordinates for us. Indices
        // 2/3 (the circumference angle handles) use ControlPointColor to distinguish them from the
        // center/corner handles (indices 0/1), which use the shared default handle color; whichever
        // index matches AnchorPointIndex is highlighted with AnchorPointColor instead.
        if (IsSelected)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                var color = i == AnchorPointIndex ? DrawingThemeContext.AnchorPointColor : (i >= 2 ? ControlPointSkiaColor : (SKColor?)null);
                SelectionHandleRenderer.Draw(canvas, handles[i], color);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        if (IsArcEnabled) return HitTestArc(screenPoint, transform, tolerance);

        if (!TryComputeEllipseParams(transform, out var centerScreen, out var rotationAngle, out var rx, out var ry)) return false; // Degenerate

        // Map the click into the ellipse's local (unrotated) frame by inverse-rotating it around the
        // center, then reuse the same axis-aligned ellipse-equation test as before rotation support.
        var localPoint = EllipseArcGeometry.RotatePoint(
            new SKPoint((float)screenPoint.X, (float)screenPoint.Y), centerScreen, -rotationAngle);

        double normalizedX = (localPoint.X - centerScreen.X) / rx;
        double normalizedY = (localPoint.Y - centerScreen.Y) / ry;
        double distSq = normalizedX * normalizedX + normalizedY * normalizedY;
        double dist = Math.Sqrt(distSq);

        bool isRing = InnerRadiusRatio > GeometryEpsilon;

        if (IsFilled)
        {
            // Inside the outer boundary and, when this is a ring, also outside the inner boundary —
            // a normal solid ellipse is just the isRing==false case of the same check (inner bound 0).
            if (distSq <= 1.0 && (!isRing || dist >= InnerRadiusRatio)) return true;
        }

        // Check if close to the outer boundary within tolerance
        double minR = Math.Min(rx, ry);
        if (minR < 1.0) minR = 1.0;

        double outerDiff = Math.Abs(dist - 1.0);
        if (outerDiff * minR <= tolerance * 2.0) return true;

        // A ring also has an inner boundary that can be clicked/hovered on its own.
        if (isRing)
        {
            double innerDiff = Math.Abs(dist - InnerRadiusRatio);
            if (innerDiff * minR <= tolerance * 2.0) return true;
        }

        return false;
    }

    private bool HitTestArc(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance)
    {
        if (Points.Count < 4) return false;
        if (!TryComputeArc(transform, out var localRect, out var startAngle, out var sweep, out var centerScreen, out var rotationAngle, out _, out _, out _)) return false;

        bool isRing = InnerRadiusRatio > GeometryEpsilon;
        SKRect? innerRect = isRing ? EllipseArcGeometry.ScaleRectAroundCenter(localRect, (float)InnerRadiusRatio) : null;
        using var path = BuildArcPath(localRect, startAngle, sweep, ShowRadiusLines, ShowChordLine, innerRect);

        // Map the click into the ellipse's local (unrotated) frame, matching the frame the path above
        // was built in, instead of rotating the path itself.
        var localPoint = EllipseArcGeometry.RotatePoint(
            new SKPoint((float)screenPoint.X, (float)screenPoint.Y), centerScreen, -rotationAngle);
        float x = localPoint.X;
        float y = localPoint.Y;

        if (IsFilled && (isRing || ShowRadiusLines || ShowChordLine) && path.Contains(x, y)) return true;

        double effectiveTolerance = tolerance + Thickness / 2.0;
        return EllipseArcGeometry.IsNearPathBoundary(path, x, y, effectiveTolerance);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    /// <summary>
    /// Builds the path shared by rendering and hit-testing: the circumference arc is always drawn,
    /// with <paramref name="showRadiusLines"/> and <paramref name="showChordLine"/> each independently
    /// composing an additional straight edge on top of it. A sweep of exactly 360° (the
    /// coincident-angle-handles default; see EllipseSettingsPanelDefinition) is nudged down by 0.1°
    /// first — SkiaSharp's SKPath.AddArc treats an exact 360° sweep as a degenerate no-op arc rather
    /// than a full oval, an imperceptible gap that keeps the shape visually a complete circle/ellipse.
    /// When <paramref name="innerRect"/> is supplied (<see cref="InnerRadiusRatio"/> &gt; 0), this
    /// instead builds a ring/annular-sector as a single connected contour — outer arc forward, straight
    /// seam in to the inner boundary, inner arc backward, close — mirroring
    /// <see cref="EllipseAnnulusObject"/>'s own <c>TryComputeRing</c> technique exactly.
    /// <paramref name="showRadiusLines"/>/<paramref name="showChordLine"/> are ignored in that case:
    /// the ring's own two radial seams already close the shape, so a third line back to the center (or
    /// a chord) would be redundant/conflicting.
    /// </summary>
    private static SKPath BuildArcPath(SKRect rect, float startAngle, float sweep, bool showRadiusLines, bool showChordLine, SKRect? innerRect = null)
    {
        float addArcSweep = Math.Min(sweep, 359.9f);
        var path = new SKPath();

        if (innerRect is { } inner)
        {
            float sweepEndAngle = startAngle + addArcSweep;
            path.MoveTo(EllipseArcGeometry.PointOnEllipse(rect, startAngle));
            path.ArcTo(rect, startAngle, addArcSweep, forceMoveTo: false);
            path.LineTo(EllipseArcGeometry.PointOnEllipse(inner, sweepEndAngle));
            path.ArcTo(inner, sweepEndAngle, -addArcSweep, forceMoveTo: false);
            path.Close();
            return path;
        }

        path.AddArc(rect, startAngle, addArcSweep);

        if (showRadiusLines)
        {
            // Closes the fill/hit-test boundary through the center (pie/wedge shape).
            path.LineTo(rect.MidX, rect.MidY);
            path.Close();
        }
        else if (showChordLine)
        {
            // Closes the fill/hit-test boundary directly back to the arc's start (bow shape).
            path.Close();
        }

        if (showRadiusLines && showChordLine)
        {
            // The radius lines above already define the fill boundary; the chord is added here as a
            // separate stroke-only segment (it does not change the enclosed fill area).
            path.MoveTo(EllipseArcGeometry.PointOnEllipse(rect, startAngle));
            path.LineTo(EllipseArcGeometry.PointOnEllipse(rect, startAngle + sweep));
        }

        return path;
    }

    /// <summary>
    /// Resolves the current ellipse's local (unrotated) bounding rect, start angle, and sweep — all
    /// expressed in the local frame established by <see cref="TryComputeEllipseParams"/> — from the 4
    /// control points, or returns false if the geometry is degenerate (zero-size box or non-finite
    /// screen coordinates) and should not be rendered/hit-tested. <paramref name="centerScreen"/>/
    /// <paramref name="rotationAngle"/> are also returned so callers can rotate the local geometry (or
    /// an inverse-rotated query point) into/out of true screen space. <paramref name="handle2Angle"/>/
    /// <paramref name="handle3Angle"/> are the same nudge-adjusted local (pre-rotation) angles used to
    /// place the handle markers themselves — exposed so <see cref="ShowTangentLines"/> can draw each
    /// tangent line at the exact same position/angle the marker is drawn at, without re-deriving the
    /// coincidence-nudge logic separately.
    /// </summary>
    private bool TryComputeArc(ICoordinateTransform transform, out SKRect localRect, out float startAngle, out float sweep, out SKPoint centerScreen, out float rotationAngle, out global::Avalonia.Point[] handles, out float handle2Angle, out float handle3Angle)
    {
        localRect = default;
        startAngle = 0f;
        sweep = 0f;
        centerScreen = default;
        rotationAngle = 0f;
        handles = Array.Empty<global::Avalonia.Point>();
        handle2Angle = 0f;
        handle3Angle = 0f;

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);
        var s3 = transform.ChartToScreen(Points[3]);

        if (!IsFinite(s0) || !IsFinite(s1) || !IsFinite(s2) || !IsFinite(s3)) return false;
        if (!TryComputeEllipseParams(transform, out centerScreen, out rotationAngle, out var rx, out var ry)) return false;

        localRect = new SKRect(centerScreen.X - rx, centerScreen.Y - ry, centerScreen.X + rx, centerScreen.Y + ry);

        // Map the angle handles into the ellipse's local (unrotated) frame before measuring their
        // angle — AngleFromPoint/PointOnEllipse operate purely on the local, axis-aligned rect.
        var local2 = EllipseArcGeometry.RotatePoint(new SKPoint((float)s2.X, (float)s2.Y), centerScreen, -rotationAngle);
        var local3 = EllipseArcGeometry.RotatePoint(new SKPoint((float)s3.X, (float)s3.Y), centerScreen, -rotationAngle);

        startAngle = EllipseArcGeometry.AngleFromPoint(localRect, local2);
        float endAngle = EllipseArcGeometry.AngleFromPoint(localRect, local3);
        sweep = EllipseArcGeometry.ComputeSweep(startAngle, endAngle);

        // Handle markers for the two angle points are drawn at their boundary-projected position
        // (matching the arc curve itself, which is always built from PointOnEllipse), not at Points[2]/
        // [3]'s raw stored position — otherwise dragging the corner (Points[1]) changes the rect/
        // boundary every frame while Points[2]/[3]'s stored chart coordinates stay fixed, so the raw
        // screen position would lag behind the actual (re-derived) arc endpoints. The local boundary
        // points are rotated back into true screen space since handles are drawn outside the canvas's
        // rotation transform (see RenderArc).
        //
        // Points[2]/[3] default to the *same* boundary direction as each other (both computed from the
        // corner's own direction — see EllipseArcGeometry.ComputeChartSpaceBoundaryPoint), by design:
        // this collapses to a 360-degree sweep, so Arc mode's default shape is the full circle/ellipse
        // rather than a razor-thin sliver. Left un-adjusted, their MARKER positions would then land
        // exactly on top of Points[1]'s own corner handle (same angle => same boundary point),
        // rendering as a single dot and making the two circumference handles impossible to tell apart
        // or grab independently — especially now that they're always shown, not just once Arc mode is
        // enabled. So marker placement only (never the sweep/arc-path angles the shape itself is built
        // from) nudges the two apart, symmetric around the shared angle, whenever they're still
        // (near-)coincident; a single real drag on either one moves it to a genuinely different angle
        // and this nudge no longer applies.
        //
        // The sign assignment below (handle2 gets +nudge, handle3 gets -nudge) is deliberately chosen
        // to match the most natural first drag: a user grabbing whichever marker and continuing to pull
        // it further in the direction it's already nudged toward (rather than back through the other
        // marker) must carve a small sliver OUT of the full circle — i.e. the majority of the circle
        // stays filled — not collapse the filled region down to that sliver. ComputeSweep always reads
        // Points[2] as the sweep's start and Points[3] as its end, so which one gets the positive vs.
        // negative offset here determines which of the two directions is "natural"; the values were
        // picked, and can be verified, against that concrete drag scenario rather than derived from
        // sweep math in the abstract.
        handle2Angle = startAngle;
        handle3Angle = startAngle + sweep;
        if (sweep >= 359.9f)
        {
            handle2Angle = startAngle + CoincidentHandleMarkerNudgeDegrees;
            handle3Angle = startAngle - CoincidentHandleMarkerNudgeDegrees;
        }

        var boundary2Local = EllipseArcGeometry.PointOnEllipse(localRect, handle2Angle);
        var boundary3Local = EllipseArcGeometry.PointOnEllipse(localRect, handle3Angle);
        var boundary2 = EllipseArcGeometry.RotatePoint(boundary2Local, centerScreen, rotationAngle);
        var boundary3 = EllipseArcGeometry.RotatePoint(boundary3Local, centerScreen, rotationAngle);
        handles = new[] { s0, s1, new global::Avalonia.Point(boundary2.X, boundary2.Y), new global::Avalonia.Point(boundary3.X, boundary3.Y) };
        return true;
    }

    private static bool IsFinite(global::Avalonia.Point p)
        => !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y);
}

