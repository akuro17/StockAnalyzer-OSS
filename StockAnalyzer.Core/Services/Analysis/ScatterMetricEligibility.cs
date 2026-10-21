using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Which Screener catalog items the registration screen offers as scatter metrics: every Indicator (the Price items included) and every Column that
/// holds a number the Screener's extractor can read. A Criteria (pattern), a text Column (Sector, Industry, Region, Name, Tag) and a Column that never
/// yields a number (<see cref="ColumnsWithoutAValue"/>) are not offered, and <see cref="ScatterMetricRules"/> refuses them in a saved definition.
/// The catalog itself already leaves out the blocked (look-ahead) indicators and series, so that is not judged again here.
/// </summary>
public static class ScatterMetricEligibility
{
    /// <summary>
    /// The Columns (by <see cref="ScreenerCatalogItem.ColumnMemberName"/>) that give no number for a ticker whose metadata is completely filled in, when
    /// they are built the way the registration screen builds them (the catalog description is the lookup key, see ScreenerValueExtractor.GetMetadataValue).
    /// Measured 2026-10-21 (plan finding F-28): 48 of the 100 non-text Columns. Some are not numbers at all (summary, notes, time zone, dates), the others
    /// are not mapped by the extractor under their catalog description. The owner decided (F-28 = B) to offer only the Columns that give a value. The test
    /// <c>ScatterMetricColumnAvailabilityTests</c> measures the extractor again and fails when this table and the measurement differ in either direction.
    /// Sorted by ordinal comparison.
    /// </summary>
    public static IReadOnlyList<string> ColumnsWithoutAValue { get; } = new[]
    {
        "AverageVolume",
        "Beta",
        "BookValue",
        "CurrentRatio",
        "DividendRate",
        "EarningsGrowth",
        "EarningsYield",
        "Ebitda",
        "ExDividendDate",
        "ExchangeTimezoneName",
        "ExitLong",
        "ExitShort",
        "FiftyTwoWeekHigh",
        "FiftyTwoWeekLow",
        "FiftyTwoWeekRangePosition",
        "ForwardEps",
        "ForwardPE",
        "FreeCashflow",
        "FullTimeEmployees",
        "GmtOffSetMilliseconds",
        "IsLong",
        "IsShort",
        "LastFiscalYearEnd",
        "LastUpdatedUtc",
        "Long",
        "LongBusinessSummary",
        "MetadataLastUpdated",
        "MostRecentQuarter",
        "NetCashRatio",
        "NetDebt",
        "Notes",
        "PayoutRatio",
        "PegRatio",
        "QuickRatio",
        "QuoteType",
        "Reminder",
        "SharesOutstanding",
        "Short",
        "StopLossLong",
        "StopLossShort",
        "TargetHighPrice",
        "TargetLowPrice",
        "TargetMeanPrice",
        "TargetMedianPrice",
        "TotalDebt",
        "TotalRevenue",
        "TrailingEps",
        "TrailingPE",
    };

    private static readonly HashSet<string> ExcludedMembers = new(ColumnsWithoutAValue, StringComparer.Ordinal);

    // Every name a Column side can be looked up by (member name, short name, description) for an excluded Column, minus the names an offered Column also uses.
    private static readonly Lazy<HashSet<string>> ExcludedLookupNames = new(BuildExcludedLookupNames);

    public static bool IsEligible(ScreenerCatalogItem? item)
    {
        if (item is null)
        {
            return false;
        }

        return item.CategoryType switch
        {
            ScreenerItemCategoryType.Indicator => true,
            ScreenerItemCategoryType.Column => !IsTextColumn(item.ColumnMemberName ?? item.ShortName)
                                               && !ExcludedMembers.Contains(item.ColumnMemberName ?? item.ShortName),
            _ => false,
        };
    }

    /// <summary>
    /// True when the Column <paramref name="side"/> names (the name the extractor looks up, <see cref="ScreenerIndicatorSideConfig.ResolveLookupName"/>)
    /// is one of <see cref="ColumnsWithoutAValue"/>. The registration screen cannot produce such a side; this judges a stored definition.
    /// </summary>
    public static bool IsExcludedColumn(ScreenerIndicatorSideConfig? side)
    {
        string? name = side?.ResolveLookupName();
        return side is { CategoryType: ScreenerItemCategoryType.Column }
               && name is not null
               && ExcludedLookupNames.Value.Contains(name);
    }

    /// <summary>
    /// True when the metric <paramref name="side"/> can be read on another time frame than Day: an Indicator (the Price items included) is computed from
    /// the bars of its time frame, and so is a Column that names a bar value (Open, High, Low, Close, Volume, Change, Change %). A fundamental Column belongs
    /// to the ticker, not to a bar, so it stays on Day.
    /// </summary>
    public static bool AllowsTimeFrameChoice(ScreenerIndicatorSideConfig? side)
        => side?.CategoryType switch
        {
            ScreenerItemCategoryType.Indicator => true,
            ScreenerItemCategoryType.Column => ScreenerValueExtractor.IsCandleValueColumn(side),
            _ => false,
        };

    private static bool IsTextColumn(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && ScreenerTextFields.Names.Any(field => string.Equals(field, name, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> BuildExcludedLookupNames()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in WatchlistColumnRegistry.AllColumns)
        {
            HashSet<string> target = ExcludedMembers.Contains(column.MemberName) ? excluded : offered;
            target.Add(column.MemberName);
            target.Add(column.DisplayName ?? column.MemberName);
            target.Add(column.Description ?? column.MemberName);
        }

        excluded.ExceptWith(offered);
        return excluded;
    }
}
