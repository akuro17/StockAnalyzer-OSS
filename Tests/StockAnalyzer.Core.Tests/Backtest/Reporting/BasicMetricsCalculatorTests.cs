using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class BasicMetricsCalculatorTests
{
    [Fact]
    public void ClosedNet_10_Neg5_0_WinRate_OneThird_PF_2()
    {
        var trades = ImmutableArray.Create(
            ReportTestHelpers.Trade(10m),
            ReportTestHelpers.Trade(-5m),
            ReportTestHelpers.Trade(0m));

        var winRate = BasicMetricsCalculator.ComputeWinRate(trades);
        var pf = BasicMetricsCalculator.ComputeProfitFactor(trades);
        var expectedPayoff = BasicMetricsCalculator.ComputeExpectedPayoff(trades);

        Assert.Equal(1m / 3m, winRate.Value);
        Assert.Equal(2m, pf.Value);
        Assert.Equal(5m / 3m, expectedPayoff.Value);
    }

    [Fact]
    public void PF_Empty_InsufficientData()
    {
        var trades = ImmutableArray<BacktestTrade>.Empty;

        Assert.Equal(MetricStatus.InsufficientData, BasicMetricsCalculator.ComputeProfitFactor(trades).Status);
        Assert.Equal(MetricStatus.InsufficientData, BasicMetricsCalculator.ComputeWinRate(trades).Status);
        Assert.Equal(MetricStatus.InsufficientData, BasicMetricsCalculator.ComputeExpectedPayoff(trades).Status);
    }

    [Fact]
    public void PF_AllZero_Undefined()
    {
        var trades = ImmutableArray.Create(ReportTestHelpers.Trade(0m), ReportTestHelpers.Trade(0m));
        var pf = BasicMetricsCalculator.ComputeProfitFactor(trades);

        Assert.Equal(MetricStatus.Undefined, pf.Status);
        Assert.Equal(MetricReason.AllBreakeven, pf.Reason);
    }

    [Fact]
    public void PF_AllWin_PositiveInfinity()
    {
        var trades = ImmutableArray.Create(ReportTestHelpers.Trade(10m), ReportTestHelpers.Trade(5m));
        var pf = BasicMetricsCalculator.ComputeProfitFactor(trades);

        Assert.Equal(MetricStatus.PositiveInfinity, pf.Status);
        Assert.Null(pf.Value);
    }

    [Fact]
    public void PF_AllLoss_Zero()
    {
        var trades = ImmutableArray.Create(ReportTestHelpers.Trade(-10m), ReportTestHelpers.Trade(-5m));
        var pf = BasicMetricsCalculator.ComputeProfitFactor(trades);

        Assert.Equal(MetricStatus.Valid, pf.Status);
        Assert.Equal(0m, pf.Value);
    }

    [Fact]
    public void CAGR_SameEquity_Zero()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 100m });
        var options = ReportTestHelpers.Options(startUtc: ReportTestHelpers.BaseUtc, endUtc: ReportTestHelpers.BaseUtc.AddDays(365.2425));
        var sample = EquitySample.Build(result, options);

        var cagr = BasicMetricsCalculator.ComputeCagr(sample.Equity, options);

        Assert.Equal(MetricStatus.Valid, cagr.Status);
        Assert.Equal(0m, Math.Round(cagr.Value!.Value, 6));
    }

    [Fact]
    public void CAGR_FinalZero_MinusOne()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 0m });
        var options = ReportTestHelpers.Options(endUtc: ReportTestHelpers.BaseUtc.AddDays(365));
        var sample = EquitySample.Build(result, options);

        var cagr = BasicMetricsCalculator.ComputeCagr(sample.Equity, options);

        Assert.Equal(MetricStatus.Valid, cagr.Status);
        Assert.Equal(-1m, cagr.Value);
    }

    [Fact]
    public void CAGR_FinalNegative_Undefined()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { -10m });
        var options = ReportTestHelpers.Options(endUtc: ReportTestHelpers.BaseUtc.AddDays(365));
        var sample = EquitySample.Build(result, options);

        var cagr = BasicMetricsCalculator.ComputeCagr(sample.Equity, options);

        Assert.Equal(MetricStatus.Undefined, cagr.Status);
        Assert.Equal(MetricReason.NegativeFinalEquity, cagr.Reason);
    }

    [Fact]
    public void CAGR_ZeroPeriod_Undefined()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m });
        var options = ReportTestHelpers.Options(startUtc: ReportTestHelpers.BaseUtc, endUtc: ReportTestHelpers.BaseUtc);
        var sample = EquitySample.Build(result, options);

        var cagr = BasicMetricsCalculator.ComputeCagr(sample.Equity, options);

        Assert.Equal(MetricStatus.Undefined, cagr.Status);
        Assert.Equal(MetricReason.ZeroPeriod, cagr.Reason);
    }

    [Fact]
    public void TotalPnL_NoEquityPoints_InsufficientData()
    {
        var result = ReportTestHelpers.BuildResult(100m, Array.Empty<decimal>());
        var sample = EquitySample.Build(result, ReportTestHelpers.Options());

        var totalPnL = BasicMetricsCalculator.ComputeTotalPnL(sample.Equity);

        Assert.Equal(MetricStatus.InsufficientData, totalPnL.Status);
    }
}
