using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 2点で結ばれるライン上に、上側・下側それぞれ独立したテキスト（文字列・フォントサイズ・
/// 配置・オフセット距離・回転モード）を配置する描画オブジェクト (UserEditableText)。
/// Top/Bottom側のプロパティ・キャッシュ済み<see cref="SKPaint"/>・描画呼び出しは
/// <see cref="LineTextAnnotationSet"/> に委譲し、曲線対応の同種ツールとロジックを共有する。
/// </summary>
public class LineTextObject : RelativeGeometricRenderer, ILineTextAnnotatedObject
{
    public override ChartObjectType Type => ChartObjectType.LineText;

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

    private ChartPoint? _topFrozenP1;
    private ChartPoint? _topFrozenP2;
    public bool TopPositionLocked
    {
        get => _annotations.TopPositionLocked;
        set
        {
            if (_annotations.TopPositionLocked == value) return;
            _annotations.TopPositionLocked = value;
            // トグルのたびにスナップショットを破棄し、次回DrawGeometryで「その時点のPoints」を
            // 新しいチャート座標基準として再キャプチャする。
            _topFrozenP1 = null;
            _topFrozenP2 = null;
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

    private ChartPoint? _bottomFrozenP1;
    private ChartPoint? _bottomFrozenP2;
    public bool BottomPositionLocked
    {
        get => _annotations.BottomPositionLocked;
        set
        {
            if (_annotations.BottomPositionLocked == value) return;
            _annotations.BottomPositionLocked = value;
            _bottomFrozenP1 = null;
            _bottomFrozenP2 = null;
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

    // PositionLocked時のみ使う、ライブのPointsとは独立な凍結パス。既定OFF時は一切触れない
    // （Reset/MoveTo/LineToが呼ばれないため、通常のホットパスへの追加コストはゼロ）。
    private readonly SKPath _topFrozenPath = new();
    private readonly SKPath _bottomFrozenPath = new();
    private bool _disposed;

    public LineTextObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        if (MatchBackgroundColor)
        {
            _cachedPaint.Color = DrawingThemeContext.ChartBackground;
        }

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, _cachedPaint);

        _cachedPath.MoveTo((float)p1.X, (float)p1.Y);
        _cachedPath.LineTo((float)p2.X, (float)p2.Y);

        SKPath topPath = _cachedPath;
        if (TopPositionLocked)
        {
            _topFrozenP1 ??= Points[0];
            _topFrozenP2 ??= Points[1];
            topPath = BuildFrozenPath(_topFrozenPath, _topFrozenP1.Value, _topFrozenP2.Value, transform);
        }

        SKPath bottomPath = _cachedPath;
        if (BottomPositionLocked)
        {
            _bottomFrozenP1 ??= Points[0];
            _bottomFrozenP2 ??= Points[1];
            bottomPath = BuildFrozenPath(_bottomFrozenPath, _bottomFrozenP1.Value, _bottomFrozenP2.Value, transform);
        }

        _annotations.RenderOnto(canvas, topPath, default, default, bottomPath, default, default);
    }

    private static SKPath BuildFrozenPath(SKPath frozenPath, ChartPoint frozenStart, ChartPoint frozenEnd, ICoordinateTransform transform)
    {
        var fp1 = transform.ChartToScreen(frozenStart);
        var fp2 = transform.ChartToScreen(frozenEnd);
        frozenPath.Reset();
        frozenPath.MoveTo((float)fp1.X, (float)fp1.Y);
        frozenPath.LineTo((float)fp2.X, (float)fp2.Y);
        return frozenPath;
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        var skPoint = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);
        var skP1 = new SKPoint((float)p1.X, (float)p1.Y);
        var skP2 = new SKPoint((float)p2.X, (float)p2.Y);

        double dist = BezierSplineMath.DistancePointToSegment(skPoint, skP1, skP2);
        return dist <= tolerance;
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
