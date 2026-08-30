using Avalonia.Media;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Top/Bottom完全独立のパス追従テキスト注釈（<see cref="LineTextAnnotationSet"/>）を公開する
/// 描画オブジェクトの共通契約。<see cref="LineTextObject"/>（直線）と<see cref="CurveLineTextObject"/>
/// （曲線）の両方が実装し、<c>DrawingSettingsDialog</c> が同一の設定パネル・バインディングロジックを
/// 両者に対して共有できるようにする。
/// </summary>
public interface ILineTextAnnotatedObject
{
    string TopText { get; set; }
    double TopFontSize { get; set; }
    TextHorizontalAlignment TopAlignment { get; set; }
    float TopOffsetPx { get; set; }
    TextRotationMode TopRotationMode { get; set; }
    Color TopTextColor { get; set; }
    bool TopTextReverseOrder { get; set; }
    TextManualOrientation TopTextOrientationOverride { get; set; }

    /// <summary>trueの場合、チェックを入れた時点でTopTextが表示されていた物理的な側（画面上下）を
    /// 記憶し、以降の制御点編集でパスの進行方向が反転してもその側に固定し続ける。既定false（従来通り
    /// パスの法線方向に追従）。</summary>
    bool TopPositionFixed { get; set; }

    /// <summary>trueの場合、チェックを入れた時点の線（制御点）の形状をチャート座標（日時・価格）で
    /// スナップショットし、以降はTopTextをそのスナップショットに基づいて描画する。ライブの制御点が
    /// 編集されても一切追従しない（パン・ズームには通常のチャートオブジェクトと同様に追従する）。
    /// <see cref="TopPositionFixed"/>（上下反転防止）とは独立した設定。既定false。</summary>
    bool TopPositionLocked { get; set; }

    /// <summary>trueの場合、TopTextがパス（線）の長さより長く、範囲外にはみ出す文字があっても
    /// 非表示にせず、はみ出した側の端点における接線方向へ線形外挿した位置に描画する。既定false
    /// （従来通り、パスの範囲外の文字は非表示のまま）。</summary>
    bool TopExtendBeyondLine { get; set; }

    string BottomText { get; set; }
    double BottomFontSize { get; set; }
    TextHorizontalAlignment BottomAlignment { get; set; }
    float BottomOffsetPx { get; set; }
    TextRotationMode BottomRotationMode { get; set; }
    Color BottomTextColor { get; set; }
    bool BottomTextReverseOrder { get; set; }
    TextManualOrientation BottomTextOrientationOverride { get; set; }

    /// <summary>TopPositionFixedと同様の固定機能をBottomText側に独立して適用する。</summary>
    bool BottomPositionFixed { get; set; }

    /// <summary>TopPositionLockedと同様のチャート座標スナップショット固定をBottomText側に独立して適用する。</summary>
    bool BottomPositionLocked { get; set; }

    /// <summary>TopExtendBeyondLineと同様の設定をBottomText側に独立して適用する。</summary>
    bool BottomExtendBeyondLine { get; set; }

    /// <summary>trueの場合、ライン本体の描画色をチャート背景色に一致させ、視覚的に非表示にする。</summary>
    bool MatchBackgroundColor { get; set; }
}
