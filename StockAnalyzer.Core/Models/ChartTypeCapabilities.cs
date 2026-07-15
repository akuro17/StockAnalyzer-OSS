namespace StockAnalyzer.Core.Models;

/// <summary>
/// 各チャートタイプの特性を宣言的に定義する不変オブジェクト。
/// </summary>
/// <remarks>
/// <para>
/// 従来は <see cref="ChartTypeExtensions"/> の各 Extension メソッドが
/// <c>chartType != ChartType.Renko &amp;&amp; chartType != ChartType.PointAndFigure &amp;&amp; ...</c>
/// のような否定形列挙で特性を判定していた。この方式では新チャートタイプを追加するたびに
/// 全メソッドを確認しなければならず、判断漏れがサイレントバグにつながるリスクがあった。
/// </para>
/// <para>
/// このレコードにより、各チャートタイプの特性を1箇所で宣言的に定義できるようになり、
/// 新タイプ追加時は <see cref="ChartTypeCapabilitiesRegistry"/> に1エントリ追加するだけで済む。
/// </para>
/// </remarks>
public sealed record ChartTypeCapabilities
{
    /// <summary>
    /// X軸が時系列であるかどうか。
    /// Candlestick, HeikinAshi, OHLCBar, Line, Area は true。
    /// Renko, P&amp;F, Kagi, ThreeLineBreak, ReverseWatch は false。
    /// </summary>
    public bool IsTimeBased { get; init; }

    /// <summary>
    /// Compact タイプ (マージンなし描画) であるかどうか。
    /// Renko, P&amp;F, Kagi, ReverseWatch は true。
    /// </summary>
    public bool IsCompactType { get; init; }

    /// <summary>
    /// インデックスベース (非時系列、列単位描画) であるかどうか。
    /// Renko, P&amp;F, Kagi, ThreeLineBreak は true。
    /// </summary>
    public bool IsIndexBased { get; init; }

    /// <summary>
    /// ボリュームバーの表示をサポートするかどうか。
    /// 現在は全タイプで false (Indicator 経由に移行済み)。
    /// </summary>
    public bool SupportsVolume { get; init; }

    /// <summary>
    /// 標準の OHLC ヘッダーオーバーレイを表示するかどうか。
    /// 時系列タイプ + Kagi が true。
    /// </summary>
    public bool HasStandardHeader { get; init; }

    /// <summary>
    /// 標準インジケータの描画をサポートするかどうか。
    /// 時系列タイプ (Candlestick, HeikinAshi, OHLCBar, Line, Area) は true。
    /// </summary>
    public bool SupportsIndicators { get; init; }

    /// <summary>
    /// インジケーターサブウィンドウの表示・非表示を切り替え可能（トトグル）にするかどうか。
    /// 非時系列チャート (Renko, Kagi, P&F等) ではスペース確保のために true、時系列チャートでは一貫性のために false とする。
    /// </summary>
    public bool CanToggleIndicators { get; init; }
}
