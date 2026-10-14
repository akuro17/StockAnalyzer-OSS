using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.StoredPeriod;

namespace StockAnalyzer.Core.Services.StoredPeriod;

/// <summary>The finalized stored dates of one symbol (strictly increasing). Finality and the end bound are applied by the reader.</summary>
public sealed record StoredPeriodCalendar(string Symbol, ImmutableArray<DateOnly> Dates);

/// <summary>
/// The pure arithmetic of the stored-period proposal (no I/O, no clock). Latest = the newest date over the symbols with at least one date; Start = the
/// earliest of the per-symbol starts, where a symbol with at least <c>requiredBars</c> dates starts at its <c>requiredBars</c>-th date from the end
/// (exactly that many bars lie in [start, last]) and a shorter symbol starts at its first date and is reported as short.
/// </summary>
public static class StoredPeriodMath
{
    public static StoredPeriodProposal Propose(IReadOnlyList<StoredPeriodCalendar> finalizedCalendars, int requiredBars)
    {
        ArgumentNullException.ThrowIfNull(finalizedCalendars);
        if (requiredBars <= 0) return StoredPeriodProposal.None(StoredPeriodStatus.NoProposal, requiredBars);
        var usable = finalizedCalendars.Where(c => !c.Dates.IsDefaultOrEmpty).ToArray();
        if (usable.Length == 0) return StoredPeriodProposal.None(StoredPeriodStatus.NoData, requiredBars);
        DateOnly latest = usable.Max(c => c.Dates[^1]);
        if (latest == DateOnly.MaxValue) return StoredPeriodProposal.None(StoredPeriodStatus.NoProposal, requiredBars);

        DateOnly start = DateOnly.MaxValue;
        var shortCalendars = new List<StoredPeriodCalendar>();
        foreach (var calendar in usable)
        {
            DateOnly symbolStart;
            if (calendar.Dates.Length >= requiredBars) symbolStart = calendar.Dates[calendar.Dates.Length - requiredBars];
            else { symbolStart = calendar.Dates[0]; shortCalendars.Add(calendar); }
            if (symbolStart < start) start = symbolStart;
        }
        var shortOrdered = shortCalendars.OrderBy(c => c.Dates.Length).ThenBy(c => c.Symbol, StringComparer.Ordinal).ToArray();
        int available = shortOrdered.Length > 0 ? shortOrdered[0].Dates.Length : usable.Min(c => c.Dates.Length);
        var status = shortOrdered.Length > 0 ? StoredPeriodStatus.InsufficientHistory : StoredPeriodStatus.Found;
        return new StoredPeriodProposal(status, start, latest, requiredBars, available, shortOrdered.Select(c => c.Symbol).ToArray());
    }
}
