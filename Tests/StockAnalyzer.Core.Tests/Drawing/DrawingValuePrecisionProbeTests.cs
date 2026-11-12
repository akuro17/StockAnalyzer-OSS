using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>
/// Probe for owner decision D-5: the time ratio is a double that is converted to decimal, so the value of a drawing can differ from the exact decimal result in its low digits.
/// The criterion is the text the Data tab shows: the F3 text of the code's value must equal the F3 text of the exact value. A failure means decision D-5 option B must be reviewed
/// by the owner; it is not fixed here.
/// </summary>
public sealed class DrawingValuePrecisionProbeTests
{
    private readonly ITestOutputHelper _output;

    public DrawingValuePrecisionProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly DateTime T1 = new(2026, 1, 1);

    /// <summary>Owner decision D-5 (2026-11-12): the raw value of every row is within this relative error of the exact result ...</summary>
    private const decimal MaxRelativeError = 0.00000000000001m; // 1e-14

    /// <summary>... and within this absolute error.</summary>
    private const decimal MaxAbsoluteError = 0.0000000001m; // 1e-10

    /// <summary>An exact result this close to x.xxx5 (the rounding tie of the third decimal) may show either neighbour in F3.</summary>
    private const decimal TieDistance = 0.000000001m; // 1e-9

    private const decimal ThirdDecimalUnit = 0.001m;

    private static string F3(decimal value) => value.ToString("F3", DrawingValueFormat.Culture);

    /// <summary>True when <paramref name="exact"/> lies within <see cref="TieDistance"/> of a value x.xxx5.</summary>
    private static bool IsOnThirdDecimalRoundingTie(decimal exact)
    {
        decimal lower = Math.Floor(exact / ThirdDecimalUnit) * ThirdDecimalUnit;
        return Math.Abs(exact - (lower + ThirdDecimalUnit / 2m)) <= TieDistance;
    }

    /// <summary>The F3 texts of the two neighbours of a tie: the truncated side and the rounded-up side.</summary>
    private static string[] TieNeighbourTexts(decimal exact)
    {
        decimal lower = Math.Floor(exact / ThirdDecimalUnit) * ThirdDecimalUnit;
        return new[] { F3(lower), F3(lower + ThirdDecimalUnit) };
    }

    [Fact]
    public void InterpolatePrice_EveryRowIsWithinTheErrorBounds_AndTheF3TextEqualsTheExactOneExceptOnARoundingTie()
    {
        (decimal P1, decimal P2)[] prices = { (100m, 400m), (0.01m, 0.05m), (1000000m, 1000300m), (12345.6789m, 23456.7891m) };
        int[] denominators = { 3, 6, 7, 9, 11, 13 };
        var failures = new List<string>();
        int totalRows = 0;
        int tieRows = 0;

        foreach ((decimal p1, decimal p2) in prices)
        {
            decimal maxAbsolute = 0m;
            decimal maxRelative = 0m;
            int rows = 0;
            foreach (int n in denominators)
            {
                for (int k = 1; k < n; k++)
                {
                    decimal actual = DrawingValueMath.InterpolatePrice(T1, p1, T1.AddDays(n), p2, T1.AddDays(k));
                    decimal exact = p1 + ((p2 - p1) * k) / n;
                    string row = $"Interpolate p1={p1} p2={p2} k/n={k}/{n}: code {actual}, exact {exact}";
                    decimal absolute = Math.Abs(actual - exact);
                    decimal relative = exact != 0m ? absolute / Math.Abs(exact) : 0m;
                    maxAbsolute = Math.Max(maxAbsolute, absolute);
                    maxRelative = Math.Max(maxRelative, relative);
                    rows++;
                    totalRows++;

                    // (1) every row, no exception: the raw value.
                    if (absolute > MaxAbsoluteError || relative > MaxRelativeError)
                    {
                        failures.Add($"{row}: error outside the bounds (absolute {absolute}, relative {relative})");
                    }

                    // (2) the text: equal, except on a rounding tie of the third decimal where both neighbours are accepted.
                    if (IsOnThirdDecimalRoundingTie(exact))
                    {
                        tieRows++;
                        if (!TieNeighbourTexts(exact).Contains(F3(actual)))
                        {
                            failures.Add($"{row}: rounding tie, F3 {F3(actual)} is neither of {string.Join(" / ", TieNeighbourTexts(exact))}");
                        }
                    }
                    else if (F3(actual) != F3(exact))
                    {
                        failures.Add($"{row}: F3 {F3(actual)} differs from the exact F3 {F3(exact)}");
                    }
                }
            }

            _output.WriteLine($"InterpolatePrice p1={p1} p2={p2}: {rows} rows, max absolute error {maxAbsolute}, max relative error {maxRelative}");
        }

        _output.WriteLine($"{totalRows} rows, {tieRows} on a third-decimal rounding tie");
        Assert.Equal(172, totalRows);
        Assert.True(failures.Count == 0, "Decision D-5 bounds violated (option B must be reviewed):\n" + string.Join('\n', failures));
    }

    [Fact]
    public void GannFan_EveryAngleHasTheSameF3TextAsTheExactDecimalResult()
    {
        var p1 = new DrawingAnchor(T1, 100m);
        var p2 = new DrawingAnchor(T1.AddDays(10), 150m);
        decimal intervalSeconds = (decimal)(p2.Time - p1.Time).TotalSeconds;
        var mismatches = new List<string>();
        decimal maxAbsolute = 0m;

        foreach (DateTime time in new[] { new DateTime(2026, 1, 6, 12, 0, 0), new DateTime(2026, 1, 21, 18, 30, 15), new DateTime(2025, 12, 25, 6, 0, 0) })
        {
            decimal elapsedSeconds = (decimal)(time - p1.Time).TotalSeconds;
            IReadOnlyList<DrawingCalculatedValue> values = GannFanValues.Calculate(p1, p2, time, IndicatorColor.Gray);

            foreach ((int ratioTime, int ratioPrice) in GannFanValues.Ratios.ToArray())
            {
                DrawingCalculatedValue value = values.Single(v => v.Key == $"Gann_{ratioTime}x{ratioPrice}");
                decimal exact = 100m + (elapsedSeconds * 50m * ratioPrice) / (intervalSeconds * ratioTime);
                decimal actual = value.NumericValue!.Value;
                maxAbsolute = Math.Max(maxAbsolute, Math.Abs(actual - exact));
                if (F3(actual) != F3(exact) || value.FormattedText != F3(exact))
                {
                    mismatches.Add($"{value.Key} at {time:yyyy-MM-dd HH:mm:ss}: value {actual}, text '{value.FormattedText}', exact {exact} (F3 {F3(exact)})");
                }
            }
        }

        _output.WriteLine($"GannFan: max absolute error {maxAbsolute}");
        Assert.True(mismatches.Count == 0, "F3 text differs from the exact result (decision D-5 option B must be reviewed):\n" + string.Join('\n', mismatches));
    }
}
