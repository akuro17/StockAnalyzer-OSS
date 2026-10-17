using System;

namespace StockAnalyzer.Core.Models.StoredPeriod;

/// <summary>
/// The stored bars of one symbol, counted against the period of the Training Wizard and its required bar count (read-only facts; no judgement).
/// </summary>
/// <param name="Symbol">The symbol as the stored file is named.</param>
/// <param name="BarsInRange">Bars with start &lt;= date &lt;= end (a null bound is unbounded).</param>
/// <param name="BarsUpToEnd">Bars with date &lt;= end, whatever the start.</param>
/// <param name="RequiredStart">The date of the required-th bar counted back from the last bar on or before the end; null when <paramref name="BarsUpToEnd"/> is below the requirement.</param>
/// <param name="BarsFromMinimumStart">Bars with minimumStart &lt;= date &lt;= end, where minimumStart is the earliest <paramref name="RequiredStart"/> over all returned symbols; null when no symbol has a <paramref name="RequiredStart"/>.</param>
public sealed record SymbolBarInfo(string Symbol, int BarsInRange, int BarsUpToEnd, DateOnly? RequiredStart, int? BarsFromMinimumStart);
