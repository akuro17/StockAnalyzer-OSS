using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.StoredPeriod;

namespace StockAnalyzer.Core.Services.StoredPeriod;

/// <summary>
/// The pure counting behind <see cref="IStoredPeriodAdvisor.ReadSymbolBarsForTrainingAsync"/> (no I/O): how many stored bars of each symbol lie in the period
/// of the Training Wizard, and from where the required number of bars is available. A separate class from <see cref="StoredPeriodMath"/>, which proposes one
/// period for all symbols and counts a short symbol from its first date.
/// </summary>
public static class StoredPeriodBarCounts
{
    /// <summary>Bars with start &lt;= date &lt;= end over strictly increasing dates; a null bound is unbounded; an empty period (start after end) is 0.</summary>
    public static int Count(ImmutableArray<DateOnly> dates, DateOnly? start, DateOnly? end)
    {
        if (dates.IsDefaultOrEmpty) return 0;
        int upTo = end is { } last ? OnOrBefore(dates, last) : dates.Length;
        int before = start is { } first ? Before(dates, first) : 0;
        return Math.Max(0, upTo - before);
    }

    /// <summary>One <see cref="SymbolBarInfo"/> per calendar, ordered by ordinal symbol name. A requirement that is not positive gives an empty list.</summary>
    public static IReadOnlyList<SymbolBarInfo> Build(IReadOnlyList<StoredPeriodCalendar> calendars, int requiredBars, DateOnly? start, DateOnly? end)
    {
        ArgumentNullException.ThrowIfNull(calendars);
        if (requiredBars <= 0) return Array.Empty<SymbolBarInfo>();
        var ordered = calendars.OrderBy(c => c.Symbol, StringComparer.Ordinal).ToArray();
        var requiredStarts = new DateOnly?[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            int upTo = Count(ordered[i].Dates, null, end);
            requiredStarts[i] = upTo >= requiredBars ? ordered[i].Dates[upTo - requiredBars] : null;
        }

        DateOnly? minimumStart = null;
        foreach (var candidate in requiredStarts)
            if (candidate is { } date && (minimumStart is not { } current || date < current)) minimumStart = date;

        var result = new SymbolBarInfo[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            var dates = ordered[i].Dates;
            result[i] = new SymbolBarInfo(ordered[i].Symbol, Count(dates, start, end), Count(dates, null, end), requiredStarts[i],
                minimumStart is { } from ? Count(dates, from, end) : null);
        }
        return result;
    }

    private static int OnOrBefore(ImmutableArray<DateOnly> dates, DateOnly bound)
    {
        int index = dates.BinarySearch(bound);
        return index >= 0 ? index + 1 : ~index;
    }

    private static int Before(ImmutableArray<DateOnly> dates, DateOnly bound)
    {
        int index = dates.BinarySearch(bound);
        return index >= 0 ? index : ~index;
    }
}
