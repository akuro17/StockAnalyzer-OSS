using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// 全 <see cref="ChartType"/> の <see cref="ChartTypeCapabilities"/> を一元管理する静的レジストリ。
/// </summary>
/// <remarks>
/// <para>
/// 各チャートタイプの特性を Dictionary で宣言的に管理する。
/// <see cref="ChartTypeExtensions"/> の Extension メソッドはこのレジストリに委譲することで、
/// 否定形列挙 (<c>!= Renko &amp;&amp; != PnF &amp;&amp; ...</c>) を廃止する。
/// </para>
/// <para>
/// 新チャートタイプの追加手順:
/// 1. <see cref="ChartType"/> enum に値を追加
/// 2. このレジストリの _capabilities に対応するエントリを追加
/// 3. 完了 — Extension メソッドの修正は不要
/// </para>
/// </remarks>
public static class ChartTypeCapabilitiesRegistry
{
    private static readonly Dictionary<ChartType, ChartTypeCapabilities> _capabilities = new()
    {
        [ChartType.Candlestick] = new ChartTypeCapabilities
        {
            IsTimeBased = true,
            IsIndexBased = false,
            HasStandardHeader = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.HeikinAshi] = new ChartTypeCapabilities
        {
            IsTimeBased = true,
            IsIndexBased = false,
            HasStandardHeader = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.OHLCBar] = new ChartTypeCapabilities
        {
            IsTimeBased = true,
            IsIndexBased = false,
            HasStandardHeader = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.Line] = new ChartTypeCapabilities
        {
            IsTimeBased = true,
            IsIndexBased = false,
            HasStandardHeader = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.Area] = new ChartTypeCapabilities
        {
            IsTimeBased = true,
            IsIndexBased = false,
            HasStandardHeader = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.Renko] = new ChartTypeCapabilities
        {
            IsCompactType = true,
            IsIndexBased = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.PointAndFigure] = new ChartTypeCapabilities
        {
            IsCompactType = true,
            IsIndexBased = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.Kagi] = new ChartTypeCapabilities
        {
            IsCompactType = true,
            IsIndexBased = true,
            HasStandardHeader = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.ThreeLineBreak] = new ChartTypeCapabilities
        {
            IsIndexBased = true,
            SupportsIndicators = true,
            CanToggleIndicators = true,
        },
        [ChartType.ReverseWatch] = new ChartTypeCapabilities
        {
            IsCompactType = true,
            SupportsIndicators = false, 
            CanToggleIndicators = true,
        },
        [ChartType.RelativePerformance] = new ChartTypeCapabilities
        {
            IsTimeBased = true,
            IsCompactType = true,
            HasStandardHeader = true,
            SupportsIndicators = false,
        },
    };

    /// <summary>
    /// 指定されたチャートタイプの Capabilities を取得する。
    /// 未登録のタイプの場合は全プロパティが false のデフォルト値を返す。
    /// </summary>
    public static ChartTypeCapabilities Get(ChartType type)
        => _capabilities.TryGetValue(type, out var cap) ? cap : new ChartTypeCapabilities();
}
