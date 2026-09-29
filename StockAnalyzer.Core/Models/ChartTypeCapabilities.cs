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
    /// タイプ別の値は <see cref="ChartTypeCapabilitiesRegistry"/> が唯一の宣言元。
    /// </summary>
    public bool IsTimeBased { get; init; }

    /// <summary>
    /// Compact タイプ (マージンなし描画) であるかどうか。
    /// タイプ別の値は <see cref="ChartTypeCapabilitiesRegistry"/> が唯一の宣言元。
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
    /// タイプ別の値は <see cref="ChartTypeCapabilitiesRegistry"/> が唯一の宣言元。
    /// </summary>
    public bool HasStandardHeader { get; init; }

    /// <summary>
    /// テクニカル指標の描画をサポートするかどうか。
    /// false の場合、指標の描画パス全体がスキップされ、メインチャートのオーバーレイ指標も
    /// サブウィンドウのパネル指標もいっさい描画されない。
    /// タイプ別の true/false 一覧と根拠は docs/SA_UI_INTERACTION.md セクション 27 / 27.1 を参照。
    /// </summary>
    public bool SupportsIndicators { get; init; }

    /// <summary>
    /// インジケーターサブウィンドウ（サブパネル）を表示し得るかどうか。
    /// false の場合、ユーザーのサブウィンドウトグル状態に関わらず
    /// <c>EffectiveIsSubWindowVisible</c> は常に false となる。
    /// <see cref="SupportsIndicators"/> とは独立したフラグ。両者の組み合わせと
    /// タイプ別の値は docs/SA_UI_INTERACTION.md セクション 27 / 27.1 を参照。
    /// </summary>
    public bool CanToggleIndicators { get; init; }
}
