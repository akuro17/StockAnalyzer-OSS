namespace StockAnalyzer.Avalonia.Services;

using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using System.Collections.Generic;

/// <summary>
/// Provides the canonical 12-category definition of all drawing tools.
/// Categories are organized by user purpose, not visual shape.
/// Tools within each category are sorted by Japanese display name (fifty-sounds order).
/// Icons follow the specification: geometric/mathematical symbols for tool essence.
/// </summary>
public static class DrawingToolCategoryService
{
    public static IReadOnlyList<DrawingToolCategoryDefinition> GetCategories()
    {
        return new[]
        {
            // 1. Cursors — sorted: インフォメーション, 消しゴム, ポインター, ルーラー
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Cursors",
                Icon: "\U0001F446", // 👆
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.Information, "\u2139\uFE0F", "DrawTool_Information"),
                    new DrawingToolInfo(DrawingTool.Eraser, "\U0001F9F9", "DrawTool_Eraser"),
                    new DrawingToolInfo(DrawingTool.Pointer, "\U0001F446", "DrawTool_Pointer"),
                    new DrawingToolInfo(DrawingTool.Ruler, "\U0001F4CF", "DrawTool_Ruler"),
                }),

            // 2. Lines & Trend — sorted: 角度, 矢印, カーブトレンド, カテナリー曲線, 水平線, トレンドライン,
            //   NURBS円錐曲線(弧), NURBS双曲線, NURBS放物線, NURBS曲線, ポリライン, レイ, 垂直線
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Lines",
                Icon: "\uFF0F", // ／
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.AngleTool, "\u2220", "DrawTool_AngleTool"),
                    new DrawingToolInfo(DrawingTool.Arrow, "\u2197", "DrawTool_Arrow"),
                    new DrawingToolInfo(DrawingTool.CurveTrend, "\u2312", "DrawTool_CurveTrend"),
                    new DrawingToolInfo(DrawingTool.CatenaryCurve, "\u2312", "DrawTool_CatenaryCurve"),
                    new DrawingToolInfo(DrawingTool.HorizontalLine, "\u2500", "DrawTool_HorizontalLine"),
                    new DrawingToolInfo(DrawingTool.TrendLine, "\uFF0F", "DrawTool_TrendLine"),
                    new DrawingToolInfo(DrawingTool.NurbsConicArc, "\u2312", "DrawTool_NurbsConic"),
                    new DrawingToolInfo(DrawingTool.NurbsHyperbola, "\u22D9", "DrawTool_NurbsHyperbola"),
                    new DrawingToolInfo(DrawingTool.NurbsParabola, "\u222A", "DrawTool_NurbsParabola"),
                    new DrawingToolInfo(DrawingTool.NurbsTrendCurve, "\u223F", "DrawTool_NurbsTrendCurve"),
                    new DrawingToolInfo(DrawingTool.Polyline, "\u29E2", "DrawTool_Polyline"),
                    new DrawingToolInfo(DrawingTool.Ray, "\u2198", "DrawTool_Ray"),
                    new DrawingToolInfo(DrawingTool.VerticalLine, "\u2502", "DrawTool_VerticalLine"),
                }),

            // 3. Shapes — sorted: アルキメデスの螺旋, 円・楕円, 黄金螺旋, 三角形, 対数螺旋, 長方形, NURBS円, NURBS楕円
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Shapes",
                Icon: "\u2B1C", // ⬜
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.ArchimedeanSpiral, "\U0001F300", "DrawTool_ArchimedeanSpiral"),
                    new DrawingToolInfo(DrawingTool.Ellipse, "\u2B55", "DrawTool_Ellipse"),
                    new DrawingToolInfo(DrawingTool.GoldenSpiral, "\U0001F300", "DrawTool_GoldenSpiral"),
                    new DrawingToolInfo(DrawingTool.Triangle, "\U0001F53A", "DrawTool_Triangle"),
                    new DrawingToolInfo(DrawingTool.LogarithmicSpiral, "\U0001F300", "DrawTool_LogarithmicSpiral"),
                    new DrawingToolInfo(DrawingTool.Rectangle, "\u2B1C", "DrawTool_Rectangle"),
                    new DrawingToolInfo(DrawingTool.NurbsConic, "\u25CE", "DrawTool_NurbsCircle"),
                    new DrawingToolInfo(DrawingTool.NurbsEllipse, "\u2B2D", "DrawTool_NurbsEllipse"),
                }),

            // 4. Text — sorted: 吹き出し, 価格ラベル, 曲線ラインテキスト, ラインテキスト, テキスト
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Text",
                Icon: "\U0001F4DD", // 📝
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.Callout, "\U0001F4AC", "DrawTool_Callout"),
                    new DrawingToolInfo(DrawingTool.PriceLabel, "\U0001F3F7\uFE0F", "DrawTool_PriceLabel"),
                    new DrawingToolInfo(DrawingTool.CurveLineText, "\U0001F30A", "DrawTool_CurveLineText"),
                    new DrawingToolInfo(DrawingTool.LineText, "\U0001F524", "DrawTool_LineText"),
                    new DrawingToolInfo(DrawingTool.Text, "\U0001F4DD", "DrawTool_Text"),
                }),

            // 5. Channels & Regression — sorted: カーブチャネル, 回帰トレンド, 範囲スプライン, ピッチフォーク, 平行チャネル
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Channels",
                Icon: "\u2AFD", // ⫽
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.CurveChannel, "\u224B", "DrawTool_CurveChannel"),
                    new DrawingToolInfo(DrawingTool.RegressionTrend, "\u223F", "DrawTool_RegressionTrend"),
                    new DrawingToolInfo(DrawingTool.RangeSpline, "\u223D", "DrawTool_RangeSpline"),
                    new DrawingToolInfo(DrawingTool.Pitchfork, "\u22D4", "DrawTool_Pitchfork"),
                    new DrawingToolInfo(DrawingTool.ParallelChannel, "\u2AFD", "DrawTool_ParallelChannel"),
                }),

            // 6. Fibonacci Tools — sorted: アーク, エクスパンション, エリプス, サークル, スパイラル, タイムゾーン, チャネル, ファン, リトレースメント
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Fibonacci",
                Icon: "\u2262", // ≢
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.FibonacciArc, "\u25E0", "DrawTool_FibArc"),
                    new DrawingToolInfo(DrawingTool.FibonacciExpansion, "\u2913", "DrawTool_FibExpansion"),
                    new DrawingToolInfo(DrawingTool.FibonacciEllipse, "\u2B2D", "DrawTool_FibEllipse"),
                    new DrawingToolInfo(DrawingTool.FibonacciCircle, "\u25D6", "DrawTool_FibCircle"),
                    new DrawingToolInfo(DrawingTool.FibonacciSpiral, "\U0001F300", "DrawTool_FibSpiral"),
                    new DrawingToolInfo(DrawingTool.FibonacciTimeZone, "\U0001F552", "DrawTool_FibTimeZone"),
                    new DrawingToolInfo(DrawingTool.FibonacciChannel, "\u2226", "DrawTool_FibChannel"),
                    new DrawingToolInfo(DrawingTool.FibonacciFan, "\u2A5A", "DrawTool_FibFan"),
                    new DrawingToolInfo(DrawingTool.FibonacciRetracement, "\u2262", "DrawTool_FibRetracement"),
                }),

            // 7. Gann Tools — sorted: グリッド, スクエア, スクエア144, スクエア9, ファン, ボックス, ホイール
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Gann",
                Icon: "\u29A2", // ⧢
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.GannGrid, "\u25A6", "DrawTool_GannGrid"),
                    new DrawingToolInfo(DrawingTool.GannSquare, "\U0001F4A0", "DrawTool_GannSquare"),
                    new DrawingToolInfo(DrawingTool.GannSquare144, "\U0001F522", "DrawTool_GannSquare144"),
                    new DrawingToolInfo(DrawingTool.GannSquareOfNine, "\u2684", "DrawTool_GannSquare9"),
                    new DrawingToolInfo(DrawingTool.GannFan, "\u29A2", "DrawTool_GannFan"),
                    new DrawingToolInfo(DrawingTool.GannBox, "\U0001F533", "DrawTool_GannBox"),
                    new DrawingToolInfo(DrawingTool.GannWheel, "\u2638", "DrawTool_GannWheel"),
                }),

            // 8. Patterns — sorted: Auto Elliott Wave, Geometric Pattern, バーパターン, ハーモニックパターン
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Patterns",
                Icon: "\u29D6", // ⧖
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.AutoElliottWave, "\u223E", "DrawTool_AutoElliottWave"),
                    new DrawingToolInfo(DrawingTool.GeometricPattern, "\U0001F4D0", "DrawTool_GeometricPattern"),
                    new DrawingToolInfo(DrawingTool.BarPattern, "\u2016", "DrawTool_BarPattern"),
                    new DrawingToolInfo(DrawingTool.HarmonicPattern, "\u29D6", "DrawTool_HarmonicPattern"),
                }),

            // 9. Algorithms & Forecast — grouped by family, then sorted within each group
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Algorithms",
                Icon: "\U0001F916", // 🤖
                Tools: new[]
                {
                    // 汎用予測モデル — sorted: ARIMA, DTW, FFT(高速~), HMM, カルマン, ピアソン, フレシェ
                    new DrawingToolInfo(DrawingTool.ArimaProjection, "\U0001F4C9", "DrawTool_ArimaProjection"),
                    new DrawingToolInfo(DrawingTool.DtwProjection, "\U0001F52E", "DrawTool_DtwProjection"),
                    new DrawingToolInfo(DrawingTool.FftProjection, "\u2248", "DrawTool_FftProjection"),
                    new DrawingToolInfo(DrawingTool.HmmProjection, "\U0001F500", "DrawTool_HmmProjection"),
                    new DrawingToolInfo(DrawingTool.KalmanFilterProjection, "\U0001F4C8", "DrawTool_KalmanFilterProjection"),
                    new DrawingToolInfo(DrawingTool.PearsonProjection, "\u223F", "DrawTool_PearsonProjection"),
                    new DrawingToolInfo(DrawingTool.FrechetProjection, "\u223E", "DrawTool_FrechetProjection"),
                    // SSA系 — sorted: 将来予測線, 多重成分, 構造的ピボット, 動的~, 未来予測~, 構造的異常~
                    new DrawingToolInfo(DrawingTool.SsaProjection, "\u223F", "DrawTool_SsaProjection"),
                    new DrawingToolInfo(DrawingTool.SsaMultiComponent, "\u224B", "DrawTool_SsaMultiComponent"),
                    new DrawingToolInfo(DrawingTool.SsaStructuralPivots, "\u224C", "DrawTool_SsaStructuralPivots"),
                    new DrawingToolInfo(DrawingTool.SsaDynamicEnvelopes, "\u224D", "DrawTool_SsaDynamicEnvelopes"),
                    new DrawingToolInfo(DrawingTool.SsaProjectedTargets, "\u22B9", "DrawTool_SsaProjectedTargets"),
                    new DrawingToolInfo(DrawingTool.SsaAnomalyHighlight, "\u26A0", "DrawTool_SsaAnomalyHighlight"),
                    // ハフ変換系 — sorted: 自動~, 重要~, 市場~, マグネット, 放物線~
                    new DrawingToolInfo(DrawingTool.HoughAutoLines, "\u2296", "DrawTool_HoughAutoLines"),
                    new DrawingToolInfo(DrawingTool.HoughKeyLevels, "\u2263", "DrawTool_HoughKeyLevels"),
                    new DrawingToolInfo(DrawingTool.HoughResonantFan, "\u22BE", "DrawTool_HoughResonantFan"),
                    new DrawingToolInfo(DrawingTool.HoughMagneticLine, "\U0001F9F2", "DrawTool_HoughMagneticLine"),
                    new DrawingToolInfo(DrawingTool.HoughParabolicCurve, "\u2312", "DrawTool_HoughParabolicCurve"),
                }),

            // 10. Position & Risk Management — sorted: ショートポジション, ロングポジション, 目標価格の投影
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Position",
                Icon: "\U0001F4B0", // 💰
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.ShortPosition, "\U0001F7E5", "DrawTool_ShortPosition"),
                    new DrawingToolInfo(DrawingTool.LongPosition, "\U0001F7E9", "DrawTool_LongPosition"),
                    new DrawingToolInfo(DrawingTool.TargetPriceProjection, "\u21F2", "DrawTool_TargetPriceProjection"),
                }),

            // 11. Cycles & Volume — sorted: アンカードVWAP, FFTスペクトラム, FFT Cycle Detector, サイクリックライン, サイン波, タイムサイクル, 固定期間出来高プロファイル
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Cycles",
                Icon: "\u21B9", // ↹
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.AnchoredVWAP, "\u2693", "DrawTool_AnchoredVWAP"),
                    new DrawingToolInfo(DrawingTool.FftSpectrum, "\u2248", "DrawTool_FftSpectrum"),
                    new DrawingToolInfo(DrawingTool.AutoTimeCycle, "\u2248", "DrawTool_AutoTimeCycle"),
                    new DrawingToolInfo(DrawingTool.CyclicLines, "\u21B9", "DrawTool_CyclicLines"),
                    new DrawingToolInfo(DrawingTool.SineLine, "\u223D", "DrawTool_SineLine"),
                    new DrawingToolInfo(DrawingTool.TimeCycles, "\u23F1\uFE0F", "DrawTool_TimeCycles"),
                    new DrawingToolInfo(DrawingTool.FixedRangeVolumeProfile, "\U0001F4CA", "DrawTool_FRVP"),
                }),

            // 12. Icon
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Icon",
                Icon: "\U0001F5BC\uFE0F", // 🖼️
                Tools: System.Array.Empty<DrawingToolInfo>()),
        };
    }
}
