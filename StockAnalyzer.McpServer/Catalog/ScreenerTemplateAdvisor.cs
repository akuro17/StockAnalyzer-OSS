using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Says which enabled entries of a saved Screener template can never match here. The Screener tab accepts entries this server's
/// screener evaluates to "no value" (a look-ahead indicator, a two-symbol indicator without a comparison symbol or beside weekly or monthly bars, AnchoredVWAP on another time frame); a template is the user's own, so it is
/// run as saved and the entries that cannot match are reported instead of being silently ignored.
/// </summary>
internal sealed class ScreenerTemplateAdvisor
{
    private readonly IndicatorCatalog _indicators;

    public ScreenerTemplateAdvisor(IndicatorCatalog indicators)
    {
        _indicators = indicators;
    }

    public IReadOnlyList<string> Warnings(ScreenerIndicatorTemplate template)
    {
        var warnings = new List<string>();
        if (!template.Entries.Any(entry => entry.IsEnabled))
        {
            warnings.Add("The template has no enabled entry, so nothing can match.");
        }

        // A template that reads a weekly or monthly bar on any side is run with every side on its own stored frame (no comparison symbols are read then).
        bool sideFrameScan = ScreenerSideFrames.UsesNonDailyBars(template.Entries);
        for (int index = 0; index < template.Entries.Count; index++)
        {
            ScreenerIndicatorEntry entry = template.Entries[index];
            if (!entry.IsEnabled)
            {
                continue;
            }

            foreach (string problem in Problems(entry.LeftHand, sideFrameScan).Concat(entry.TargetMode == RightHandTargetMode.Indicator ? Problems(entry.RightHand, sideFrameScan) : Array.Empty<string>()))
            {
                warnings.Add($"Entry {index} ({ScreenerEntryText.Describe(entry)}): {problem}, so it never matches.");
            }
        }

        return warnings;
    }

    private IEnumerable<string> Problems(ScreenerIndicatorSideConfig side, bool sideFrameScan)
    {
        // Only an indicator side is evaluated through the indicator calculation; prices, columns and patterns are read directly.
        if (side.CategoryType != ScreenerItemCategoryType.Indicator || side.IndicatorType == IndicatorType.Price)
        {
            yield break;
        }

        if (side.TimeFrame != TimeFrame.D1 && side.IndicatorType == IndicatorType.AnchoredVWAP)
        {
            yield return $"AnchoredVWAP has no definition on the {side.TimeFrame} time frame of its side";
        }

        IndicatorCatalogEntry? entry = _indicators.Find(side.IndicatorType.ToString());
        if (entry is null || !entry.IsRegistered)
        {
            yield return $"{side.IndicatorType} cannot be calculated";
        }
        else if (entry.IsCrossTicker && ScreenerComparisonSymbols.Of(side) is null)
        {
            yield return $"{side.IndicatorType} compares two symbols and the side names no ComparisonSymbol";
        }
        else if (entry.IsCrossTicker && (sideFrameScan || side.TimeFrame != TimeFrame.D1))
        {
            yield return $"{side.IndicatorType} compares two symbols, which is not evaluated in a template that reads weekly or monthly bars (the comparison symbol's bars are daily)";
        }
        else if (!BacktestIndicatorEligibility.IsAllowed(side.IndicatorType, side.OutputName))
        {
            yield return $"{side.IndicatorType} ({side.OutputName}) looks ahead or cannot be calculated bar by bar";
        }
    }
}
