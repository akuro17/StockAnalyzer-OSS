using System;
using StockAnalyzer.Core.MathUtils;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Measures <see cref="FresnelMath"/> against an independent oracle: the same Taylor series evaluated in 28-digit decimal arithmetic.
/// The oracle is reliable only while the largest Taylor term stays far below the decimal range, i.e. for |u| up to about 5
/// (the largest term is about exp(pi u^2 / 2): 1e17 at u = 5, whose 28-digit resolution is still 1e-11); beyond that nothing is measured here.
/// </summary>
public class FresnelMathAccuracyTests
{
    private const decimal PiOver2 = 1.5707963267948966192313216916m;
    private const double TaylorBranchLimit = 4.0;
    private const double OracleLimit = 5.0;
    private const double GridStep = 0.01;

    private readonly ITestOutputHelper _output;

    public FresnelMathAccuracyTests(ITestOutputHelper output) => _output = output;

    private static (decimal C, decimal S) Oracle(decimal u)
    {
        decimal arg = PiOver2 * u * u;
        decimal argSq = arg * arg;

        decimal termC = u, c = u;
        for (int n = 1; n < 400; n++)
        {
            termC *= -argSq / (2 * n * (2 * n - 1)) * (4 * n - 3) / (4 * n + 1);
            c += termC;
            if (n > (int)arg && Math.Abs(termC) < 1e-22m) break;
        }

        decimal termS = PiOver2 * u * u * u / 3m, s = termS;
        for (int n = 1; n < 400; n++)
        {
            termS *= -argSq / ((2 * n + 1) * (2 * n)) * (4 * n - 1) / (4 * n + 3);
            s += termS;
            if (n > (int)arg && Math.Abs(termS) < 1e-22m) break;
        }
        return (c, s);
    }

    private static double MaxAbsError(double from, double to)
    {
        double max = 0.0;
        for (double u = from; u <= to; u += GridStep)
        {
            var (c, s) = FresnelMath.Evaluate(u);
            var (oc, os) = Oracle((decimal)u);
            max = Math.Max(max, Math.Max(Math.Abs(c - (double)oc), Math.Abs(s - (double)os)));
        }
        return max;
    }

    [Fact]
    public void TaylorBranch_MaxAbsoluteErrorAgainstDecimalOracle_IsMeasured()
    {
        double max = MaxAbsError(GridStep, TaylorBranchLimit);
        _output.WriteLine($"Taylor branch (0 < u <= {TaylorBranchLimit}) max absolute error: {max:E3}");

        // Measured 7.2e-8 (documented on FresnelMath); the bound pins it so a worse implementation fails.
        Assert.True(max < 1e-7, $"Taylor branch error {max:E3}");
    }

    [Fact]
    public void AsymptoticBranch_MaxAbsoluteErrorAgainstDecimalOracle_IsMeasured()
    {
        double max = MaxAbsError(TaylorBranchLimit + GridStep, OracleLimit);
        _output.WriteLine($"Asymptotic branch ({TaylorBranchLimit} < u <= {OracleLimit}) max absolute error: {max:E3}");

        // Measured 4.7e-8 (documented on FresnelMath).
        Assert.True(max < 1e-7, $"Asymptotic branch error {max:E3}");
    }

    [Fact]
    public void Evaluate_PositiveInfinity_ReturnsTheLimitHalf()
    {
        var (c, s) = FresnelMath.Evaluate(double.PositiveInfinity);

        Assert.Equal(0.5, c);
        Assert.Equal(0.5, s);
    }

    [Fact]
    public void Evaluate_NegativeInfinity_ReturnsTheNegativeLimit()
    {
        var (c, s) = FresnelMath.Evaluate(double.NegativeInfinity);

        Assert.Equal(-0.5, c);
        Assert.Equal(-0.5, s);
    }
}
