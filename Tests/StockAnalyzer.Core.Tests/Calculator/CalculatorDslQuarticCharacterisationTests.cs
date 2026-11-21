#nullable enable
using System;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// Characterisation of the two quartic solvers: the exact error code, root count and every root (decimal string, full precision) of
/// <see cref="CalculatorDslQuarticCharacterisationData"/>, recorded before the shared Ferrari width computation was extracted into one helper (constraint-check M-2/M-3).
/// The production solver (<c>SolveQuarticDsl</c>) is frozen. The legacy solver (<c>SolveQuartic</c>) is pinned after the correction of its +-w pairing (2026-11-21); any further change
/// of its results is re-recorded only after a before/after review and with the owner's approval (see the data class).
/// </summary>
public sealed class CalculatorDslQuarticCharacterisationTests
{
    private const int RootBufferLength = 4;
    private const int MinimumCasesPerSolver = 500;

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private delegate MathErrorCode Solver(decimal a, decimal b, decimal c, decimal d, decimal e, Span<decimal> roots, out int rootCount);

    private static (int Mismatches, string First) Replay(string kind, string[] cases, Solver solve)
    {
        int mismatches = 0;
        string first = string.Empty;
        foreach (string line in cases)
        {
            string[] parts = line.Split('|');
            Assert.Equal(kind, parts[0]);
            decimal[] k = parts[1].Split(',').Select(s => decimal.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            decimal[] buffer = new decimal[RootBufferLength];
            MathErrorCode error = solve(k[0], k[1], k[2], k[3], k[4], buffer, out int count);
            string actual = $"{parts[0]}|{parts[1]}|{error}|{count}" + string.Concat(buffer.Take(count).Select(r => "|" + Format(r)));
            if (!string.Equals(actual, line, StringComparison.Ordinal))
            {
                mismatches++;
                if (first.Length == 0)
                {
                    first = $"expected {line} but got {actual}";
                }
            }
        }

        return (mismatches, first);
    }

    [Fact]
    public void TheProductionSolverReproducesTheFrozenResultsDigitForDigit()
    {
        (int mismatches, string first) = Replay("D", CalculatorDslQuarticCharacterisationData.ProductionSolverCases, DslElementaryMath.SolveQuarticDsl);
        Assert.True(mismatches == 0, $"{mismatches} of {CalculatorDslQuarticCharacterisationData.ProductionSolverCases.Length} frozen cases changed; first: {first}");
    }

    [Fact]
    public void TheLegacySolverReproducesTheRecordedBehaviour()
    {
        // Re-record only after a before/after review and with the owner's approval; see the data class.
        (int mismatches, string first) = Replay("L", CalculatorDslQuarticCharacterisationData.LegacySolverCases, DslElementaryMath.SolveQuartic);
        Assert.True(mismatches == 0, $"{mismatches} of {CalculatorDslQuarticCharacterisationData.LegacySolverCases.Length} recorded legacy cases changed; first: {first}");
    }

    [Theory]
    [InlineData(nameof(CalculatorDslQuarticCharacterisationData.ProductionSolverCases))]
    [InlineData(nameof(CalculatorDslQuarticCharacterisationData.LegacySolverCases))]
    public void TheRecordedDataCoversEveryRootCount(string set)
    {
        string[] cases = set == nameof(CalculatorDslQuarticCharacterisationData.ProductionSolverCases)
            ? CalculatorDslQuarticCharacterisationData.ProductionSolverCases
            : CalculatorDslQuarticCharacterisationData.LegacySolverCases;
        Assert.True(cases.Length >= MinimumCasesPerSolver);
        foreach (string rootCount in new[] { "0", "2", "4" })
        {
            Assert.Contains(cases, c => c.Split('|')[3] == rootCount);
        }
    }
}
