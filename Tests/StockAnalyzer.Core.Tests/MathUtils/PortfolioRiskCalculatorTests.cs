using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// <see cref="PortfolioRiskCalculator"/> against an independent calculation of the definitions in its summary, on closes built from chosen returns.
/// </summary>
public sealed class PortfolioRiskCalculatorTests
{
    private const double Tolerance = 1e-9;
    private const double ConfidenceLevel = 0.95;
    private static readonly DateTime Start = new(2024, 1, 1);

    // 20 returns per symbol: the 5 % quantile index is floor(0.05 x 20) = 1, so the two worst days are the tail.
    private static readonly decimal[] ReturnsA =
        { 0.010m, -0.020m, 0.015m, 0.005m, -0.030m, 0.020m, 0.000m, -0.010m, 0.025m, -0.005m, 0.012m, -0.018m, 0.007m, 0.003m, -0.025m, 0.016m, 0.009m, -0.004m, 0.011m, -0.013m };

    private static readonly decimal[] ReturnsB =
        { -0.005m, 0.010m, -0.012m, 0.008m, 0.020m, -0.015m, 0.004m, 0.006m, -0.022m, 0.013m, -0.007m, 0.009m, -0.011m, 0.002m, 0.018m, -0.014m, 0.005m, 0.001m, -0.009m, 0.017m };

    private static IReadOnlyList<CandleData> Series(decimal first, decimal[] returns)
    {
        var bars = new List<CandleData>(returns.Length + 1);
        decimal close = first;
        bars.Add(new CandleData(Start, close, close, close, close, 1000));
        for (int i = 0; i < returns.Length; i++)
        {
            close *= 1m + returns[i];
            bars.Add(new CandleData(Start.AddDays(i + 1), close, close, close, close, 1000));
        }

        return bars;
    }

