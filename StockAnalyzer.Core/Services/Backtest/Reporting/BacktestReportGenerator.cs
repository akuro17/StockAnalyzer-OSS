using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Builds and validates the complete 18-slot report while isolating arithmetic dependency graphs.</summary>
public sealed class BacktestReportGenerator : IBacktestReportGenerator, ICancellableBacktestReportGenerator, IQualifiedBacktestReportGenerator
{
    private readonly ILogger<BacktestReportGenerator> _logger;

    public BacktestReportGenerator(ILogger<BacktestReportGenerator>? logger = null)
    {
        _logger = logger ?? NullLogger<BacktestReportGenerator>.Instance;
    }

    public BacktestReport Generate(BacktestResult result, BacktestReportOptions options) =>
        Generate(result, options, CancellationToken.None);

    public BacktestReport Generate(
        BacktestResult result,
        BacktestReportOptions options,
        CancellationToken cancellationToken)
    {
        BacktestReport report = GenerateCore(result, options, null, cancellationToken).Report;
        _logger.LogDebug("Backtest report generated (legacy entry): Trades={TradeCount}, Frame={Frame}", report.TotalTrades, report.Frame);
        return report;
    }

    public QualifiedBacktestReportResult GenerateQualified(
        BacktestResult result,
        BacktestReportOptions options,
        SamplingQualification sampling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampling);
        if (options?.RunFingerprintBuilder is null)
        {
            throw new ArgumentException("Qualified generation requires a frozen run fingerprint builder.", nameof(options));
        }
        GenerationResult generated = GenerateCore(result, options, sampling, cancellationToken);
        _logger.LogDebug(
            "Backtest report generated (qualified): Trades={TradeCount}, Frame={Frame}, Sampling={SamplingStatus}",
            generated.Report.TotalTrades,
            generated.Report.Frame,
            sampling.Status);
        return new QualifiedBacktestReportResult(
            generated.Report,
            generated.RunFingerprint!,
            generated.BootstrapDiagnostics);
    }

    private static GenerationResult GenerateCore(
        BacktestResult result,
        BacktestReportOptions options,
        SamplingQualification? sampling,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateForGeneration();
        cancellationToken.ThrowIfCancellationRequested();

        EquitySample sample = EquitySample.Build(result, options, cancellationToken);
        ImmutableArray<BacktestTrade> trades = result.Trades;

        bool ratioSeriesValid = TryComputeSeries(
            () => DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(sample.Equity, cancellationToken),
            out ImmutableArray<double> ddRatioSeries,
            out MetricReason ratioSeriesFailure);
        bool amountSeriesValid = TryComputeSeries(
            () => DrawdownSeriesCalculator.ComputeDrawdownAmountSeries(sample.Equity, cancellationToken),
            out ImmutableArray<decimal> ddAmountSeries,
            out MetricReason amountSeriesFailure);

        cancellationToken.ThrowIfCancellationRequested();
        MetricValue totalPnL = MetricCalculation.Run(
            MetricUnit.Currency,
            () => BasicMetricsCalculator.ComputeTotalPnL(sample.Equity));
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue totalReturn = MetricCalculation.Run(
            MetricUnit.ReturnRatio,
            () => BasicMetricsCalculator.ComputeTotalReturn(sample.Equity));
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue cagr = MetricCalculation.Run(
            MetricUnit.ReturnRatio,
            () => sampling is null
                ? BasicMetricsCalculator.ComputeCagr(sample.Equity, options, cancellationToken)
                : BasicMetricsCalculator.ComputeQualifiedCagr(
                    sample.Equity, options, sampling.Status == SamplingStatus.Verified, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue winRate = MetricCalculation.Run(
            MetricUnit.WinRateRatio,
            () => BasicMetricsCalculator.ComputeWinRate(trades, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue profitFactor = MetricCalculation.Run(
            MetricUnit.Dimensionless,
            () => BasicMetricsCalculator.ComputeProfitFactor(trades, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue expectedPayoff = MetricCalculation.Run(
            MetricUnit.Currency,
            () => BasicMetricsCalculator.ComputeExpectedPayoff(trades, cancellationToken));

        MetricValue maxDrawdown = ratioSeriesValid
            ? MetricCalculation.Run(MetricUnit.DrawdownRatio, () => DrawdownSeriesCalculator.ComputeMaxDrawdown(ddRatioSeries, cancellationToken))
            : MetricCalculation.Failure(MetricUnit.DrawdownRatio, ratioSeriesFailure);
        MetricValue ulcerIndex = ratioSeriesValid
            ? MetricCalculation.Run(MetricUnit.PercentPoints, () => DrawdownSeriesCalculator.ComputeUlcerIndex(ddRatioSeries, sample.SampleCount, cancellationToken))
            : MetricCalculation.Failure(MetricUnit.PercentPoints, ratioSeriesFailure);
        MetricValue maxDrawdownAmount = amountSeriesValid
            ? MetricCalculation.Run(MetricUnit.Currency, () => DrawdownSeriesCalculator.ComputeMaxDrawdownAmount(ddAmountSeries, cancellationToken))
            : MetricCalculation.Failure(MetricUnit.Currency, amountSeriesFailure);

        decimal rf = options.AnnualRiskFreeRate / options.AnnualPeriods;
        decimal mar = options.AnnualMAR / options.AnnualPeriods;

        cancellationToken.ThrowIfCancellationRequested();
        MetricValue barSharpe = RiskAdjustedMetricsCalculator.ComputeBarSharpe(sample, rf, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue annualizedSharpe = sampling is null || sampling.Status == SamplingStatus.Verified
            ? RiskAdjustedMetricsCalculator.ComputeAnnualizedSharpe(barSharpe, options.AnnualPeriods)
            : ApplySamplingGate(barSharpe, sampling.Status);
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue annualizedSharpeAutocorrelationAdjusted =
            sampling is null
                ? RiskAdjustedMetricsCalculator.ComputeAnnualizedSharpeAutocorrelationAdjusted(sample, rf, options.AnnualPeriods, cancellationToken)
                : RiskAdjustedMetricsCalculator.ComputeQualifiedAnnualizedSharpeAutocorrelationAdjusted(
                    sample, rf, options.AnnualPeriods, sampling.Status, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue barSortino = RiskAdjustedMetricsCalculator.ComputeBarSortino(sample, mar, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue annualizedSortino = sampling is null || sampling.Status == SamplingStatus.Verified
            ? RiskAdjustedMetricsCalculator.ComputeAnnualizedSortino(barSortino, options.AnnualPeriods)
            : ApplySamplingGate(barSortino, sampling.Status);
        cancellationToken.ThrowIfCancellationRequested();
        MetricValue annualizedSortinoAutocorrelationAdjusted;
        ConfidenceInterval? adjustedSortinoInterval;
        BootstrapDiagnostics bootstrapDiagnostics;
        if (sampling is null)
        {
            (annualizedSortinoAutocorrelationAdjusted, adjustedSortinoInterval) =
                RiskAdjustedMetricsCalculator.ComputeAnnualizedSortinoAutocorrelationAdjusted(
                    sample,
                    mar,
                    options.AnnualPeriods,
                    options.BootstrapSeed,
                    options.BootstrapIterations,
                    cancellationToken);
            bootstrapDiagnostics = BootstrapDiagnostics.NotComputed(BootstrapDiagnostics.LegacyEntryPointReason);
        }
        else
        {
            BootstrapMetricComputation computation =
                RiskAdjustedMetricsCalculator.ComputeQualifiedAnnualizedSortinoAutocorrelationAdjusted(
                    sample,
                    mar,
                    options.AnnualPeriods,
                    options.BootstrapSeed,
                    options.BootstrapIterations,
                    sampling.Status,
                    cancellationToken);
            annualizedSortinoAutocorrelationAdjusted = computation.Point;
            adjustedSortinoInterval = computation.Interval;
            bootstrapDiagnostics = computation.Diagnostics;
        }
        cancellationToken.ThrowIfCancellationRequested();

        MetricValue calmarFullPeriod = RiskAdjustedMetricsCalculator.ComputeCalmarFullPeriod(cagr, maxDrawdown);
        MetricValue sqn = RiskAdjustedMetricsCalculator.ComputeSqn(trades);
        MetricValue recoveryFactor = RiskAdjustedMetricsCalculator.ComputeRecoveryFactor(totalPnL, maxDrawdownAmount);

        int winTrades = 0;
        int lossTrades = 0;
        int breakevenTrades = 0;
        for (int i = 0; i < trades.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (trades[i].ClosedNet > 0m) winTrades++;
            else if (trades[i].ClosedNet < 0m) lossTrades++;
            else breakevenTrades++;
        }

        BacktestRunFingerprint? sealedFingerprint = options.RunFingerprintBuilder?.Seal(result);
        var report = new BacktestReport
        {
            TotalPnL = totalPnL,
            MaxDrawdownAmount = maxDrawdownAmount,
            ExpectedPayoff = expectedPayoff,
            TotalReturn = totalReturn,
            CAGR = cagr,
            WinRate = winRate,
            ProfitFactor = profitFactor,
            MaxDrawdown = maxDrawdown,
            UlcerIndex = ulcerIndex,
            BarSharpe = barSharpe,
            AnnualizedSharpe = annualizedSharpe,
            AnnualizedSharpeAutocorrelationAdjusted = annualizedSharpeAutocorrelationAdjusted,
            BarSortino = barSortino,
            AnnualizedSortino = annualizedSortino,
            AnnualizedSortinoAutocorrelationAdjusted = annualizedSortinoAutocorrelationAdjusted,
            AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval = adjustedSortinoInterval,
            CalmarFullPeriod = calmarFullPeriod,
            SQN = sqn,
            RecoveryFactor = recoveryFactor,
            TotalTrades = trades.Length,
            WinTrades = winTrades,
            LossTrades = lossTrades,
            BreakevenTrades = breakevenTrades,
            SqnWarning = RiskAdjustedMetricsCalculator.ComputeSqnWarning(trades.Length),
            FormulaVersion = BacktestReport.CurrentFormulaVersion,
            AnnualPeriods = options.AnnualPeriods,
            AnnualRiskFreeRate = options.AnnualRiskFreeRate,
            AnnualMAR = options.AnnualMAR,
            Frame = options.Frame,
            ExecutionModel = result.Configuration.ExecutionModel == ExecutionModel.YFinanceApproximate
                ? ExecutionModel.YFinanceApproximate
                : null,
            ExecutionDisclosure = result.Configuration.ExecutionModel == ExecutionModel.YFinanceApproximate
                ? BacktestReport.YFinanceApproximateDisclosure
                : null,
            RunFingerprint = sealedFingerprint is not null
                ? BacktestReportRunFingerprint.From(sealedFingerprint)
                : null,
        };

        cancellationToken.ThrowIfCancellationRequested();
        BacktestReportValidator.Validate(report);
        cancellationToken.ThrowIfCancellationRequested();
        return new GenerationResult(report, sealedFingerprint, bootstrapDiagnostics);
    }

    private static MetricValue ApplySamplingGate(MetricValue prerequisite, SamplingStatus status)
    {
        if (prerequisite.Status != MetricStatus.Valid)
        {
            return MetricCalculation.Propagate(prerequisite, MetricUnit.Dimensionless);
        }
        return MetricValue.NonValid(
            MetricStatus.NotApplicable,
            MetricUnit.Dimensionless,
            status == SamplingStatus.Rejected ? MetricReason.SamplingRejected : MetricReason.SamplingUnverified);
    }

    private sealed record GenerationResult(
        BacktestReport Report,
        BacktestRunFingerprint? RunFingerprint,
        BootstrapDiagnostics BootstrapDiagnostics);

    private static bool TryComputeSeries<T>(
        Func<ImmutableArray<T>> calculation,
        out ImmutableArray<T> series,
        out MetricReason failureReason)
    {
        try
        {
            series = calculation();
            failureReason = MetricReason.None;
            return true;
        }
        catch (OverflowException)
        {
            series = ImmutableArray<T>.Empty;
            failureReason = MetricReason.ArithmeticOverflow;
            return false;
        }
        catch (DivideByZeroException)
        {
            series = ImmutableArray<T>.Empty;
            failureReason = MetricReason.UnexpectedZeroDivisor;
            return false;
        }
    }
}
