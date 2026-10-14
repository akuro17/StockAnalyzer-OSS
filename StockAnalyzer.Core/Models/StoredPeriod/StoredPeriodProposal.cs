using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Models.StoredPeriod;

/// <summary>Why a stored-period proposal was or was not made. The evaluation order is NoProposal, TooMany, NoData, InsufficientHistory, Found.</summary>
public enum StoredPeriodStatus
{
    /// <summary>Every usable symbol has at least the required number of bars up to the latest data date.</summary>
    Found = 0,
    /// <summary>At least one usable symbol has fewer bars than required; the proposal still carries the earliest usable start.</summary>
    InsufficientHistory = 1,
    /// <summary>No symbol has a usable stored bar.</summary>
    NoData = 2,
    /// <summary>More symbols than <see cref="StoredPeriodLimits.MaximumSymbols"/>; nothing was read.</summary>
    TooMany = 3,
    /// <summary>The proposal cannot be formed (the required bar count is not a positive number, or the latest date has no successor).</summary>
    NoProposal = 4,
}

/// <summary>
/// The stored period of a symbol set: the latest stored date and the earliest start from which the required number of bars is available.
/// </summary>
/// <param name="Start">The earliest start over the usable symbols (null for NoData, TooMany and NoProposal).</param>
/// <param name="Latest">The latest finalized stored date over the usable symbols (null for NoData, TooMany and NoProposal).</param>
/// <param name="RequiredBars">The required bar count the proposal was made for.</param>
/// <param name="AvailableBars">The bars of the first short symbol, else the smallest bar count over the usable symbols (0 without data).</param>
/// <param name="ShortSymbols">The symbols with fewer bars than required, fewest bars first, then by ordinal symbol.</param>
public sealed record StoredPeriodProposal(StoredPeriodStatus Status, DateOnly? Start, DateOnly? Latest, int RequiredBars, int AvailableBars,
    IReadOnlyList<string> ShortSymbols)
{
    /// <summary>Value equality: the symbol lists are compared element by element (the generated record equality would compare the list references).</summary>
    public bool Equals(StoredPeriodProposal? other)
        => other is not null && Status == other.Status && Start == other.Start && Latest == other.Latest && RequiredBars == other.RequiredBars
            && AvailableBars == other.AvailableBars && ShortSymbols.SequenceEqual(other.ShortSymbols);

    public override int GetHashCode() => HashCode.Combine(Status, Start, Latest, RequiredBars, AvailableBars, ShortSymbols.Count);

    /// <summary>A proposal without a period.</summary>
    public static StoredPeriodProposal None(StoredPeriodStatus status, int requiredBars = 0)
        => new(status, null, null, requiredBars, 0, Array.Empty<string>());
}
