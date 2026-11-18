using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// Flat daily bars (open = high = low = close) dated by a day number counted from <see cref="Day1"/>, for the tests that need two series with their own days and prices
/// (a market closed on a day the other traded on). Two fixed price formulas give two series that are not proportional to each other.
/// </summary>
internal static class DayNumberedBars
{
    /// <summary>Day number 1 (a Monday).</summary>
    public static readonly DateTime Day1 = new(2024, 1, 1);

    public static CandleData Bar(DateTime date, decimal close) => new(date, close, close, close, close, 1000);

    public static decimal PriceA(int day) => 100m + 3m * day + (day * day % 5);

    public static decimal PriceB(int day) => 50m + 2m * day + (day * 7 % 4);

    /// <summary>The two series as the universe of <c>ReturnStatistics.Calculate</c>, named A and B.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CandleData>> Universe(IReadOnlyList<CandleData> a, IReadOnlyList<CandleData> b)
        => new Dictionary<string, IReadOnlyList<CandleData>> { ["A"] = a, ["B"] = b };

    /// <summary>One bar for each of <paramref name="dayNumbers"/> (1 = <see cref="Day1"/>), priced by <paramref name="price"/> of the day number.</summary>
    public static List<CandleData> Days(Func<int, decimal> price, params int[] dayNumbers)
        => dayNumbers.Select(n => Bar(Day1.AddDays(n - 1), price(n))).ToList();
}
