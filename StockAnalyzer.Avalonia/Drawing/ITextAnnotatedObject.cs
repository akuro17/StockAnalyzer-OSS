using Avalonia.Media;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// テキストボックス表示（本文・フォントサイズ・アライメント・背景ボックス）を公開する描画オブジェクトの
/// 共通契約。<see cref="TextObject"/>と<see cref="CalloutObject"/>の両方が実装し、
/// <c>DrawingSettingsDialog</c>が同一の設定パネル・バインディングロジックを両者に対して共有できるように
/// する（<see cref="ILineTextAnnotatedObject"/>と同様の意図）。
/// </summary>
public interface ITextAnnotatedObject
{
    string Text { get; set; }
    double FontSize { get; set; }
    TextHorizontalAlignment Alignment { get; set; }
    bool ShowBackgroundBox { get; set; }
    Color BackgroundColor { get; set; }
    float BackgroundPadding { get; set; }
    float CornerRadius { get; set; }
}
