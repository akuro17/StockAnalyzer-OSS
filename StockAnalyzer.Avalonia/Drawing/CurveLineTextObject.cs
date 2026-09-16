using System;
using System.Buffers;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 可変点数のクリックで曲線を引ける独立した描画ツール (UserEditableText)。
/// <see cref="PolylineObject"/> と同一の Catmull-Rom スプライン方式（<see cref="BezierSplineMath"/>）で
/// 曲線パスを構築し、そのパス上に上側・下側それぞれ独立したテキストを
/// <see cref="LineTextAnnotationSet"/> 経由で追従表示する。
/// </summary>
public class CurveLineTextObject : RelativeGeometricRenderer, ILineTextAnnotatedObject
{
    public override ChartObjectType Type => ChartObjectType.CurveLineText;

    // Smooth Line (Cubic Bézier / Catmull-Rom) properties — same semantics as PolylineObject.
    public bool IsSmooth { get; set; } = true;
    public double Tension { get; set; } = BezierSplineMath.DefaultTension;

    private readonly LineTextAnnotationSet _annotations = new();

    public string TopText
    {
        get => _annotations.TopText;
        set => _annotations.TopText = value;
    }

    public double TopFontSize
    {
        get => _annotations.TopFontSize;
        set => _annotations.TopFontSize = value;
    }

    public TextHorizontalAlignment TopAlignment
    {
        get => _annotations.TopAlignment;
        set => _annotations.TopAlignment = value;
    }

    public float TopOffsetPx
    {
        get => _annotations.TopOffsetPx;
        set => _annotations.TopOffsetPx = value;
    }

    public TextRotationMode TopRotationMode
    {
        get => _annotations.TopRotationMode;
        set => _annotations.TopRotationMode = value;
    }

    public Color TopTextColor
    {
        get => _annotations.TopTextColor;
        set => _annotations.TopTextColor = value;
    }

    public bool TopTextReverseOrder
    {
        get => _annotations.TopTextReverseOrder;
        set => _annotations.TopTextReverseOrder = value;
    }

    public TextManualOrientation TopTextOrientationOverride
    {
        get => _annotations.TopTextOrientationOverride;
        set => _annotations.TopTextOrientationOverride = value;
    }

    public bool TopPositionFixed
    {
        get => _annotations.TopPositionFixed;
        set => _annotations.TopPositionFixed = value;
    }

    private List<ChartPoint>? _topFrozenPoints;
    public bool TopPositionLocked
    {
        get => _annotations.TopPositionLocked;
        set
        {
            if (_annotations.TopPositionLocked == value) return;
            _annotations.TopPositionLocked = value;
            // トグルのたびにスナップショットを破棄し、次回DrawGeometryで「その時点のPoints」を
            // 新しいチャート座標基準として再キャプチャする。
            _topFrozenPoints = null;
        }
    }

    public bool TopExtendBeyondLine
    {
        get => _annotations.TopExtendBeyondLine;
        set => _annotations.TopExtendBeyondLine = value;
    }

    public string BottomText
    {
        get => _annotations.BottomText;
        set => _annotations.BottomText = value;
    }

    public double BottomFontSize
    {
        get => _annotations.BottomFontSize;
        set => _annotations.BottomFontSize = value;
    }

    public TextHorizontalAlignment BottomAlignment
    {
        get => _annotations.BottomAlignment;
        set => _annotations.BottomAlignment = value;
    }

    public float BottomOffsetPx
    {
        get => _annotations.BottomOffsetPx;
        set => _annotations.BottomOffsetPx = value;
    }

    public TextRotationMode BottomRotationMode
    {
        get => _annotations.BottomRotationMode;
        set => _annotations.BottomRotationMode = value;
    }

    public Color BottomTextColor
    {
        get => _annotations.BottomTextColor;
        set => _annotations.BottomTextColor = value;
    }

    public bool BottomTextReverseOrder
    {
        get => _annotations.BottomTextReverseOrder;
        set => _annotations.BottomTextReverseOrder = value;
    }

    public TextManualOrientation BottomTextOrientationOverride
    {
        get => _annotations.BottomTextOrientationOverride;
        set => _annotations.BottomTextOrientationOverride = value;
    }

    public bool BottomPositionFixed
    {
        get => _annotations.BottomPositionFixed;
        set => _annotations.BottomPositionFixed = value;
    }

    private List<ChartPoint>? _bottomFrozenPoints;
    public bool BottomPositionLocked
    {
        get => _annotations.BottomPositionLocked;
        set
        {
            if (_annotations.BottomPositionLocked == value) return;
            _annotations.BottomPositionLocked = value;
            _bottomFrozenPoints = null;
        }
    }

    public bool BottomExtendBeyondLine
    {
        get => _annotations.BottomExtendBeyondLine;
        set => _annotations.BottomExtendBeyondLine = value;
    }

    /// <summary>trueの場合、ライン本体の描画色を<see cref="DrawingThemeContext.ChartBackground"/>（テーマ変更に追従）に
    /// 置き換え、ラインをチャート背景に溶け込ませて視覚的に非表示にする。テキスト自体の色には影響しない。</summary>
    public bool MatchBackgroundColor { get; set; } = false;

