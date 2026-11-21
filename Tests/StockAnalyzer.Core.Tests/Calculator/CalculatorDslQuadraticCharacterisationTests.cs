#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// Characterisation of <see cref="DslElementaryMath.SolveQuadratic"/> over a grid of 1141 coefficient sets (scales 1e-20..1e9 of the leading coefficient, roots up to 1e12), recorded before the
/// degeneracy rule was made dimension-aware (backlog D-2b). Every case must reproduce the recorded result digit for digit, except the 230 cases listed as corrected: those were reviewed one by
/// one (before and after) and are exactly the genuine quadratics that the former rule reduced to a linear equation or to a division by zero.
/// </summary>
public sealed class CalculatorDslQuadraticCharacterisationTests
{
    private const int RootBufferLength = 2;
    private const int ReviewedCorrectionCount = 230;
    private const double ResidualTolerance = 1e-9;
    private const double FormerRelativeThreshold = 1e-12;
    private const double RootScaleLimit = 1e12;

    private static string KeyOf(string line) => line.Split('|')[0];

    private static double[] Coefficients(string key) => key.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void EveryRecordedCaseIsUnchangedExceptTheReviewedCorrections()
    {
        Dictionary<string, string> corrections = CalculatorDslQuadraticCharacterisationData.CorrectedCases.ToDictionary(KeyOf, l => l, StringComparer.Ordinal);
        int mismatches = 0;
        string first = string.Empty;
        foreach (string line in CalculatorDslQuadraticCharacterisationData.Cases)
        {
            string key = KeyOf(line);
            string expected = corrections.TryGetValue(key, out string? corrected) ? corrected : line;
            decimal[] k = key.Split(',').Select(s => decimal.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            decimal[] buffer = new decimal[RootBufferLength];
            MathErrorCode error = DslElementaryMath.SolveQuadratic(k[0], k[1], k[2], buffer, out int count);
            string actual = $"{key}|{error}|{count}" + string.Concat(buffer.Take(count).Select(r => "|" + r.ToString(CultureInfo.InvariantCulture)));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                mismatches++;
                if (first.Length == 0)
                {
                    first = $"expected {expected} but got {actual}";
                }
            }
        }

        Assert.True(mismatches == 0, $"{mismatches} of {CalculatorDslQuadraticCharacterisationData.Cases.Length} cases differ; first: {first}");
    }

    [Fact]
    public void EveryCorrectionIsAGenuineQuadraticThatTheFormerRuleReducedAndTheRootsSatisfyTheEquation()
    {
        HashSet<string> recorded = CalculatorDslQuadraticCharacterisationData.Cases.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ReviewedCorrectionCount, CalculatorDslQuadraticCharacterisationData.CorrectedCases.Length);
        foreach (string line in CalculatorDslQuadraticCharacterisationData.CorrectedCases)
        {
            string[] parts = line.Split('|');
            Assert.Contains(parts[0], recorded);
            double[] k = Coefficients(parts[0]);
            double a = k[0], b = k[1], c = k[2];

            // The former rule reduced the quadratic (|a| below 1e-12 times the largest coefficient) ...
            Assert.True(Math.Abs(a) < FormerRelativeThreshold * Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c))), $"{parts[0]} was not reduced by the former rule");

            // ... but the second root is within the root scale limit, so it is a genuine quadratic now.
            Assert.True(a != 0.0 && Math.Abs(a) * RootScaleLimit >= Math.Abs(b), $"{parts[0]} is still reducible");
            Assert.Equal("None", parts[1]);
            Assert.True(int.Parse(parts[2], CultureInfo.InvariantCulture) is 1 or 2);
            foreach (string root in parts.Skip(3))
            {
                double r = double.Parse(root, CultureInfo.InvariantCulture);
                double residual = Math.Abs((a * r * r) + (b * r) + c);
                double scale = (Math.Abs(a) * r * r) + (Math.Abs(b) * Math.Abs(r)) + Math.Abs(c);
                Assert.True(residual <= ResidualTolerance * scale, $"{parts[0]}: root {root} leaves a residual {residual}");
            }
        }
    }
}
