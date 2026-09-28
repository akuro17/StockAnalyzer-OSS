using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;
using System.Threading;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class BacktestReportGeneratorTests
{
    [Fact]
    public void Generate_OneTickDoubling_IsolatesCagrOverflow()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 200m });
        var options = new BacktestReportOptions(
            TimeFrame.D1,
            0,
            ReportTestHelpers.BaseUtc,
            ReportTestHelpers.BaseUtc.AddTicks(1))
        {
            AnnualPeriods = 252,
            BootstrapIterations = 1000,
        };

        BacktestReport report = new BacktestReportGenerator().Generate(result, options);

        Assert.Equal(100m, report.TotalPnL.Value);
        Assert.Equal(1m, report.TotalReturn.Value);
        Assert.Equal(MetricStatus.NumericFailure, report.CAGR.Status);
        Assert.Equal(MetricReason.NonFiniteResult, report.CAGR.Reason);
        Assert.Equal(MetricUnit.Dimensionless, report.CalmarFullPeriod.Unit);
        Assert.Equal(MetricReason.NonFiniteResult, report.CalmarFullPeriod.Reason);
    }

    [Fact]
    public void Generate_PreCancelled_ThrowsOperationCanceledException()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 90m, 80m });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ((ICancellableBacktestReportGenerator)new BacktestReportGenerator()).Generate(
                result,
                ReportTestHelpers.Options(),
                cancellation.Token));
    }

    [Fact]
    public void Generate_EmptySample_DerivedRecoveryKeepsDimensionlessUnit()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, Array.Empty<decimal>()),
            ReportTestHelpers.Options());

        Assert.NotEqual(MetricStatus.Valid, report.RecoveryFactor.Status);
        Assert.Equal(MetricUnit.Dimensionless, report.RecoveryFactor.Unit);
    }
    [Fact]
    public void AnnualPeriods_D1_252_W1_52_Independent()
    {
        // Non-constant, non-monotonic returns so BarSharpe is Valid (nonzero mean and variance).
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m });
        var d1Options = ReportTestHelpers.Options(annualPeriods: 252);
        var w1Options = new BacktestReportOptions(TimeFrame.W1, 0, ReportTestHelpers.BaseUtc, ReportTestHelpers.BaseUtc.AddDays(365))
        {
            AnnualPeriods = 52,
        };
        var generator = new BacktestReportGenerator();

        BacktestReport d1Report = generator.Generate(result, d1Options);
        BacktestReport w1Report = generator.Generate(result, w1Options);

        Assert.Equal(MetricStatus.Valid, d1Report.AnnualizedSharpe.Status);
        Assert.Equal(MetricStatus.Valid, w1Report.AnnualizedSharpe.Status);
        Assert.Equal(MetricUnit.Dimensionless, d1Report.AnnualizedSharpe.Unit);
        Assert.Equal(MetricUnit.Dimensionless, w1Report.AnnualizedSharpe.Unit);
        Assert.NotEqual(d1Report.AnnualizedSharpe.Value, w1Report.AnnualizedSharpe.Value);
        Assert.Equal(TimeFrame.D1, d1Report.Frame);
        Assert.Equal(TimeFrame.W1, w1Report.Frame);
        Assert.Equal(252, d1Report.AnnualPeriods);
        Assert.Equal(52, w1Report.AnnualPeriods);
    }

    [Fact]
    public void Generate_EndToEnd_MultipleMetricsAgreeInOneCall()
    {
        var trades = ImmutableArray.Create(
            ReportTestHelpers.Trade(100m),
            ReportTestHelpers.Trade(-50m),
            ReportTestHelpers.Trade(0m));
        var result = ReportTestHelpers.BuildResult(1000m, new[] { 1100m, 900m, 1200m }, trades.ToArray());
        var options = ReportTestHelpers.Options();
        var generator = new BacktestReportGenerator();

        BacktestReport report = generator.Generate(result, options);

        Assert.Equal(200m, report.TotalPnL.Value);
        Assert.Equal(0.2m, report.TotalReturn.Value);
        Assert.Equal(1m / 3m, report.WinRate.Value);
        Assert.Equal(2m, report.ProfitFactor.Value);
        Assert.Equal(50m / 3m, report.ExpectedPayoff.Value);
        Assert.Equal(200m, report.MaxDrawdownAmount.Value);
        Assert.Equal(MetricStatus.Valid, report.MaxDrawdown.Status);
        Assert.True(report.MaxDrawdown.Value > 0m);
        Assert.Equal(MetricStatus.NotApplicable, report.SQN.Status);
        Assert.Equal(MetricReason.RiskDataMissing, report.SQN.Reason);
        Assert.True(report.SqnWarning);
        Assert.Equal(3, report.TotalTrades);
        Assert.Equal(1, report.WinTrades);
        Assert.Equal(1, report.LossTrades);
        Assert.Equal(1, report.BreakevenTrades);
        Assert.Equal(1, report.FormulaVersion);
        Assert.Equal(TimeFrame.D1, report.Frame);
    }

    [Fact]
    public void Generate_PopulatesAnnualizedSharpeAutocorrelationAdjusted()
    {
        // Same non-constant, non-monotonic fixture as AnnualPeriods_D1_252_W1_52_Independent, so the
        // underlying excess-return sample is well-formed (m=3, nonzero mean and variance).
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m });
        var options = ReportTestHelpers.Options(annualPeriods: 252);
        var generator = new BacktestReportGenerator();

        BacktestReport report = generator.Generate(result, options);

        Assert.Equal(MetricStatus.Valid, report.AnnualizedSharpeAutocorrelationAdjusted.Status);
        Assert.Equal(MetricUnit.Dimensionless, report.AnnualizedSharpeAutocorrelationAdjusted.Unit);
    }

    [Fact]
    public void Generate_PopulatesAnnualizedSortinoAutocorrelationAdjusted()
    {
        // m=30 deterministic synthetic series, large enough to clear the bootstrap-specific m>=20 floor
        // (see RiskAdjustedMetricsCalculatorTests.BlockBootstrapFixtureEquity for provenance).
        decimal[] equity =
        {
            100.0000000000m, 101.6666538193m, 103.8501437261m, 105.0493633971m, 104.5398310335m,
            103.0187135220m, 102.0446417359m, 102.7932695971m, 105.2472900478m, 108.2360811430m,
            110.2105637506m, 110.4143741761m, 109.5731574672m, 109.3277495697m, 110.9318567054m,
            114.3791405326m, 118.4182289117m, 121.3709651233m, 122.4088762469m, 122.3254790670m,
            122.9346911537m, 125.6402936190m, 130.4489842244m, 135.9569387935m, 140.2565783197m,
            142.3964041744m, 143.2880461490m, 145.0367948342m, 149.2927779581m, 156.0806219362m,
        };
        var result = ReportTestHelpers.BuildResult(100m, equity);
        var options = ReportTestHelpers.Options(annualPeriods: 252);
        var generator = new BacktestReportGenerator();

        BacktestReport report = generator.Generate(result, options);

        Assert.Equal(MetricStatus.Valid, report.AnnualizedSortinoAutocorrelationAdjusted.Status);
        Assert.Equal(MetricUnit.Dimensionless, report.AnnualizedSortinoAutocorrelationAdjusted.Unit);
        Assert.True(report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval.HasValue);
        Assert.True(report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval!.Value.Lower
            <= report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval.Value.Upper);
    }

    [Fact]
    public void Generate_NullResult_Throws()
    {
        var generator = new BacktestReportGenerator();
        Assert.Throws<ArgumentNullException>(() => generator.Generate(null!, ReportTestHelpers.Options()));
    }

    [Fact]
    public void Generate_NullOptions_Throws()
    {
        var generator = new BacktestReportGenerator();
        var result = ReportTestHelpers.BuildResult(100m, new[] { 100m });
        Assert.Throws<ArgumentNullException>(() => generator.Generate(result, null!));
    }
}
