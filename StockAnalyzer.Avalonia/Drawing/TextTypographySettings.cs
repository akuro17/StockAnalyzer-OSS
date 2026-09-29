using System;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 水平テキストアライメント
/// </summary>
public enum TextHorizontalAlignment
{
    Left = 0,
    Center = 1,
    Right = 2
}

/// <summary>
/// パス追従テキストの回転挙動。
/// </summary>
public enum TextRotationMode
{
    /// <summary>常にパスの接線角度に厳密に追従して回転する（従来挙動）。</summary>
    FollowLine = 0,

    /// <summary>接線角度が±90°を超える場合は180°反転し、常に左から右へ読める向きを維持する。</summary>
    AlwaysUpright = 1,

    /// <summary>各文字はパスの局所的な接線角度に追従して傾くが、パスの区間の向き（隣接する
    /// 制御点ペアのベクトル）が90°〜270°の範囲（左向き成分を持つ）の場合のみ、文字列全体を
    /// 180°補正し文字の並び順を反転させて、線への追従性を保ったまま読める向きを維持する。</summary>
    FollowLineAutoFlip = 2
}

/// <summary>
/// RotationMode/ManualReverseOrderとは独立に、文字の描画結果に追加で適用する向き変換。
/// 表現目的の機能であり、判読可能性（鏡文字になるか等）は設計上の制約としない。
/// </summary>
public enum TextManualOrientation
{
    /// <summary>追加の変換なし（既定・従来互換）。</summary>
    Default = 0,

    /// <summary>ローカルX軸（ラインと平行な、文字送り方向の軸）で反射する。個々の文字自体が
    /// 左右反転した鏡文字になる。文字の並び順・位置には影響しない。</summary>
    Mirror = 1,

    /// <summary>表示直前にさらに180°回転する（点対称）。文字の並び順・位置には影響しない。</summary>
    Rotate180 = 2
}

/// <summary>
/// 不変なテキスト設定構造体（0 bytes managed allocation）
/// </summary>
public readonly record struct TextTypographySettings(
    float FontSizePx = TextTypographySettings.DefaultFontSizePx,
    uint TextColorArgb = TextTypographySettings.DefaultTextColorArgb,
    uint BackgroundColorArgb = TextTypographySettings.DefaultBackgroundColorArgb, // White with 220 alpha
    TextHorizontalAlignment Alignment = TextHorizontalAlignment.Left,
    float LetterSpacingPx = 0f,
    float LineSpacingFactor = TextTypographySettings.DefaultLineSpacingFactor,
    bool ShowBackgroundBox = true,
    float BackgroundPaddingPx = TextTypographySettings.DefaultBackgroundPaddingPx,
    float CornerRadiusPx = TextTypographySettings.DefaultCornerRadiusPx
)
{
    // 既定値のSSoT。プライマリコンストラクタの既定引数・Default静的フィールド・
    // パラメータなしコンストラクタの3箇所すべてがこの定数群を参照する（値の二重管理を防止）。
    private const float DefaultFontSizePx = 12f;
    private const uint DefaultTextColorArgb = 0xFF000000;
    private const uint DefaultBackgroundColorArgb = 0xDCFFFFFF;
    private const float DefaultLineSpacingFactor = 1.2f;
    private const float DefaultBackgroundPaddingPx = 8f;
    private const float DefaultCornerRadiusPx = 4f;

    public static readonly TextTypographySettings Default = new();

    // record struct の暗黙のパラメータなしコンストラクタは既定引数を適用せず全フィールドを
    // ゼロ初期化するため、new TextTypographySettings() / default(TextTypographySettings) でも
    // 有効な既定値が使われるよう明示的に上書きする。
    public TextTypographySettings() : this(
        DefaultFontSizePx,
        DefaultTextColorArgb,
        DefaultBackgroundColorArgb,
        TextHorizontalAlignment.Left,
        0f,
        DefaultLineSpacingFactor,
        true,
        DefaultBackgroundPaddingPx,
        DefaultCornerRadiusPx)
    {
    }

    // 不変条件バリデーション
    public void Validate()
    {
        if (FontSizePx < 1.0f || FontSizePx > 120.0f)
            throw new ArgumentOutOfRangeException(nameof(FontSizePx), "FontSize must be in [1.0, 120.0] px.");
        if (LineSpacingFactor < 0.5f || LineSpacingFactor > 3.0f)
            throw new ArgumentOutOfRangeException(nameof(LineSpacingFactor), "LineSpacingFactor must be in [0.5, 3.0].");
    }
}

/// <summary>
/// パス追従テキスト描画オプション
/// </summary>
public readonly record struct PathTextOptions(
    TextHorizontalAlignment Alignment = TextHorizontalAlignment.Center,
    float NormalOffset = -4f,         // パス法線方向オフセット（負値＝パスの上側）
    float StartMarginPx = 0f,         // パス始端からの余白
    float EndMarginPx = 0f,           // パス終端からの余白
    bool ClipOverflow = true,         // パス長を超える文字を非描画とするか
    TextRotationMode RotationMode = TextRotationMode.FollowLine, // 文字の回転挙動（既定は従来互換）
    bool ManualReverseOrder = false,  // RotationModeとは独立に、文字の並び順だけを手動で反転する
    TextManualOrientation OrientationOverride = TextManualOrientation.Default, // 表示直前に追加で適用する反射/回転
    bool ExtendBeyondPath = false      // trueの場合、パス範囲外の文字を非表示にせず、端点の接線方向へ線形外挿した位置に描画する
);
