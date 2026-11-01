using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Tests.MathUtils;

public class ReturnStatisticsTests
{
    private const double Precision = 1e-9;

    private static CandleData MakeCandle(DateTime date, decimal close)
        => new(date, close, close, close, close, 1000);

    private static List<CandleData> MakeSeries(params (string Date, decimal Close)[] items)
        => items.Select(x => MakeCandle(DateTime.Parse(x.Date, CultureInfo.InvariantCulture), x.Close)).ToList();

    [Fact]
    public void IdenticalSeries_ReturnsCorrelationOneAndBetaOne()
    {
        var candlesA = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 105m),
            ("2024-01-03", 95m),
            ("2024-01-04", 102m),
            ("2024-01-05", 110m)
        );

        var candlesB = MakeSeries(
            ("2024-01-01", 50m),
            ("2024-01-02", 52.5m),
            ("2024-01-03", 47.5m),
            ("2024-01-04", 51m),
            ("2024-01-05", 55m)
        );

        var input = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(input, benchmarkSymbol: "B", includeCovariance: true);

        Assert.Equal(2, result.Symbols.Count);
        Assert.Equal(4, result.CommonDateCount);
        Assert.NotNull(result.CorrelationMatrix);
        Assert.Equal(1.0, result.CorrelationMatrix[0][0]!.Value, Precision);
        Assert.Equal(1.0, result.CorrelationMatrix[1][1]!.Value, Precision);
        Assert.Equal(1.0, result.CorrelationMatrix[0][1]!.Value, Precision);
        Assert.Equal(1.0, result.CorrelationMatrix[1][0]!.Value, Precision);

        Assert.NotNull(result.CovarianceMatrix);
        Assert.True(result.CovarianceMatrix[0][0] > 0);

        // Beta / Alpha vs benchmark B
        Assert.Equal(2, result.BetaAlpha.Count);
        var entryA = result.BetaAlpha.First(e => e.Symbol == "A");
        Assert.Equal(1.0, entryA.Beta!.Value, Precision);
        Assert.Equal(0.0, entryA.Alpha!.Value, Precision);
        Assert.Equal(1.0, entryA.RSquared!.Value, Precision);
    }

    [Fact]
    public void InverseSeries_ReturnsCorrelationNegativeOneAndBetaNegativeOne()
    {
        // R_A: +10%, -10%, +10%
        var candlesA = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 110m),
            ("2024-01-03", 99m),
            ("2024-01-04", 108.9m)
        );

        // R_B: -10%, +10%, -10%
        var candlesB = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 90m),
            ("2024-01-03", 99m),
            ("2024-01-04", 89.1m)
        );

        var input = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(input, benchmarkSymbol: "B");

        Assert.Equal(-1.0, result.CorrelationMatrix[0][1]!.Value, Precision);
        Assert.Equal(-1.0, result.CorrelationMatrix[1][0]!.Value, Precision);

        var entryA = result.BetaAlpha.First(e => e.Symbol == "A");
        Assert.Equal(-1.0, entryA.Beta!.Value, Precision);
        Assert.Equal(1.0, entryA.RSquared!.Value, Precision);
    }

    [Fact]
    public void HandCalculatedValues_MatchExactSampleCovarianceAndCorrelation()
    {
        // 4 bars -> 3 return dates (T = 3)
        // A returns: 0.05, -0.10, 0.05. Mean A = 0. Dev A = [0.05, -0.10, 0.05]. Var A = 0.015 / 2 = 0.0075
        var candlesA = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 105m),
            ("2024-01-03", 94.5m),
            ("2024-01-04", 99.225m)
        );

        // B returns: 0.10, -0.20, 0.10. Exactly 2 * A. Mean B = 0. Dev B = [0.10, -0.20, 0.10]. Var B = 0.060 / 2 = 0.030
        var candlesB = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 110m),
            ("2024-01-03", 88m),
            ("2024-01-04", 96.8m)
        );

        var input = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(input, benchmarkSymbol: "A", includeCovariance: true);

        Assert.Equal(3, result.CommonDateCount);
        Assert.NotNull(result.CovarianceMatrix);
        // Var A = 0.0075
        Assert.Equal(0.0075, result.CovarianceMatrix[0][0]!.Value, Precision);
        // Var B = 0.030
        Assert.Equal(0.030, result.CovarianceMatrix[1][1]!.Value, Precision);
        // Cov(A, B) = (0.05*0.10 + (-0.10)*(-0.20) + 0.05*0.10) / 2 = (0.005 + 0.020 + 0.005) / 2 = 0.015
        Assert.Equal(0.015, result.CovarianceMatrix[0][1]!.Value, Precision);
        Assert.Equal(0.015, result.CovarianceMatrix[1][0]!.Value, Precision);

        // Correlation = 0.015 / sqrt(0.0075 * 0.030) = 0.015 / sqrt(0.000225) = 0.015 / 0.015 = 1.0
        Assert.Equal(1.0, result.CorrelationMatrix[0][1]!.Value, Precision);

        // B vs Benchmark A: Beta = Cov(B, A) / Var(A) = 0.015 / 0.0075 = 2.0
        var entryB = result.BetaAlpha.First(e => e.Symbol == "B");
        Assert.Equal(2.0, entryB.Beta!.Value, Precision);
        Assert.Equal(0.0, entryB.Alpha!.Value, Precision);
        Assert.Equal(1.0, entryB.RSquared!.Value, Precision);
    }

    [Fact]
    public void ZeroVariance_ReturnsNullCorrelationAndRecordsNote()
    {
        // Flat series: 100, 100, 100, 100 -> returns are all 0
        var candlesFlat = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 100m),
            ("2024-01-03", 100m),
            ("2024-01-04", 100m)
        );

        var candlesNormal = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 105m),
            ("2024-01-03", 95m),
            ("2024-01-04", 102m)
        );

        var input = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["FLAT"] = candlesFlat,
            ["NORMAL"] = candlesNormal
        };

        var result = ReturnStatistics.Calculate(input, benchmarkSymbol: "FLAT", includeCovariance: true);

        Assert.Null(result.CorrelationMatrix[0][0]); // Flat diagonal is null
        Assert.Equal(1.0, result.CorrelationMatrix[1][1]!.Value, Precision);
        Assert.Null(result.CorrelationMatrix[0][1]);
        Assert.Null(result.CorrelationMatrix[1][0]);

        Assert.Contains(result.Notes, n => n.Contains("zero return variance", StringComparison.OrdinalIgnoreCase));

        // When benchmark has zero variance, beta is null
        var entryNormal = result.BetaAlpha.First(e => e.Symbol == "NORMAL");
        Assert.Null(entryNormal.Beta);
        Assert.Null(entryNormal.Alpha);
        Assert.Null(entryNormal.RSquared);
    }

    [Fact]
    public void InsufficientCommonDates_UnderThree_ReturnsEmptyMatrices()
    {
        // Only 3 bars -> 2 returns
        var candlesA = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 105m),
            ("2024-01-03", 95m)
        );
        var candlesB = MakeSeries(
            ("2024-01-01", 50m),
            ("2024-01-02", 52m),
            ("2024-01-03", 48m)
        );

        var input = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(input);

        Assert.Equal(2, result.CommonDateCount);
        Assert.Empty(result.CorrelationMatrix);
    }

    [Fact]
    public void DateAlignment_InnerJoinDiscardsNonOverlappingDates()
    {
        // A has Jan 1, 2, 3, 4, 5 -> returns on Jan 2, 3, 4, 5
        var candlesA = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 105m),
            ("2024-01-03", 95m),
            ("2024-01-04", 102m),
            ("2024-01-05", 110m)
        );

        // B has Jan 2, 3, 4, 5, 6 -> returns on Jan 3, 4, 5, 6
        var candlesB = MakeSeries(
            ("2024-01-02", 50m),
            ("2024-01-03", 48m),
            ("2024-01-04", 51m),
            ("2024-01-05", 55m),
            ("2024-01-06", 54m)
        );

        var input = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(input);

        // Common return dates: Jan 3, 4, 5 (3 dates)
        Assert.Equal(3, result.CommonDateCount);
        Assert.Equal(new DateTime(2024, 1, 3), result.StartDate);
        Assert.Equal(new DateTime(2024, 1, 5), result.EndDate);
        Assert.NotEmpty(result.CorrelationMatrix);
    }

    [Fact]
    public void ParityWithIndicatorCalculationHelper_CalculateRollingBetaStats()
    {
        // 10 daily bars
        var pricesA = new decimal[] { 100, 102, 98, 105, 110, 108, 115, 112, 120, 118 };
        var pricesB = new decimal[] { 50, 51, 49, 52, 54, 53, 55, 54, 58, 57 };

        var candlesA = pricesA.Select((p, i) => MakeCandle(new DateTime(2024, 1, 1).AddDays(i), p)).ToList();
        var candlesB = pricesB.Select((p, i) => MakeCandle(new DateTime(2024, 1, 1).AddDays(i), p)).ToList();

        var input = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(input, benchmarkSymbol: "B", returnBasis: "SimpleReturn");

        // Compute via IndicatorCalculationHelper
        var decimalPricesA = pricesA.Select(p => (decimal?)p).ToList();
        var decimalPricesB = pricesB.Select(p => (decimal?)p).ToList();
        var returnsA = IndicatorCalculationHelper.ConvertToSimpleReturns(decimalPricesA);
        var returnsB = IndicatorCalculationHelper.ConvertToSimpleReturns(decimalPricesB);

        int period = 9; // 9 returns across 10 bars
        var (betaList, alphaList, corrList) = IndicatorCalculationHelper.CalculateRollingBetaStats(returnsA, returnsB, period);

        decimal? expectedBeta = betaList[^1];
        decimal? expectedAlpha = alphaList[^1];
        decimal? expectedCorr = corrList[^1];

        var entryA = result.BetaAlpha.First(e => e.Symbol == "A");
        Assert.NotNull(expectedBeta);
        Assert.NotNull(expectedAlpha);
        Assert.NotNull(expectedCorr);

        Assert.Equal((double)expectedBeta.Value, entryA.Beta!.Value, Precision);
        Assert.Equal((double)expectedAlpha.Value, entryA.Alpha!.Value, Precision);
        Assert.Equal((double)expectedCorr.Value, result.CorrelationMatrix[0][1]!.Value, Precision);
    }

    [Fact]
    public void LogReturnBasis_ComputesLogReturnsCorrectly()
    {
        var candlesA = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 110m),
            ("2024-01-03", 99m),
            ("2024-01-04", 108.9m)
        );
        var candlesB = MakeSeries(
            ("2024-01-01", 50m),
            ("2024-01-02", 55m),
            ("2024-01-03", 49.5m),
            ("2024-01-04", 54.45m)
        );

        var input = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(input, benchmarkSymbol: "B", returnBasis: "LogReturn");

        Assert.Equal("LogReturn", result.ReturnBasis);
        Assert.Equal(1.0, result.CorrelationMatrix[0][1]!.Value, Precision);
        var entryA = result.BetaAlpha.First(e => e.Symbol == "A");
        Assert.Equal(1.0, entryA.Beta!.Value, Precision);
    }

    [Fact]
    public void ExternalBenchmark_ComputesBetaAlpha_WithoutPollutingSymbolsOrMatrices()
    {
        var candlesA = MakeSeries(
            ("2024-01-01", 100m),
            ("2024-01-02", 105m),
            ("2024-01-03", 95m),
            ("2024-01-04", 102m),
            ("2024-01-05", 110m)
        );

        var candlesB = MakeSeries(
            ("2024-01-01", 50m),
            ("2024-01-02", 52.5m),
            ("2024-01-03", 47.5m),
            ("2024-01-04", 51m),
            ("2024-01-05", 55m)
        );

        var benchmarkCandles = MakeSeries(
            ("2024-01-01", 200m),
            ("2024-01-02", 210m),
            ("2024-01-03", 190m),
            ("2024-01-04", 204m),
            ("2024-01-05", 220m)
        );

        // Universe only has A and B
        var input = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = candlesA,
            ["B"] = candlesB
        };

        var result = ReturnStatistics.Calculate(
            input,
            benchmarkSymbol: "BENCH",
            includeCovariance: true,
            benchmarkCandles: benchmarkCandles);

        // Universe symbols MUST only be A and B (2 items)
        Assert.Equal(2, result.Symbols.Count);
        Assert.Equal("A", result.Symbols[0]);
        Assert.Equal("B", result.Symbols[1]);

        // Matrices MUST be 2x2, NOT 3x3
        Assert.Equal(2, result.CorrelationMatrix.Length);
        Assert.Equal(2, result.CorrelationMatrix[0].Length);
        Assert.Equal(2, result.CovarianceMatrix!.Length);
        Assert.Equal(2, result.CovarianceMatrix[0].Length);

        // BetaAlpha has entries for universe symbols vs BENCH
        Assert.Equal(2, result.BetaAlpha.Count);
        Assert.Contains(result.BetaAlpha, e => e.Symbol == "A" && e.Beta.HasValue);
        Assert.Contains(result.BetaAlpha, e => e.Symbol == "B" && e.Beta.HasValue);
    }
}
