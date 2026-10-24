using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Says which enabled entries of a saved Screener template can never match here. The Screener tab accepts entries this server's
/// screener evaluates to "no value" (a side on another time frame, a look-ahead indicator, a two-symbol indicator without a comparison symbol); a template is the user's own, so it is
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

        for (int index = 0; index < template.Entries.Count; index++)
        {
            ScreenerIndicatorEntry entry = template.Entries[index];
            if (!entry.IsEnabled)
            {
                continue;
            }

            foreach (string problem in Problems(entry.LeftHand).Concat(entry.TargetMode == RightHandTargetMode.Indicator ? Problems(entry.RightHand) : Array.Empty<string>()))
            {
                warnings.Add($"Entry {index} ({ScreenerEntryText.Describe(entry)}): {problem}, so it never matches.");
            }
        }

        return warnings;
    }

    private IEnumerable<string> Problems(ScreenerIndicatorSideConfig side)
    {
        // Only an indicator side is evaluated through the indicator calculation; prices, columns and patterns are read directly.
        if (side.CategoryType != ScreenerItemCategoryType.Indicator || side.IndicatorType == IndicatorType.Price)
        {
            yield break;
        }

        if (side.TimeFrame != TimeFrame.D1)
        {
            yield return $"the {side.IndicatorType} side uses the {side.TimeFrame} time frame and the screener evaluates D1 only";
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
        else if (!BacktestIndicatorEligibility.IsAllowed(side.IndicatorType, side.OutputName))
        {
            yield return $"{side.IndicatorType} ({side.OutputName}) looks ahead or cannot be calculated bar by bar";
        }
    }
}
