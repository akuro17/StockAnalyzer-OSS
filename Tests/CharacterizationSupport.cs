using System;
using System.Collections.Generic;
using System.Globalization;
using StockAnalyzer.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.TestSupport;

/// <summary>
/// Shared plumbing of the characterization tests (captured-then-frozen expected lines). This one file is compiled into every test
/// assembly that has such tests (linked from the test projects), so the fixture and the comparison exist once.
/// </summary>
internal static class CharacterizationSupport
{
    /// <summary>Fixture F-OHLC8: eight bars whose row 3 mixes decimal scales 0, 1 and 4 so a scale change is detected.</summary>
    internal static readonly (decimal O, decimal H, decimal L, decimal C, long V)[] Ohlc8Rows =
    {
        (100.00m, 102.50m, 99.50m, 101.00m, 1000),
        (101.00m, 103.00m, 100.25m, 102.75m, 1200),
        (102.75m, 104.10m, 101.90m, 103.00m, 900),
        (103m, 103.0m, 100.0000m, 100.5m, 1500),
        (100.50m, 101.20m, 98.80m, 99.00m, 1100),
        (99.00m, 100.00m, 97.50m, 99.75m, 1300),
        (99.75m, 101.75m, 99.50m, 101.50m, 800),
        (101.50m, 103.20m, 101.00m, 102.90m, 1000),
    };

    internal static readonly DateTime Ohlc8FirstDay = new DateTime(2026, 1, 5);

    internal static CoreCandleData Ohlc8Core(int i) =>
        new CoreCandleData(Ohlc8FirstDay.AddDays(i), Ohlc8Rows[i].O, Ohlc8Rows[i].H, Ohlc8Rows[i].L, Ohlc8Rows[i].C, Ohlc8Rows[i].V);

    internal static CandleData Ohlc8Plain(int i) =>
        new CandleData(Ohlc8FirstDay.AddDays(i), Ohlc8Rows[i].O, Ohlc8Rows[i].H, Ohlc8Rows[i].L, Ohlc8Rows[i].C, Ohlc8Rows[i].V);

    /// <summary>"null", or the invariant text of the value followed by "#" and its <see cref="decimal.GetBits(decimal)"/> (digits and scale).</summary>
    internal static string Format(decimal? value) =>
        value.HasValue
            ? value.Value.ToString(CultureInfo.InvariantCulture) + "#" + string.Join(",", decimal.GetBits(value.Value))
            : "null";

    /// <summary>
    /// Compares the captured lines with the frozen ones. While <paramref name="expected"/> is empty (capture step) it prints every actual
    /// line through <paramref name="output"/> and fails, so the lines can be pasted into the test.
    /// </summary>
    internal static void AssertMatches(string[] expected, IReadOnlyList<string> actual, ITestOutputHelper output)
    {
        if (expected.Length == 0)
        {
            foreach (var line in actual)
            {
                output.WriteLine(line);
            }

            Assert.True(false, "Expected is empty: paste the printed lines into Expected (capture step).");
        }

        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }
}
