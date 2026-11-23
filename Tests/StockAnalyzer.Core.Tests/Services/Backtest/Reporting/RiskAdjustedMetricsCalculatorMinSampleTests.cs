using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Backtest.Reporting;

public sealed class RiskAdjustedMetricsCalculatorMinSampleTests
{
    [Fact]
    public void MinReturnSamplesForRatioMetrics_IsTwenty()
    {
        Assert.Equal(20, BacktestReportGenerator.MinReturnSamplesForRatioMetrics);
    }

    [Fact]
    public void AnnualizedSortinoBootstrap_TransitionsFromSampleTooSmallAt19ToValidAt20()
    {
        EquitySample sample19 = CreateVariedSample(BacktestReportGenerator.MinReturnSamplesForRatioMetrics - 1);
        EquitySample sample20 = CreateVariedSample(BacktestReportGenerator.MinReturnSamplesForRatioMetrics);

        var below = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample19,
            mar: 0m,
            annualPeriods: 252,
            bootstrapSeed: 42,
            bootstrapIterations: 200);

        Assert.Equal(MetricStatus.InsufficientData, below.Point.Status);
        Assert.Equal(MetricReason.SampleTooSmall, below.Point.Reason);
        Assert.Null(below.Interval);

        var atThreshold = RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
            sample20,
            mar: 0m,
            annualPeriods: 252,
            bootstrapSeed: 42,
            bootstrapIterations: 200);

        Assert.Equal(MetricStatus.Valid, atThreshold.Point.Status);
        Assert.NotNull(atThreshold.Point.Value);
        Assert.NotNull(atThreshold.Interval);
    }

    private static EquitySample CreateVariedSample(int m)
    {
        var returns = ImmutableArray.CreateBuilder<double>(m);
        var equity = ImmutableArray.CreateBuilder<decimal>(m + 1);
        decimal current = 1000m;
        equity.Add(current);

        for (int i = 0; i < m; i++)
        {
            double r = (i % 3) switch
            {
                0 => 0.015,
                1 => -0.010,
                _ => 0.005,
            };
            returns.Add(r);
            current = decimal.Round(current * (1m + (decimal)r), 6);
            equity.Add(current);
        }

        return new EquitySample
        {
            Equity = equity.ToImmutable(),
            Returns = returns.ToImmutable(),
            HasNegativeEquityPeriod = false,
            ReturnFailureReason = null,
        };
    }
}
