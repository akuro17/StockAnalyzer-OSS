using System;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 上側(Top)・下側(Bottom)完全独立のパス追従テキスト注釈。<see cref="LineTextObject"/> と、
/// 曲線対応の同種ツールの双方から共有され、Text/FontSize/Alignment/OffsetPx/RotationMode の
/// 重複実装（SSoT違反）を避けるために抽出された内部コンポーネント。IChartObject 自体ではなく、
/// 各オブジェクトが1インスタンスをフィールドとして保持し、<see cref="RenderOnto"/> に自身の
/// (毎フレーム再構築される) <see cref="SKPath"/> を渡すことで描画を委譲する。
/// </summary>
internal sealed class LineTextAnnotationSet : IDisposable
{
    private string _topText = string.Empty;
    public string TopText
    {
        get => _topText;
        set => _topText = SanitizeSingleLine(value);
    }

    private double _topFontSize = DrawingThemeContext.DrawingFontSize;
    public double TopFontSize
    {
        get => _topFontSize;
        set
        {
            if (Math.Abs(_topFontSize - value) < 1e-6) return;
            _topFontSize = value;
            _topTextPaint.TextSize = (float)value;
        }
    }

    public TextHorizontalAlignment TopAlignment { get; set; } = TextHorizontalAlignment.Left;
    public float TopOffsetPx { get; set; } = ChartConstants.DefaultLineTextOffsetPx;
    public TextRotationMode TopRotationMode { get; set; } = TextRotationMode.AlwaysUpright;
    public bool TopTextReverseOrder { get; set; } = false;
    public TextManualOrientation TopTextOrientationOverride { get; set; } = TextManualOrientation.Default;

    private bool _topPositionFixed;
    private float? _topFixedReferenceAngle;
    public bool TopPositionFixed
    {
        get => _topPositionFixed;
        set
        {
            if (_topPositionFixed == value) return;
            _topPositionFixed = value;
            // トグルのたびに参照角度を破棄し、次回RenderOntoで「その時点の表示側」を新しい基準として
            // 再キャプチャする（OFF→ON時に古い基準が残らないようにする）。
            _topFixedReferenceAngle = null;
        }
    }

    /// <summary>チャート座標スナップショットへの固定フラグそのもの。スナップショットの取得・凍結パスの
    /// 再構築は、線の形状（直線/スプライン）を知っている呼び出し側（LineTextObject/CurveLineTextObject）
    /// の責務であり、このクラスはフラグの保持のみを行う。</summary>
    public bool TopPositionLocked { get; set; }

    /// <summary>trueの場合、パスの長さを超えるTopTextの文字を非表示にせず、端点の接線方向へ外挿した
    /// 位置に描画する（<see cref="PathTextRenderer.DrawTextOnPath"/>の<c>ExtendBeyondPath</c>参照）。
    /// 既定false。</summary>
    public bool TopExtendBeyondLine { get; set; }

    private Color _topTextColor = DrawingThemeContext.MainTextColor;
    public Color TopTextColor
    {
        get => _topTextColor;
        set
        {
            if (_topTextColor == value) return;
            _topTextColor = value;
            _topTextPaint.Color = new SKColor(value.R, value.G, value.B, value.A);
        }
    }

    private string _bottomText = string.Empty;
    public string BottomText
    {
        get => _bottomText;
        set => _bottomText = SanitizeSingleLine(value);
    }

