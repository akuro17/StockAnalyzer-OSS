namespace StockAnalyzer.Core.Models;

/// <summary>
/// チャート表示タイプを定義する列挙型
/// </summary>
public enum ChartType
{
    /// <summary>
    /// 通常のローソク足チャート
    /// </summary>
    Candlestick,

    /// <summary>
    /// 平均足（Heikin-Ashi）チャート
    /// </summary>
    HeikinAshi,

    /// <summary>
    /// ラインチャート（終値のみ表示）
    /// </summary>
    Line,

    /// <summary>
    /// エリアチャート（ライン下部を塗りつぶし）
    /// </summary>
    Area,

    /// <summary>
    /// 練行足（Renko）チャート
    /// </summary>
    Renko,

    /// <summary>
    /// カギ足 (Kagi) チャート
    /// </summary>
    Kagi,

    /// <summary>
    /// ポイント＆フィギュア (Point & Figure) チャート
    /// </summary>
    PointAndFigure,

    /// <summary>
    /// 新値足 (Three Line Break) チャート
    /// </summary>
    ThreeLineBreak,

    /// <summary>
    /// OHLCバーチャート（欧米式バー）
    /// </summary>
    OHLCBar,

    /// <summary>
    /// Reverse Watch Curve (Volume vs Price XY plot)
    /// </summary>
    ReverseWatch,

    /// <summary>
    /// Relative Performance Chart (multi-symbol percentage-change overlay)
    /// </summary>
    RelativePerformance
}
