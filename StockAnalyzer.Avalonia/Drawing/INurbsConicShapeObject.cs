using Avalonia.Media;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 塗りつぶし表示（IsFilled/FillColor）を公開する円錐曲線系描画オブジェクトの共通契約。
/// <see cref="NurbsConicObject"/>と<see cref="NurbsEllipseObject"/>の両方が実装し、
/// <c>DrawingSettingsDialog</c>が同一の設定パネル・バインディングロジックを両者に対して共有できる
/// ようにする（<see cref="ITextAnnotatedObject"/>と同様の意図）。
/// </summary>
public interface INurbsConicShapeObject
{
    bool IsFilled { get; set; }
    Color FillColor { get; set; }
}