    // PositionLocked時のみ使う、ライブのPointsとは独立な凍結パス。既定OFF時は一切触れない。
    private readonly SKPath _topFrozenPath = new();
    private readonly SKPath _bottomFrozenPath = new();

    // BuildFrozenPathAndSegments用の配列キャッシュ。frozenPoints自体はトグル時にのみ再キャプチャ
    // されるため、Countが変化しない限り(＝再トグルされない限り)これらの配列も再確保せず使い回す。
    private SKPoint[]? _topFrozenScreenPoints;
    private float[]? _topFrozenSegmentEndArcLengths;
    private float[]? _topFrozenSegmentDirectionAngles;
    private SKPoint[]? _bottomFrozenScreenPoints;
    private float[]? _bottomFrozenSegmentEndArcLengths;
    private float[]? _bottomFrozenSegmentDirectionAngles;
    private bool _disposed;

    public CurveLineTextObject() : base()
    {
    }

    public CurveLineTextObject(IEnumerable<ChartPoint> points) : base()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    public void AddPoint(ChartPoint point) => Points.Add(point);

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        if (MatchBackgroundColor)
        {
            _cachedPaint.Color = DrawingThemeContext.ChartBackground;
        }

        int count = Points.Count;
        SKPoint[]? rented = null;
        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rented = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
            }

            _cachedPaint.StrokeJoin = SKStrokeJoin.Round;

            if (IsSmooth && count >= 3)
            {
                BezierSplineMath.BuildCatmullRomSplinePath(_cachedPath, screenPoints, Tension);
            }
            else
            {
                _cachedPath.MoveTo(screenPoints[0]);
                for (int i = 1; i < count; i++)
                {
                    _cachedPath.LineTo(screenPoints[i]);
                }
            }

            canvas.DrawPath(_cachedPath, _cachedPaint);

            // FollowLineAutoFlip用: 隣接制御点ペアごとの区間境界（弧長）・向きを算出し、Top/Bottom
            // 両方のテキスト描画に共有する（同一パス・同一区間構成のため1回の計算で足りる）。
            int maxSegments = count - 1;
            float[]? rentedBoundaries = null;
            float[]? rentedAngles = null;
            Span<float> segmentEndArcLengths = maxSegments <= 128
                ? stackalloc float[maxSegments]
                : (rentedBoundaries = ArrayPool<float>.Shared.Rent(maxSegments)).AsSpan(0, maxSegments);
            Span<float> segmentDirectionAngles = maxSegments <= 128
                ? stackalloc float[maxSegments]
                : (rentedAngles = ArrayPool<float>.Shared.Rent(maxSegments)).AsSpan(0, maxSegments);

            try
            {
                int segCount = BezierSplineMath.ComputeSegmentArcLengthsAndDirections(
                    screenPoints, IsSmooth, Tension, segmentEndArcLengths, segmentDirectionAngles);

                SKPath topPath = _cachedPath;
                ReadOnlySpan<float> topSegEnd = segmentEndArcLengths.Slice(0, segCount);
                ReadOnlySpan<float> topSegAngles = segmentDirectionAngles.Slice(0, segCount);
                if (TopPositionLocked)
                {
                    int frozenSegCount = BuildFrozenPathAndSegments(ref _topFrozenPoints, Points, transform, IsSmooth, Tension, _topFrozenPath,
                        ref _topFrozenScreenPoints, ref _topFrozenSegmentEndArcLengths, ref _topFrozenSegmentDirectionAngles,
                        out float[] topFrozenSegEnd, out float[] topFrozenSegAngles);
                    topPath = _topFrozenPath;
                    topSegEnd = topFrozenSegEnd.AsSpan(0, frozenSegCount);
                    topSegAngles = topFrozenSegAngles.AsSpan(0, frozenSegCount);
                }

                SKPath bottomPath = _cachedPath;
                ReadOnlySpan<float> bottomSegEnd = segmentEndArcLengths.Slice(0, segCount);
                ReadOnlySpan<float> bottomSegAngles = segmentDirectionAngles.Slice(0, segCount);
                if (BottomPositionLocked)
                {
                    int frozenSegCount = BuildFrozenPathAndSegments(ref _bottomFrozenPoints, Points, transform, IsSmooth, Tension, _bottomFrozenPath,
                        ref _bottomFrozenScreenPoints, ref _bottomFrozenSegmentEndArcLengths, ref _bottomFrozenSegmentDirectionAngles,
                        out float[] bottomFrozenSegEnd, out float[] bottomFrozenSegAngles);
                    bottomPath = _bottomFrozenPath;
                    bottomSegEnd = bottomFrozenSegEnd.AsSpan(0, frozenSegCount);
                    bottomSegAngles = bottomFrozenSegAngles.AsSpan(0, frozenSegCount);
                }

                _annotations.RenderOnto(canvas, topPath, topSegEnd, topSegAngles, bottomPath, bottomSegEnd, bottomSegAngles);
            }
            finally
            {
                if (rentedBoundaries != null) ArrayPool<float>.Shared.Return(rentedBoundaries);
                if (rentedAngles != null) ArrayPool<float>.Shared.Return(rentedAngles);
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// TopPositionLocked/BottomPositionLockedが有効な側について、ロックされた時点の制御点
    /// （<paramref name="frozenPoints"/>、初回は<paramref name="livePoints"/>のスナップショットを
    /// 遅延キャプチャする）から、ライブの<see cref="DrawGeometry"/>と同じ構築ロジック（スプライン/
    /// 直線・区間境界情報）で凍結パスを再構築する。パン・ズームでは<paramref name="transform"/>経由で
    /// 毎フレーム再投影されるため追従するが、ライブの制御点編集には一切追従しない。
    /// 本機能はユーザーが明示的に有効化した場合にのみ実行される低頻度パスだが、有効化された後は
    /// 通常のレンダリングループと同じ頻度（毎フレーム）で呼び出されるため、作業用配列は
    /// <paramref name="screenPointsCache"/>等の呼び出し元フィールドに保持させ、<paramref name="frozenPoints"/>の
    /// 点数が変化した場合（＝再トグルで新しいスナップショットが取られた場合）のみ再確保する。
    /// </summary>
    private static int BuildFrozenPathAndSegments(
        ref List<ChartPoint>? frozenPoints, List<ChartPoint> livePoints, ICoordinateTransform transform,
        bool isSmooth, double tension, SKPath outputPath,
        ref SKPoint[]? screenPointsCache, ref float[]? segmentEndArcLengthsCache, ref float[]? segmentDirectionAnglesCache,
        out float[] segmentEndArcLengths, out float[] segmentDirectionAngles)
    {
        frozenPoints ??= new List<ChartPoint>(livePoints);
        int count = frozenPoints.Count;

        if (screenPointsCache == null || screenPointsCache.Length != count)
        {
            screenPointsCache = new SKPoint[count];
        }
        SKPoint[] screenPoints = screenPointsCache;
        for (int i = 0; i < count; i++)
        {
            var pt = transform.ChartToScreen(frozenPoints[i]);
            screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
        }

        outputPath.Reset();
        if (isSmooth && count >= 3)
        {
            BezierSplineMath.BuildCatmullRomSplinePath(outputPath, screenPoints, tension);
        }
        else
        {
            outputPath.MoveTo(screenPoints[0]);
            for (int i = 1; i < count; i++)
            {
                outputPath.LineTo(screenPoints[i]);
            }
        }

        int maxSegments = Math.Max(count - 1, 0);
        if (segmentEndArcLengthsCache == null || segmentEndArcLengthsCache.Length != maxSegments)
        {
            segmentEndArcLengthsCache = new float[maxSegments];
            segmentDirectionAnglesCache = new float[maxSegments];
        }
        segmentEndArcLengths = segmentEndArcLengthsCache;
        segmentDirectionAngles = segmentDirectionAnglesCache!;
        return maxSegments > 0
            ? BezierSplineMath.ComputeSegmentArcLengthsAndDirections(screenPoints, isSmooth, tension, segmentEndArcLengths, segmentDirectionAngles)
            : 0;
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        int count = Points.Count;
        SKPoint skScreenPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);

        SKPoint[]? rented = null;
        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rented = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var p = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)p.X, (float)p.Y);
            }

            if (IsSmooth && count >= 3)
            {
                for (int i = 0; i < count - 1; i++)
                {
                    SKPoint pCurr = screenPoints[i];
                    SKPoint pNext = screenPoints[i + 1];

                    if (BezierSplineMath.DistanceSquared(pCurr, pNext) < BezierSplineMath.EpsilonSquared)
                        continue;

                    SKPoint pPrev = (i == 0)
                        ? new SKPoint(2f * screenPoints[0].X - screenPoints[1].X, 2f * screenPoints[0].Y - screenPoints[1].Y)
                        : screenPoints[i - 1];

                    SKPoint pNextNext = (i == count - 2)
                        ? new SKPoint(2f * screenPoints[count - 1].X - screenPoints[count - 2].X, 2f * screenPoints[count - 1].Y - screenPoints[count - 2].Y)
                        : screenPoints[i + 2];

                    BezierSplineMath.CalculateControlPoints(pPrev, pCurr, pNext, pNextNext, Tension, out var c1, out var c2);
                    if (BezierSplineMath.HitTestCubicSegment(skScreenPt, pCurr, c1, c2, pNext, tolerance))
                        return true;
                }
                return false;
            }
            else
            {
                for (int i = 0; i < count - 1; i++)
                {
                    if (BezierSplineMath.DistancePointToSegment(skScreenPt, screenPoints[i], screenPoints[i + 1]) <= tolerance)
                        return true;
                }
                return false;
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _annotations.Dispose();
        _topFrozenPath.Dispose();
        _bottomFrozenPath.Dispose();
        base.Dispose();
    }
}