    private static Dictionary<string, IReadOnlyList<CandleData>> Candles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = Series(100m, ReturnsA),
        ["B"] = Series(50m, ReturnsB),
    };

    /// <summary>Weights and returns -> the figures by the definitions, written out independently of the calculator.</summary>
    private static (double Var, double Cvar, double Sigma, double[] Marginal, double[] Tail) Expected(double[] weights, decimal[][] returns)
    {
        int t = returns[0].Length;
        double[] portfolio = Enumerable.Range(0, t).Select(i => Enumerable.Range(0, weights.Length).Sum(j => weights[j] * (double)returns[j][i])).ToArray();
        double[] sorted = portfolio.OrderBy(r => r).ToArray();
        int k = Math.Clamp((int)Math.Floor((1 - ConfidenceLevel) * t), 0, t - 1);
        int[] worst = Enumerable.Range(0, t).OrderBy(i => portfolio[i]).Take(k + 1).ToArray();
        double[] means = returns.Select(r => r.Select(x => (double)x).Average()).ToArray();
        double[,] cov = new double[weights.Length, weights.Length];
        for (int a = 0; a < weights.Length; a++)
        {
            for (int b = 0; b < weights.Length; b++)
            {
                cov[a, b] = Enumerable.Range(0, t).Sum(i => ((double)returns[a][i] - means[a]) * ((double)returns[b][i] - means[b])) / (t - 1);
            }
        }

        double[] covW = Enumerable.Range(0, weights.Length).Select(a => Enumerable.Range(0, weights.Length).Sum(b => cov[a, b] * weights[b])).ToArray();
        double sigma = Math.Sqrt(Enumerable.Range(0, weights.Length).Sum(a => weights[a] * covW[a]));
        return (
            -sorted[k],
            -sorted.Take(k + 1).Average(),
            sigma,
            covW.Select(c => c / sigma).ToArray(),
            Enumerable.Range(0, weights.Length).Select(j => -worst.Sum(i => weights[j] * (double)returns[j][i]) / (k + 1)).ToArray());
    }

    private static void AssertClose(double expected, decimal? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange(Math.Abs(expected - (double)actual!.Value), 0.0, Tolerance);
    }

    [Fact]
    public void TwoLongHoldings_MatchTheDefinitions_AndTheComponentsAddUpToTheTotals()
    {
        PortfolioRiskResult result = PortfolioRiskCalculator.Calculate(
            new[] { new PortfolioExposure("A", 600m), new PortfolioExposure("B", 400m) }, Candles(), ConfidenceLevel);

        var expected = Expected(new[] { 0.6, 0.4 }, new[] { ReturnsA, ReturnsB });
        Assert.Equal(1000m, result.GrossExposure);
        Assert.Equal(1000m, result.NetExposure);
        Assert.Equal(20, result.CommonDateCount);
        Assert.Equal(1, result.TailIndex);
        AssertClose(expected.Var, result.ValueAtRisk);
        AssertClose(expected.Cvar, result.ConditionalValueAtRisk);
        AssertClose(expected.Sigma, result.PortfolioVolatility);
        Assert.Equal(new[] { "A", "B" }, result.Contributions.Select(c => c.Symbol));
        for (int i = 0; i < 2; i++)
        {
            AssertClose(expected.Marginal[i], result.Contributions[i].MarginalVolatility);
            AssertClose(expected.Tail[i], result.Contributions[i].TailComponentCVaR);
        }

        Assert.InRange(Math.Abs((double)result.Contributions.Sum(c => c.ComponentVolatility!.Value) - expected.Sigma), 0.0, Tolerance);
        Assert.InRange(Math.Abs((double)result.Contributions.Sum(c => c.TailComponentCVaR!.Value) - expected.Cvar), 0.0, Tolerance);
        Assert.True(result.ConditionalValueAtRisk >= result.ValueAtRisk);
    }

    [Fact]
    public void AShortHolding_HasANegativeWeight_AndTheGrossExposureCountsItsAbsoluteValue()
    {
        PortfolioRiskResult result = PortfolioRiskCalculator.Calculate(
            new[] { new PortfolioExposure("A", 600m), new PortfolioExposure("B", -400m) }, Candles(), ConfidenceLevel);

        var expected = Expected(new[] { 0.6, -0.4 }, new[] { ReturnsA, ReturnsB });
        Assert.Equal(1000m, result.GrossExposure);
        Assert.Equal(200m, result.NetExposure);
        Assert.Equal(-0.4m, result.Contributions[1].Weight);
        AssertClose(expected.Var, result.ValueAtRisk);
        AssertClose(expected.Cvar, result.ConditionalValueAtRisk);
        AssertClose(expected.Sigma, result.PortfolioVolatility);
    }

    [Fact]
    public void ASingleHolding_IsCalculated_AndTheCopyItNeedsIsNotReported()
    {
        PortfolioRiskResult result = PortfolioRiskCalculator.Calculate(new[] { new PortfolioExposure("A", 500m) }, Candles(), ConfidenceLevel);

        var expected = Expected(new[] { 1.0 }, new[] { ReturnsA });
        PortfolioRiskContribution only = Assert.Single(result.Contributions);
        Assert.Equal("A", only.Symbol);
        Assert.Equal(1m, only.Weight);
        AssertClose(expected.Var, result.ValueAtRisk);
        AssertClose(expected.Cvar, result.ConditionalValueAtRisk);
        AssertClose(expected.Sigma, result.PortfolioVolatility);
        Assert.DoesNotContain(result.Contributions, c => c.Symbol.Contains("pad", StringComparison.Ordinal));
    }

    [Fact]
    public void ALongAndAShortOfOneSymbol_AreNetted()
    {
        PortfolioRiskResult result = PortfolioRiskCalculator.Calculate(
            new[] { new PortfolioExposure("A", 300m), new PortfolioExposure("B", 200m), new PortfolioExposure("A", -100m) }, Candles(), ConfidenceLevel);

        Assert.Equal(600m, result.GrossExposure); // 300 + 200 + 100: the positions are not netted in the gross exposure
        Assert.Equal(400m, result.NetExposure);
        PortfolioRiskContribution a = result.Contributions.Single(c => c.Symbol == "A");
        Assert.Equal(200m, a.SignedValue);
        Assert.Equal(200m / 600m, a.Weight);
    }

    [Fact]
    public void AHoldingWithoutReturnData_IsExcluded_AndStaysInTheGrossExposure()
    {
        PortfolioRiskResult result = PortfolioRiskCalculator.Calculate(
            new[] { new PortfolioExposure("A", 500m), new PortfolioExposure("B", 300m), new PortfolioExposure("NODATA", 200m) }, Candles(), ConfidenceLevel);

        Assert.Equal(1000m, result.GrossExposure);
        Assert.Contains(result.ExcludedSymbols, e => e.Symbol == "NODATA");
        Assert.Equal(new[] { 0.5m, 0.3m }, result.Contributions.Select(c => c.Weight));
        var expected = Expected(new[] { 0.5, 0.3 }, new[] { ReturnsA, ReturnsB });
        AssertClose(expected.Var, result.ValueAtRisk);
    }

    [Fact]
    public void TooFewCommonDates_GivesNoFigures_AndSaysWhy()
    {
        var candles = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = Series(100m, ReturnsA.Take(1).ToArray()),
            ["B"] = Series(50m, ReturnsB.Take(1).ToArray()),
        };

        PortfolioRiskResult result = PortfolioRiskCalculator.Calculate(new[] { new PortfolioExposure("A", 1m), new PortfolioExposure("B", 1m) }, candles, ConfidenceLevel);

        Assert.Null(result.ValueAtRisk);
        Assert.Null(result.ConditionalValueAtRisk);
        Assert.Empty(result.Contributions);
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public void NoExposure_GivesNoFigures_AndAConfidenceLevelOutsideItsRangeIsRefused()
    {
        PortfolioRiskResult empty = PortfolioRiskCalculator.Calculate(Array.Empty<PortfolioExposure>(), Candles(), ConfidenceLevel);

        Assert.Equal(0m, empty.GrossExposure);
        Assert.Null(empty.ValueAtRisk);
        Assert.Throws<ArgumentOutOfRangeException>(() => PortfolioRiskCalculator.Calculate(Array.Empty<PortfolioExposure>(), Candles(), 0.4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortfolioRiskCalculator.Calculate(Array.Empty<PortfolioExposure>(), Candles(), 1.0));
    }

    [Fact]
    public void TheBeta_IsTheWeightedSumOfTheHoldingsBetas()
    {
        IReadOnlyList<CandleData> benchmark = Series(1000m, ReturnsA.Zip(ReturnsB, (a, b) => (a + b) / 2m).ToArray());
        var exposures = new[] { new PortfolioExposure("A", 600m), new PortfolioExposure("B", 400m) };
        CrossAssetStatsResult stats = ReturnStatistics.Calculate(Candles(), "BENCH", false, ReturnStatistics.SimpleReturnBasis, benchmark);

        PortfolioRiskResult result = PortfolioRiskCalculator.Calculate(exposures, Candles(), ConfidenceLevel, "BENCH", benchmark);

        double expected = 0.6 * stats.BetaAlpha[0].Beta!.Value + 0.4 * stats.BetaAlpha[1].Beta!.Value;
        AssertClose(expected, result.Beta);
        Assert.Null(PortfolioRiskCalculator.Calculate(exposures, Candles(), ConfidenceLevel).Beta);
    }
}
