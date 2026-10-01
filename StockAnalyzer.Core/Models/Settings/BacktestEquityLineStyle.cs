using System;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The resolved equity-curve color style: the mode plus the three colors it can use. Pure value type so the
/// rendering control can compare two styles for equality before rebuilding anything.
/// <see cref="UpColor"/>/<see cref="DownColor"/> mean "up segment"/"down segment" in
/// <see cref="BacktestEquityColorMode.PreviousBar"/> and "at a high"/"in drawdown" in <see cref="BacktestEquityColorMode.Drawdown"/>.
/// </summary>
public sealed record BacktestEquityLineStyle(
    IndicatorColor LineColor,
    BacktestEquityColorMode Mode,
    IndicatorColor UpColor,
    IndicatorColor DownColor)
{
    /// <summary>Builds the style from the persisted settings; an unparsable color falls back to its default constant.</summary>
    public static BacktestEquityLineStyle FromSettings(GlobalChartSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new BacktestEquityLineStyle(
            ResolveColor(settings.BacktestEquityLineColor, ChartSettingsConstants.DefaultBacktestEquityLineColor),
            settings.BacktestEquityColorMode,
            ResolveColor(settings.BacktestEquityUpColor, ChartSettingsConstants.DefaultBacktestEquityUpColor),
            ResolveColor(settings.BacktestEquityDownColor, ChartSettingsConstants.DefaultBacktestEquityDownColor));
    }

    /// <summary>
    /// The single definition of "persisted hex -> displayed color": an unparsable value resolves to the (parsed) default.
    /// Shared by the rendering style and the settings page so both read a stored color identically.
    /// </summary>
    public static HsvData ResolveHsv(string html, string defaultHtml) =>
        HsvData.FromHtmlSafe(html, HsvData.FromHtmlSafe(defaultHtml));

    private static IndicatorColor ResolveColor(string html, string defaultHtml) =>
        ResolveHsv(html, defaultHtml).ToIndicatorColor();
}