    /// <summary>
    /// PathTextRendererはTop/Bottomテキストを1文字ずつパス弧長上にグリフとして直接描画するため、
    /// 改行制御文字（グリフを持たない）を含んだまま渡すと文字化け（豆腐文字）になる。改行を
    /// スペースへ置換し、パス追従テキストが常に単一行であることを保証する。
    /// </summary>
    private static string SanitizeSingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
    }

    private double _bottomFontSize = DrawingThemeContext.DrawingFontSize;
    public double BottomFontSize
    {
        get => _bottomFontSize;
        set
        {
            if (Math.Abs(_bottomFontSize - value) < 1e-6) return;
            _bottomFontSize = value;
            _bottomTextPaint.TextSize = (float)value;
        }
    }

    public TextHorizontalAlignment BottomAlignment { get; set; } = TextHorizontalAlignment.Left;
    public float BottomOffsetPx { get; set; } = ChartConstants.DefaultLineTextOffsetPx;
    public TextRotationMode BottomRotationMode { get; set; } = TextRotationMode.AlwaysUpright;
    public bool BottomTextReverseOrder { get; set; } = false;
    public TextManualOrientation BottomTextOrientationOverride { get; set; } = TextManualOrientation.Default;

    private bool _bottomPositionFixed;
    private float? _bottomFixedReferenceAngle;
    public bool BottomPositionFixed
    {
        get => _bottomPositionFixed;
        set
        {
            if (_bottomPositionFixed == value) return;
            _bottomPositionFixed = value;
            _bottomFixedReferenceAngle = null;
        }
    }

    /// <summary>TopPositionLockedと同様のフラグをBottomText側に独立して持つ。</summary>
    public bool BottomPositionLocked { get; set; }

    /// <summary>TopExtendBeyondLineと同様のフラグをBottomText側に独立して持つ。</summary>
    public bool BottomExtendBeyondLine { get; set; }

    private Color _bottomTextColor = DrawingThemeContext.MainTextColor;
    public Color BottomTextColor
    {
        get => _bottomTextColor;
        set
        {
            if (_bottomTextColor == value) return;
            _bottomTextColor = value;
            _bottomTextPaint.Color = new SKColor(value.R, value.G, value.B, value.A);
        }
    }

    // Independent paints so Top/Bottom font size never cross-contaminate each other's glyph metrics.
    private readonly SKPaint _topTextPaint;
    private readonly SKPaint _bottomTextPaint;
    private bool _disposed;

    public LineTextAnnotationSet()
    {
        _topTextPaint = new SKPaint
        {
            Color = DrawingThemeContext.MainTextSkColor,
            IsAntialias = true,
            TextSize = (float)_topFontSize,
            Typeface = SKTypeface.Default
        };

        _bottomTextPaint = new SKPaint
        {
            Color = DrawingThemeContext.MainTextSkColor,
            IsAntialias = true,
            TextSize = (float)_bottomFontSize,
            Typeface = SKTypeface.Default
        };
    }

    /// <summary>指定パスに沿って、空でない側だけ独立した設定で描画する。</summary>
    public void RenderOnto(SKCanvas canvas, SKPath path)
        => RenderOnto(canvas, path, default, default, path, default, default);

    /// <summary>
    /// 指定パスに沿って、空でない側だけ独立した設定で描画する。<paramref name="segmentEndArcLengths"/>/
    /// <paramref name="segmentDirectionAngles"/>は曲線(CurveLineTextObject)の隣接制御点ペアごとの
    /// 区間境界情報で、FollowLineAutoFlip時の反転判定をより局所的に行うために使う。Top/Bottomが
    /// 同一のパス・同一の区間構成を共有する場合（＝どちらもPositionLockedでない通常のケース）に使う便宜
    /// オーバーロード。
    /// </summary>
    public void RenderOnto(SKCanvas canvas, SKPath path, ReadOnlySpan<float> segmentEndArcLengths, ReadOnlySpan<float> segmentDirectionAngles)
        => RenderOnto(canvas, path, segmentEndArcLengths, segmentDirectionAngles, path, segmentEndArcLengths, segmentDirectionAngles);

    /// <summary>
    /// Top/Bottomそれぞれに独立したパス（および対応する区間境界情報）を指定して描画する。
    /// TopPositionLocked/BottomPositionLockedが有効な側は、呼び出し側が凍結スナップショットから
    /// 再構築した専用のパスを渡すことで、ライブの制御点編集から独立させることができる。
    /// </summary>
    public void RenderOnto(
        SKCanvas canvas,
        SKPath topPath, ReadOnlySpan<float> topSegmentEndArcLengths, ReadOnlySpan<float> topSegmentDirectionAngles,
        SKPath bottomPath, ReadOnlySpan<float> bottomSegmentEndArcLengths, ReadOnlySpan<float> bottomSegmentDirectionAngles)
    {
        if (!string.IsNullOrEmpty(_topText))
        {
            float topSign = ResolveFixedSign(topPath, _topPositionFixed, ref _topFixedReferenceAngle);
            PathTextRenderer.DrawTextOnPath(canvas, _topText, topPath, _topTextPaint,
                new PathTextOptions(TopAlignment, -TopOffsetPx * topSign, RotationMode: TopRotationMode, ManualReverseOrder: TopTextReverseOrder, OrientationOverride: TopTextOrientationOverride, ExtendBeyondPath: TopExtendBeyondLine),
                topSegmentEndArcLengths, topSegmentDirectionAngles);
        }

        if (!string.IsNullOrEmpty(_bottomText))
        {
            float bottomSign = ResolveFixedSign(bottomPath, _bottomPositionFixed, ref _bottomFixedReferenceAngle);
            PathTextRenderer.DrawTextOnPath(canvas, _bottomText, bottomPath, _bottomTextPaint,
                new PathTextOptions(BottomAlignment, BottomOffsetPx * bottomSign, RotationMode: BottomRotationMode, ManualReverseOrder: BottomTextReverseOrder, OrientationOverride: BottomTextOrientationOverride, ExtendBeyondPath: BottomExtendBeyondLine),
                bottomSegmentEndArcLengths, bottomSegmentDirectionAngles);
        }
    }

    /// <summary>
    /// PositionFixedが有効な場合、チェックを入れた時点（＝最初にこのメソッドが呼ばれた時点）のパス
    /// 起点接線角度を参照値として記憶し、以降のフレームで現在の接線角度と比較する。角度の左右成分の
    /// 符号（FollowLineAutoFlipと同一の±90°しきい値）が参照値から反転していれば-1を返し、
    /// 呼び出し側でNormalOffsetの符号を反転させて物理的な表示側を維持する。無効時・パス退化時は
    /// 常に+1（従来通りパスの法線にそのまま追従）。
    /// </summary>
    private static float ResolveFixedSign(SKPath path, bool positionFixed, ref float? referenceAngle)
    {
        if (!positionFixed)
        {
            return 1f;
        }

        if (!PathTextRenderer.TryGetStartTangentAngleDegrees(path, out float currentAngle))
        {
            return 1f;
        }

        if (referenceAngle is not { } reference)
        {
            referenceAngle = currentAngle;
            return 1f;
        }

        bool referenceLeftward = MathF.Abs(reference) > 90f;
        bool currentLeftward = MathF.Abs(currentAngle) > 90f;
        return referenceLeftward != currentLeftward ? -1f : 1f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _topTextPaint.Dispose();
        _bottomTextPaint.Dispose();
    }
}
