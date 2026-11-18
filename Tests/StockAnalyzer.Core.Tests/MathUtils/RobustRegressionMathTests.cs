using System;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="RobustRegressionMath"/> to exact rational references (Python <c>fractions</c>, medians of type 7) and to the breakdown behaviour that separates
/// the two estimators: Theil-Sen survives about 29 % outliers, Siegel up to 50 %.
/// <para>
/// Reference recipe: python fractions.Fraction, the pair slopes (y_j - y_i) / (j - i), the type 7 median as the mean of the two middle values for an even count, the intercept as the median of y_i - m i, sigma = 1.482602218505602 x MAD. Not re-run on 2026-11-18.
/// Environment of 2026-11-18: Python with scipy 1.18.1, mpmath 1.3.0, numpy 2.5.2; statsmodels and arch are NOT installed.
/// </para>
/// </summary>
public class RobustRegressionMathTests
{
    private const decimal Tolerance = 1e-18m;

    private static RobustLine Fit(decimal[] window, bool siegel)
    {
        decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(window.Length, siegel)];
        Assert.True(RobustRegressionMath.TryFit(window, siegel, scratch, out RobustLine line));
        return line;
    }

    private static void AssertLine(RobustLine line, decimal slope, decimal intercept, decimal fittedLast, decimal sigma)
    {
        Assert.InRange(line.Slope, slope - Tolerance, slope + Tolerance);
        Assert.InRange(line.Intercept, intercept - Tolerance, intercept + Tolerance);
        Assert.InRange(line.FittedLast, fittedLast - Tolerance, fittedLast + Tolerance);
        Assert.InRange(line.ResidualSigma, sigma - Tolerance, sigma + Tolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoPoints_GiveTheLineThroughThem(bool siegel)
    {
        RobustLine line = Fit(new[] { 10.5m, 13m }, siegel);

        Assert.Equal(2.5m, line.Slope);
        Assert.Equal(10.5m, line.Intercept);
        Assert.Equal(13m, line.FittedLast);
        Assert.Equal(0m, line.ResidualSigma);
        Assert.Equal(1, RobustRegressionMath.PairCount(2));
    }

    [Fact]
    public void EvenNumberOfPairSlopes_UsesTheMeanOfTheTwoMiddleSlopes()
    {
        // Pair slopes of {0, 1, 3, 4}: 1, 1.5, 4/3, 2, 1.5, 1 -> sorted 1, 1, 4/3, 1.5, 1.5, 2: M = 6 (even) -> slope = (4/3 + 1.5) / 2 = 1.41666...
        Assert.Equal(6, RobustRegressionMath.PairCount(4));
        RobustLine line = Fit(new[] { 0m, 1m, 3m, 4m }, siegel: false);

        AssertLine(line, 1.41666666666666666667m, -0.125m, 4.125m, 0.30887546218866708333m);
    }

    [Fact]
    public void NoisyLine_MatchesTheExactReference_InBothModes()
    {
        decimal[] window = { 100.25m, 100.9m, 100.4m, 101.6m, 101.1m, 102.35m, 101.9m, 103.2m, 102.8m, 104.05m, 103.4m, 105.1m };

        AssertLine(Fit(window, false), 0.4m, 100.3m, 104.7m, 0.4447806655516806m);
        AssertLine(Fit(window, true), 0.4m, 100.3m, 104.7m, 0.4447806655516806m);
    }

    [Fact]
    public void TheTwoEstimatorsDiffer_WhereOutliersLeaveTheMedianOfTheSlopes()
    {
        decimal[] window = { 99.21m, 99.91m, 108.98m, 99.44m, 100.48m, 102.99m, 102.81m, 110.52m, 102.8m, 102.77m, 111.51m };

        AssertLine(Fit(window, false), 0.58m, 99.33m, 105.13m, 1.73464459565155434m);
        AssertLine(Fit(window, true), 0.65m, 99.21m, 105.71m, 2.38698957179401922m);
    }

    [Fact]
    public void TwentyFivePercentOutliers_DoNotMoveTheTheilSenLine()
    {
        // y = 5 + 0.5 i with 5 of 20 values (25 %) replaced by large errors of both signs.
        decimal[] window = new decimal[20];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = 5m + (0.5m * i);
        }

        window[3] = -22.5m;
        window[8] = 115m;
        window[11] = 37.5m;
        window[15] = 67.5m;
        window[18] = 190m;

        foreach (bool siegel in new[] { false, true })
        {
            RobustLine line = Fit(window, siegel);
            Assert.Equal(0.5m, line.Slope);
            Assert.Equal(5m, line.Intercept);
            Assert.Equal(14.5m, line.FittedLast);
            Assert.Equal(0m, line.ResidualSigma); // more than half of the residuals are exactly 0
        }
    }

    [Fact]
    public void FortyPercentOutliers_BreakTheTheilSenSlope_ButNotTheSiegelSlope()
    {
        // The last 8 of 20 values (40 %) are far above the line y = 5 + 0.5 i.
        decimal[] window = new decimal[20];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = 5m + (0.5m * i);
        }

        for (int i = 12; i < 20; i++)
        {
            window[i] += 100m + (13m * (i - 12));
        }

        RobustLine theilSen = Fit(window, siegel: false);
        RobustLine siegelLine = Fit(window, siegel: true);

        AssertLine(siegelLine, 0.5m, 5m, 14.5m, 0m);
        AssertLine(theilSen, 12.23901098901098901099m, -33.97664835164835164835m, 198.56456043956043956044m, 17.40428373536933336813m);
    }

    [Fact]
    public void ADecimalOverflow_IsReportedAsFailure_NotAsAnException()
    {
        decimal[] window = { decimal.MaxValue, decimal.MinValue, decimal.MaxValue };
        decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(3, false)];

        Assert.False(RobustRegressionMath.TryFit(window, siegel: false, scratch, out RobustLine line));
        Assert.Equal(default, line);
    }

    [Fact]
    public void BadArguments_AreRejected()
    {
        decimal[] scratch = new decimal[1000];

        Assert.False(RobustRegressionMath.TryFit(new[] { 1m }, false, scratch, out _));
        Assert.False(RobustRegressionMath.TryFit(ReadOnlySpan<decimal>.Empty, false, scratch, out _));
        Assert.False(RobustRegressionMath.TryFit(new[] { 1m, 2m, 3m, 4m }, false, new decimal[5], out _));
        Assert.False(RobustRegressionMath.TryFit(new[] { 1m, 2m, 3m, 4m }, true, new decimal[RobustRegressionMath.ScratchLength(4, true) - 1], out _));
        Assert.Equal(2, RobustRegressionMath.MinWindowLength);
        Assert.Equal(30, RobustRegressionMath.MaxWindowLength);
        decimal[] tooLong = new decimal[RobustRegressionMath.MaxWindowLength + 1];
        for (int i = 0; i < tooLong.Length; i++)
        {
            tooLong[i] = i;
        }

        Assert.False(RobustRegressionMath.TryFit(tooLong, false, new decimal[RobustRegressionMath.ScratchLength(tooLong.Length, false)], out _));
        Assert.True(RobustRegressionMath.TryFit(tooLong.AsSpan(0, RobustRegressionMath.MaxWindowLength), true, new decimal[RobustRegressionMath.ScratchLength(RobustRegressionMath.MaxWindowLength, true)], out RobustLine longest));
        Assert.Equal(1m, longest.Slope);
        Assert.Equal(6 + 8, RobustRegressionMath.ScratchLength(4, false));
        Assert.Equal(7 + 8, RobustRegressionMath.ScratchLength(4, true));
    }

    [Fact]
    public void Kernel_AllocatesNothing()
    {
        decimal[] window = { 100.25m, 100.9m, 100.4m, 101.6m, 101.1m, 102.35m, 101.9m, 103.2m };
        decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(window.Length, true)];
        RobustRegressionMath.TryFit(window, false, scratch, out _);
        RobustRegressionMath.TryFit(window, true, scratch, out _);

        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                RobustRegressionMath.TryFit(window, false, scratch, out _);
                RobustRegressionMath.TryFit(window, true, scratch, out _);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
