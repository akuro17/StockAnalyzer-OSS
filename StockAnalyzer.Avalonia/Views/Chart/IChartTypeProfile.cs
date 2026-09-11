using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// 個別のチャートタイプ (Candlestick, Renko, Kagi 等) の設定と振る舞いを一元管理するプロファイル。
/// このインターフェースを通じて、レンダラー、設定値、レイアウト計算などの責務を分離しつつ統合的に提供する。
/// オープン・クローズド原則 (OCP) を実現するための中核となる。
/// </summary>
public interface IChartTypeProfile
{
    /// <summary>
    /// このプロファイルが担当するチャートタイプ。
    /// </summary>
    ChartType Type { get; }

    /// <summary>
    /// このチャートタイプの宣言的な特性 (時系列か、マージンを持つか等)。
    /// </summary>
    ChartTypeCapabilities Capabilities { get; }

    /// <summary>
    /// このチャートタイプの描画を担当するレンダラーのインスタンスを生成・または取得する。
    /// </summary>
    IChartRenderer CreateRenderer();

    /// <summary>
    /// このチャートタイプ専用の描画設定オブジェクト (IChartRenderConfig) を生成する。
    /// </summary>
    /// <param name="settings">汎用的なレンダリング設定</param>
    /// <param name="currentPrice">現在価格 (ラベル等で使用)</param>
    /// <param name="visibleStartIndex">表示領域の開始インデックス</param>
    /// <param name="visibleCandleCount">表示領域のデータ数</param>
    /// <param name="mousePosition">インタラクション用のマウス座標</param>
    /// <param name="transform">座標変換オブジェクト</param>
    /// <param name="reverseWatchData">ReverseWatch用の特殊データ</param>
    IChartRenderConfig CreateRenderConfig(
        IChartRenderSettings settings,
        decimal currentPrice,
        int visibleStartIndex,
        int visibleCandleCount,
        StockAnalyzer.Core.Models.Point mousePosition,
        StockAnalyzer.Avalonia.Drawing.ICoordinateTransform? transform,
        double renderScaling,
        StockAnalyzer.Core.Models.Analysis.ReverseWatchCurveData? reverseWatchData = null,
        StockAnalyzer.Core.Models.ComparisonAlignedData? comparisonData = null);

    /// <summary>
    /// このチャートタイプのレイアウト計算を担当するプロバイダー。
    /// </summary>
    IChartLayoutProvider LayoutProvider { get; }

    /// <summary>
    /// このチャートタイプ固有の表示価格帯 (Y軸スケール) を計算するプロバイダー。
    /// </summary>
    IPriceRangeCalculator PriceRangeCalculator { get; }
}
