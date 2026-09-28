using Avalonia;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// チャートのレイアウト（マージン、パネル分割など）を計算するプロバイダーのインターフェース。
/// </summary>
public interface IChartLayoutProvider
{
    /// <summary>
    /// 指定された描画領域とインジケータ設定に基づいて、各領域のレイアウトコンテキストを計算します。
    /// </summary>
    ChartLayoutContext CreateLayout(
        Rect bounds, 
        IEnumerable<CoreIndicatorSettings>? indicators = null,
        bool showIndicators = true,
        bool isMainWindowVisible = true,
        float? customMarginTop = null, 
        float? customMarginBottom = null, 
        float? customMarginRight = null);
}
