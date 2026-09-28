namespace StockAnalyzer.Core.Models;

/// <summary>
/// <see cref="ChartType"/> の特性判定 Extension メソッド。
/// 全メソッドは <see cref="ChartTypeCapabilitiesRegistry"/> に委譲する。
/// </summary>
/// <remarks>
/// シグネチャは変更せず後方互換を維持。
/// 内部実装を Registry への委譲に置き換えることで、
/// 新チャートタイプ追加時にこのファイルの修正が不要になった。
/// </remarks>
public static class ChartTypeExtensions
{
    /// <summary>
    /// Determines if the chart type is time-based (standard X-axis is time).
    /// Returns false for non-time-based charts like Renko, Point &amp; Figure, Kagi, Reverse Watch.
    /// </summary>
    public static bool IsTimeBased(this ChartType chartType)
        => ChartTypeCapabilitiesRegistry.Get(chartType).IsTimeBased;

    /// <summary>
    /// Determines if the chart type supports volume display.
    /// Currently returns false for all types (migrated to unified Indicator volume).
    /// </summary>
    public static bool SupportsVolume(this ChartType chartType)
        => ChartTypeCapabilitiesRegistry.Get(chartType).SupportsVolume;

    /// <summary>
    /// Determines if the chart type uses the standard OHLC header overlay.
    /// </summary>
    public static bool HasStandardHeader(this ChartType chartType)
        => ChartTypeCapabilitiesRegistry.Get(chartType).HasStandardHeader;

    /// <summary>
    /// Determines if the chart type is a compact type (Renko, PnF, Kagi, ReverseWatch).
    /// Used for layout decisions.
    /// </summary>
    public static bool IsCompactType(this ChartType chartType)
        => ChartTypeCapabilitiesRegistry.Get(chartType).IsCompactType;

    /// <summary>
    /// Determines if the chart type is index-based (Renko, P&amp;F, Kagi, TLB).
    /// </summary>
    public static bool IsIndexBased(this ChartType chartType)
        => ChartTypeCapabilitiesRegistry.Get(chartType).IsIndexBased;

    /// <summary>
    /// Determines if the chart type supports rendering standard indicators.
    /// </summary>
    /// <remarks>
    /// Migrated from <c>ChartHelpers.SupportsIndicators()</c> to centralize
    /// all chart type capability queries in one place.
    /// </remarks>
    public static bool SupportsIndicators(this ChartType chartType)
        => ChartTypeCapabilitiesRegistry.Get(chartType).SupportsIndicators;
}
