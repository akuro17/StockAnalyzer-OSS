using System.Globalization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>The one way a screener comparison is written as text for a client (result comparisons and template entries).</summary>
internal static class ScreenerEntryText
{
    public static string Describe(ScreenerIndicatorEntry entry)
    {
        string right = entry.TargetMode switch
        {
            RightHandTargetMode.NumericValue => entry.RightNumericValue.ToString(CultureInfo.InvariantCulture),
            RightHandTargetMode.StringValue => $"\"{entry.RightStringValue}\"",
            _ => DescribeSide(entry.RightHand),
        };
        return $"{DescribeSide(entry.LeftHand)} {entry.Operator.ToSymbolString()} {right}";
    }

    /// <summary>The indicator with its parameters, or the price, column or pattern name (the side's own DisplayName would read "SMA (Market Cap)" for a column).</summary>
    public static string DescribeSide(ScreenerIndicatorSideConfig side)
    {
        string title = side.CategoryType == ScreenerItemCategoryType.Indicator && side.IndicatorType != IndicatorType.Price
            ? side.GetColumnHeaderTitle()
            : side.OutputName + (ScreenerSideFrames.ReadsBars(side) ? ScreenerIndicatorSideConfig.FrameLabel(side.TimeFrame) : string.Empty);
        return side.Offset > 0 ? $"{title} [{side.Offset} bars back]" : title;
    }
}
