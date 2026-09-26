using System;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L2 period boundary in the REPORT layer, pinning the CURRENT behavior (user direction: the current implementation is the specification):
/// E[0] = InitialCapital, E[1..m] = EquityPoints[HistoryStartIndex..] - the bar AT HistoryStartIndex is INCLUDED, m = max(0, points - start).
/// (The spec doc wording "at or before HistoryStartIndex are excluded" differs from this; the difference is listed in the completion report.)
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L2_ReportPeriodBoundaryTests
{
    private const decimal Initial = 1000m;
    private static readonly decimal[] PointEquity = { 1000m, 1100m, 900m, 1200m, 1000m, 1300m };
    private const double RatioTolerance = 1e-12;

    private static BacktestReport Report(int historyStartIndex)
        => new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(Initial, PointEquity),
            ReportTestHelpers.Options(historyStartIndex: historyStartIndex));

    [Fact]
    public void StartZero_UsesEveryPoint_AndInitialCapitalIsTheFirstPeak()
    {
        // E = [1000, 1000, 1100, 900, 1200, 1000, 1300] ; H = [1000, 1000, 1100, 1100, 1200, 1200, 1300]
        // worst drawdown: 1100 -> 900 = 200 / 1100.
        BacktestReport r = Report(0);

        Assert.Equal(MetricStatus.Valid, r.TotalPnL.Status);
        Assert.Equal(300m, r.TotalPnL.Value);                       // 1300 - 1000
        Assert.Equal(200m, r.MaxDrawdownAmount.Value);
        Assert.InRange((double)r.MaxDrawdown.Value!.Value, (200.0 / 1100.0) - RatioTolerance, (200.0 / 1100.0) + RatioTolerance);
    }

    [Fact]
    public void StartTwo_DropsTheFirstTwoPoints_ButKeepsInitialCapitalAsE0()
    {
        // E = [1000, 900, 1200, 1000, 1300] (points 1000 and 1100 excluded) ; H = [1000, 1000, 1200, 1200, 1300]
        // worst drawdown: 1200 -> 1000 = 200 / 1200 (the 1100 -> 900 fall is no longer inside the sample).
        BacktestReport r = Report(2);

        Assert.Equal(300m, r.TotalPnL.Value);
        Assert.Equal(200m, r.MaxDrawdownAmount.Value);
        Assert.InRange((double)r.MaxDrawdown.Value!.Value, (200.0 / 1200.0) - RatioTolerance, (200.0 / 1200.0) + RatioTolerance);
    }

    [Fact]
    public void StartOnTheLastPoint_KeepsExactlyOneReturn_TotalPnLValid_SharpeNeedsTwo()
    {
        BacktestReport r = Report(PointEquity.Length - 1);

        Assert.Equal(MetricStatus.Valid, r.TotalPnL.Status);
        Assert.Equal(300m, r.TotalPnL.Value);                       // the bar AT the index is included: E = [1000, 1300]
        Assert.Equal(MetricStatus.InsufficientData, r.BarSharpe.Status);
        Assert.Equal(MetricReason.SampleTooSmall, r.BarSharpe.Reason);
    }

    [Theory]
    [InlineData(6)]   // == number of points
    [InlineData(7)]   // beyond the data
    public void StartAtOrBeyondTheDataEnd_NoSample_TotalPnLInsufficientData(int historyStartIndex)
    {
        BacktestReport r = Report(historyStartIndex);

        Assert.Equal(MetricStatus.InsufficientData, r.TotalPnL.Status);
        Assert.Equal(MetricReason.EmptyInput, r.TotalPnL.Reason);
    }
}
