using System;
using System.Buffers;
using System.Collections.Generic;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Zero-Allocation Text-on-Path 描画エンジン
/// 曲線パスまたは直線パスに沿ってテキスト文字（グリフ）を接線角度および法線オフセットを適用して配置・描画します。
/// </summary>
public static class PathTextRenderer
{
    // internal (not private): BezierSplineMath.ComputeSegmentArcLengthsAndDirections shares this
    // same radians-to-degrees conversion when computing segment direction angles, and reuses this
    // single definition instead of duplicating it (SSoT).
    internal const float RadToDeg = (float)(180.0 / Math.PI);
    private const float Epsilon = 1e-6f;

    private static readonly string[] AsciiCharStrings = new string[128];

    // paint.Typeface が特定の文字（日本語等）のグリフを持たない場合のフォールバック先を
    // 文字単位でキャッシュする。SKFontManager.MatchCharacter() はフォント列挙を伴うため、
    // 同一文字に対して毎フレーム呼び出すことを避ける（一度解決したフォントは実行中不変）。
    private static readonly Dictionary<char, SKTypeface?> s_fallbackTypefaceCache = new();

    [ThreadStatic]
    private static SKPathMeasure? t_pathMeasure;

    static PathTextRenderer()
    {
        for (int i = 0; i < 128; i++)
        {
            AsciiCharStrings[i] = ((char)i).ToString();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static string GetCharString(char c)
    {
        if (c < 128)
        {
            return AsciiCharStrings[c];
        }
        return c.ToString();
    }

    /// <summary>
    /// 指定文字を描画できるSKTypefaceを解決する。primaryが対応していればそのまま使用し、
    /// 対応していない場合（例: 既定フォントが日本語グリフを持たない）はシステムフォントマネージャーで
    /// フォールバックフォントを検索する。解決結果は文字単位でキャッシュする。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static SKTypeface ResolveTypefaceForChar(SKTypeface primary, char c)
    {
        if (primary.GetGlyph(c) != 0)
        {
            return primary;
        }

        if (s_fallbackTypefaceCache.TryGetValue(c, out var cached))
        {
            return cached ?? primary;
        }

        var fallback = SKFontManager.Default.MatchCharacter(c);
        s_fallbackTypefaceCache[c] = fallback;
        return fallback ?? primary;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static SKPathMeasure GetPathMeasure(SKPath path)
    {
        var measure = t_pathMeasure ??= new SKPathMeasure();
        measure.SetPath(path, false);
        return measure;
    }

    /// <summary>
    /// パス起点(s=0)における接線角度(度)を取得する。<see cref="ILineTextAnnotatedObject.TopPositionFixed"/>/
    /// <c>BottomPositionFixed</c>が、制御点編集によるパス進行方向の反転を検出するために使う判定基準を
    /// 提供する。DrawTextOnPath内のFollowLineAutoFlipフォールバック分岐と同一のスレッドローカル
    /// <see cref="SKPathMeasure"/>・角度算出ロジックを再利用する（SSoT、重複実装を避ける）。
    /// </summary>
    internal static bool TryGetStartTangentAngleDegrees(SKPath path, out float angleDegrees)
    {
        angleDegrees = 0f;
        if (path == null || path.IsEmpty)
        {
            return false;
        }

        var measure = GetPathMeasure(path);
        if (measure.Length <= Epsilon)
        {
            return false;
        }

        if (!measure.GetPositionAndTangent(0f, out _, out SKPoint tangent))
        {
            return false;
        }

        float tangentLen = MathF.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
        if (tangentLen <= Epsilon)
        {
            return false;
        }

        angleDegrees = MathF.Atan2(tangent.Y, tangent.X) * RadToDeg;
        return true;
    }

    /// <summary>
    /// パスに沿ってテキストを描画します。
    /// </summary>
    public static void DrawTextOnPath(
        SKCanvas canvas,
        string text,
        SKPath path,
        SKPaint paint,
        PathTextOptions options = default,
        ReadOnlySpan<float> segmentEndArcLengths = default,
        ReadOnlySpan<float> segmentDirectionAngles = default)
    {
        if (canvas == null || paint == null || path == null || path.IsEmpty || string.IsNullOrEmpty(text))
        {
            return;
        }

        var measure = GetPathMeasure(path);
        float pathLength = measure.Length;
        if (pathLength <= Epsilon)
        {
            return;
        }

        int length = text.Length;
        float[]? rentedWidths = null;
        Span<float> widths = length <= 128
            ? stackalloc float[length]
            : (rentedWidths = ArrayPool<float>.Shared.Rent(length)).AsSpan(0, length);

        // paint.Typeface defaults to null (not SKTypeface.Default) when the caller never assigns
        // it explicitly; Skia only substitutes its own default at draw time in that case. Treat
        // null the same as SKTypeface.Default for glyph-support checks, but restore the original
        // (possibly-null) value afterward rather than permanently overwriting it.
        SKTypeface? originalTypeface = paint.Typeface;
        SKTypeface effectivePrimaryTypeface = originalTypeface ?? SKTypeface.Default;

        try
        {
            float totalTextWidth = 0f;
            for (int i = 0; i < length; i++)
            {
                // Measure each glyph via Span, resolving a fallback font per-character when the
                // configured typeface lacks the glyph (e.g. Latin-only default font + Japanese text).
                paint.Typeface = ResolveTypefaceForChar(effectivePrimaryTypeface, text[i]);
                float w = paint.MeasureText(text.AsSpan(i, 1));
                widths[i] = w;
                totalTextWidth += w;
            }

            // 開始基準弧長 s0 の算出
            float s0 = options.Alignment switch
            {
                TextHorizontalAlignment.Left => options.StartMarginPx,
                TextHorizontalAlignment.Right => pathLength - totalTextWidth - options.EndMarginPx,
                _ => (pathLength - totalTextWidth) / 2.0f // Center (default)
            };

            // FollowLineAutoFlip: パス区間の向き（隣接制御点ペアのベクトル）を1回だけ判定する。
            // 区間境界情報(segmentEndArcLengths/segmentDirectionAngles)が渡された場合（曲線の
            // 複数区間対応）は、テキストの中心弧長位置が属する区間の制御点ペアベクトルの角度を
            // 用いる。渡されない場合（直線、または区間情報を持たない旧来の呼び出し元）は、
            // パス起点(s=0)の接線角度にフォールバックする。直線は制御点ペアが1組のみのため、
            // この起点接線は「隣接制御点ペアのベクトル」の角度と数学的に完全に一致する。
            bool needsFlip = false;
            if (options.RotationMode == TextRotationMode.FollowLineAutoFlip)
            {
                float centerS = s0 + totalTextWidth / 2f;
                float? segmentAngle = null;

                if (!segmentEndArcLengths.IsEmpty && segmentEndArcLengths.Length == segmentDirectionAngles.Length)
                {
                    int segIndex = segmentEndArcLengths.Length - 1;
                    for (int k = 0; k < segmentEndArcLengths.Length; k++)
                    {
                        if (centerS <= segmentEndArcLengths[k]) { segIndex = k; break; }
                    }
                    segmentAngle = segmentDirectionAngles[segIndex];
                }
                else if (measure.GetPositionAndTangent(0f, out _, out SKPoint startTangent))
                {
                    float startTangentLen = MathF.Sqrt(startTangent.X * startTangent.X + startTangent.Y * startTangent.Y);
                    if (startTangentLen > Epsilon)
                    {
                        segmentAngle = MathF.Atan2(startTangent.Y, startTangent.X) * RadToDeg;
                    }
                }

                if (segmentAngle.HasValue)
                {
                    needsFlip = MathF.Abs(segmentAngle.Value) > 90f;
                }
            }

            // ManualReverseOrder: RotationModeに関わらず、文字の並び順だけをもう一段反転する
            // 手動オーバーライド（回転角の補正には影響させない）。needsFlipとのXORにより、
            // 自動判定が反転させた場合は元の順序に戻し、反転させなかった場合は反転させる。
            bool needsReorder = needsFlip ^ options.ManualReverseOrder;

            float currentS = s0;
            float prevAngle = 0f;
            bool hasPrevAngle = false;

            // ExtendBeyondPath用: パス範囲外の文字を、超えた側の端点(s=0またはs=pathLength)における
            // 接線方向へ線形外挿するために使う。端点の位置・接線は最初に必要になった時点で1回だけ
            // 計算し、以降のグリフで使い回す(毎グリフでの再計算を避ける)。
            bool hasStartAnchor = false;
            SKPoint startAnchorPos = default, startAnchorTangent = default;
            bool hasEndAnchor = false;
            SKPoint endAnchorPos = default, endAnchorTangent = default;

            for (int i = 0; i < length; i++)
            {
                // FollowLineAutoFlip自動判定またはManualReverseOrder手動指定時は文字の並び順を
                // 逆に読み替える（"AB"→"BA"の弧長配置）。弧長sの積算方向・急曲率ガードは変更せず、
                // どのソース文字をその位置に描くかだけを入れ替えることで、文字列コピーや追加の
                // ヒープ割り当てなしに反転を実現する。
                int srcIndex = needsReorder ? (length - 1 - i) : i;
                float w = widths[srcIndex];

                // グリフごとにフォールバックフォントを解決（幅計測パスと同じ解決結果になる）。
                // 以降のFontSpacing参照・DrawText呼び出しの両方で同じタイプフェースを使う。
                paint.Typeface = ResolveTypefaceForChar(effectivePrimaryTypeface, text[srcIndex]);

                // AlwaysUpright時、文字は接線角度に関わらず常に水平のまま描画される。垂直に近い
                // パスで文字の幅だけを弧長として消費すると、文字自身の高さの分だけ次の文字と
                // 重なってしまう。現在位置での接線方向へ、文字の(幅×高さ)矩形を投影した弧長を
                // 消費量として用いることで、パスの角度に関わらず重なりを防ぐ。
                float advance = w;
                if (options.RotationMode == TextRotationMode.AlwaysUpright
                    && measure.GetPositionAndTangent(currentS, out _, out SKPoint aheadTangent))
                {
                    float aheadLen = MathF.Sqrt(aheadTangent.X * aheadTangent.X + aheadTangent.Y * aheadTangent.Y);
                    if (aheadLen > Epsilon)
                    {
                        float cosT = MathF.Abs(aheadTangent.X / aheadLen);
                        float sinT = MathF.Abs(aheadTangent.Y / aheadLen);
                        advance = w * cosT + paint.FontSpacing * sinT;
                    }
                }

                float glyphCenterS = currentS + advance / 2.0f;
                currentS += advance;

                bool outOfRange = glyphCenterS < 0f || glyphCenterS > pathLength;

                // 境界条件・特異点回避ガード。ExtendBeyondPathが有効な場合、範囲外の文字を非表示に
                // せず、端点の接線方向へ外挿した位置に描画するため、この時点ではスキップしない。
                if (!options.ExtendBeyondPath)
                {
                    if (options.ClipOverflow)
                    {
                        if (glyphCenterS - advance / 2.0f < 0f || glyphCenterS + advance / 2.0f > pathLength)
                        {
                            continue;
                        }
                    }

                    if (outOfRange)
                    {
                        continue;
                    }
                }

                SKPoint pos;
                SKPoint tangent;
                if (outOfRange && options.ExtendBeyondPath)
                {
                    if (glyphCenterS < 0f)
                    {
                        if (!hasStartAnchor)
                        {
                            if (!measure.GetPositionAndTangent(0f, out startAnchorPos, out startAnchorTangent))
                            {
                                continue;
                            }
                            hasStartAnchor = true;
                        }

                        float startTangentLen = MathF.Sqrt(startAnchorTangent.X * startAnchorTangent.X + startAnchorTangent.Y * startAnchorTangent.Y);
                        pos = startTangentLen > Epsilon
                            ? new SKPoint(startAnchorPos.X + startAnchorTangent.X / startTangentLen * glyphCenterS, startAnchorPos.Y + startAnchorTangent.Y / startTangentLen * glyphCenterS)
                            : startAnchorPos;
                        tangent = startAnchorTangent;
                    }
                    else
                    {
                        if (!hasEndAnchor)
                        {
                            if (!measure.GetPositionAndTangent(pathLength, out endAnchorPos, out endAnchorTangent))
                            {
                                continue;
                            }
                            hasEndAnchor = true;
                        }

                        float endTangentLen = MathF.Sqrt(endAnchorTangent.X * endAnchorTangent.X + endAnchorTangent.Y * endAnchorTangent.Y);
                        float delta = glyphCenterS - pathLength;
                        pos = endTangentLen > Epsilon
                            ? new SKPoint(endAnchorPos.X + endAnchorTangent.X / endTangentLen * delta, endAnchorPos.Y + endAnchorTangent.Y / endTangentLen * delta)
                            : endAnchorPos;
                        tangent = endAnchorTangent;
                    }
                }
                else if (!measure.GetPositionAndTangent(glyphCenterS, out pos, out tangent))
                {
                    continue;
                }

                float tangentLen = MathF.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
                float angle;
                if (tangentLen <= Epsilon)
                {
                    angle = hasPrevAngle ? prevAngle : 0f;
                }
                else
                {
                    angle = MathF.Atan2(tangent.Y, tangent.X) * RadToDeg;

                    // 急曲率・自己交差防止ガード: 直前角度との差分が急激な場合は直前角度をホールド
                    if (hasPrevAngle)
                    {
                        float deltaAngle = MathF.Abs(angle - prevAngle);
                        if (deltaAngle > 180f) deltaAngle = 360f - deltaAngle;
                        if (deltaAngle > 60f)
                        {
                            angle = prevAngle;
                        }
                    }
                }

                prevAngle = angle;
                hasPrevAngle = true;

                // 単位法線ベクトル N(s) = (-Ty, Tx)
                SKPoint normal = tangentLen > Epsilon
                    ? new SKPoint(-tangent.Y / tangentLen, tangent.X / tangentLen)
                    : new SKPoint(0, -1);

                // 法線オフセット適用（回転モードに関わらず、配置位置は幾何学的な法線に従う）。
                // NormalOffsetはベースラインからの距離であり、フォントサイズが大きいほど文字の
                // 昇部・降部（ascent/descent）がベースラインより遠くまで伸びるため、指定オフセット
                // だけではフォントサイズが大きい場合にラインと重なる。フォントの行送り
                // (FontSpacing)の半分を追加のクリアランスとして常に加算する。
                float clearance = paint.FontSpacing / 2f;
                float effectiveOffset = options.NormalOffset;
                if (effectiveOffset > 0f) effectiveOffset += clearance;
                else if (effectiveOffset < 0f) effectiveOffset -= clearance;

                float drawX = pos.X + normal.X * effectiveOffset;
                float drawY = pos.Y + normal.Y * effectiveOffset;

                // AlwaysUpright時は表示用の回転角を常に0（水平・読める向き）に固定する。ラインが
                // 垂直に近い角度でも文字が横倒しにならないようにするため、±90°への正規化ではなく
                // 完全固定とする。FollowLineAutoFlip時はパスの局所接線への追従は維持しつつ、
                // needsFlip（区間全体の向き判定）が真の場合のみ+180°補正する。急曲率ガード
                // （上のprevAngle追跡）は幾何学的な生の接線角度で継続させる必要があるため、
                // ここで別変数に分離する。
                float displayAngle = options.RotationMode switch
                {
                    TextRotationMode.AlwaysUpright => 0f,
                    TextRotationMode.FollowLineAutoFlip => needsFlip ? angle + 180f : angle,
                    _ => angle
                };

                canvas.Save();
                canvas.Translate(drawX, drawY);
                canvas.RotateDegrees(displayAngle);

                // OrientationOverride: RotationMode/ManualReverseOrderとは独立な、表現目的の
                // 追加変換。位置・文字の並び順（srcIndex）には一切影響しない。
                switch (options.OrientationOverride)
                {
                    case TextManualOrientation.Rotate180:
                        canvas.RotateDegrees(180f);
                        break;
                    case TextManualOrientation.Mirror:
                        // ローカルX軸（ラインと平行な、文字送り方向の軸）で反射。文字自体が
                        // 左右反転した鏡文字になる（判読性は設計上の制約としない）。
                        canvas.Scale(-1f, 1f);
                        break;
                }

                // 文字を中心に描画（タイプフェースはこのグリフの反復冒頭で解決済み）
                string charToDraw = GetCharString(text[srcIndex]);
                canvas.DrawText(charToDraw, -w / 2.0f, 0, paint);
                canvas.Restore();
            }
        }
        finally
        {
            paint.Typeface = originalTypeface;
            if (rentedWidths != null)
            {
                ArrayPool<float>.Shared.Return(rentedWidths);
            }
        }
    }
}
