using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

internal sealed record BootstrapMetricComputation(
    MetricValue Point,
    ConfidenceInterval? Interval,
    BootstrapDiagnostics Diagnostics);

/// <summary>Approved Sharpe, Sortino, HAC, bootstrap, and derived report metrics.</summary>
internal static class RiskAdjustedMetricsCalculator
{
    internal static (double mean, double sampleVariance) MeanAndSampleVariance(ImmutableArray<double> x) =>
        MeanAndSampleVariance(x.AsSpan(), CancellationToken.None);

    private static (double mean, double sampleVariance) MeanAndSampleVariance(
        ReadOnlySpan<double> x,
        CancellationToken cancellationToken)
    {
        double mean = 0d;
        for (int i = 0; i < x.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            mean += x[i];
        }
        mean /= x.Length;

        double sumSq = 0d;
        for (int i = 0; i < x.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            double d = x[i] - mean;
            sumSq += d * d;
        }
        return (mean, sumSq / (x.Length - 1));
    }

    internal static (double mean, double downsideVariance) MeanAndDownsideVariance(ImmutableArray<double> y) =>
        MeanAndDownsideVariance(y.AsSpan(), CancellationToken.None);

    private static (double mean, double downsideVariance) MeanAndDownsideVariance(
        ReadOnlySpan<double> y,
        CancellationToken cancellationToken)
    {
        double mean = 0d;
        double downsideSumSq = 0d;
        for (int i = 0; i < y.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            double value = y[i];
            mean += value;
            double downside = Math.Min(value, 0d);
            downsideSumSq += downside * downside;
        }
        mean /= y.Length;
        return (mean, downsideSumSq / y.Length);
    }

    public static MetricValue ComputeBarSharpe(
        EquitySample sample,
        decimal rf,
        CancellationToken cancellationToken = default)
    {
        if (TryReturnPrecondition(sample, out MetricValue failure)) return failure;
        int m = sample.SampleCount;
        if (m < 2) return TooSmall();

        double rfDouble = (double)rf;
        var x = new double[m];
        for (int i = 0; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            x[i] = sample.Returns[i] - rfDouble;
        }
        (double mean, double variance) = MeanAndSampleVariance(x, cancellationToken);
        if (!double.IsFinite(mean) || !double.IsFinite(variance)) return NonFinite();
        double s = Math.Sqrt(variance);
        if (s == 0d) return ZeroDivisor();
        return MetricCalculation.FromDouble(mean / s, MetricUnit.Dimensionless);
    }

    public static MetricValue ComputeAnnualizedSharpe(MetricValue barSharpe, int annualPeriods)
    {
        if (barSharpe.Status != MetricStatus.Valid)
        {
            return MetricCalculation.Propagate(barSharpe, MetricUnit.Dimensionless);
        }
        return MetricCalculation.Run(
            MetricUnit.Dimensionless,
            () => MetricValue.Valid(barSharpe.Value!.Value * (decimal)Math.Sqrt(annualPeriods), MetricUnit.Dimensionless));
    }

    internal static int NeweyWestBandwidth(int m)
    {
        int raw = (int)Math.Floor(4.0 * Math.Pow(m / 100.0, 2.0 / 9.0));
        return Math.Clamp(raw, 0, Math.Max(0, m - 1));
    }

    internal static double NeweyWestLongRunVariance(
        ImmutableArray<double> x,
        double mean,
        int bandwidth) =>
        NeweyWestLongRunVariance(x.AsSpan(), mean, bandwidth, CancellationToken.None);

    private static double NeweyWestLongRunVariance(
        ReadOnlySpan<double> x,
        double mean,
        int bandwidth,
        CancellationToken cancellationToken)
    {
        int m = x.Length;
        double gamma0 = 0d;
        for (int i = 0; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            double d = x[i] - mean;
            gamma0 += d * d;
        }
        gamma0 /= m - 1;

        double longRunVariance = gamma0;
        for (int k = 1; k <= bandwidth; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double gammaK = 0d;
            for (int t = k; t < m; t++)
            {
                MetricCalculation.CheckCancellation(cancellationToken, t);
                gammaK += (x[t] - mean) * (x[t - k] - mean);
            }
            gammaK /= m - 1;
            double weight = 1.0 - (double)k / (bandwidth + 1);
            longRunVariance += 2.0 * weight * gammaK;
        }
        return longRunVariance;
    }

    public static MetricValue ComputeAnnualizedSharpeAutocorrelationAdjusted(
        EquitySample sample,
        decimal rf,
        int annualPeriods,
        CancellationToken cancellationToken = default)
        => ComputeAnnualizedSharpeAutocorrelationAdjustedCore(
            sample, rf, annualPeriods, null, cancellationToken);

    public static MetricValue ComputeQualifiedAnnualizedSharpeAutocorrelationAdjusted(
        EquitySample sample,
        decimal rf,
        int annualPeriods,
        SamplingStatus samplingStatus,
        CancellationToken cancellationToken = default)
        => ComputeAnnualizedSharpeAutocorrelationAdjustedCore(
            sample, rf, annualPeriods, samplingStatus, cancellationToken);

    private static MetricValue ComputeAnnualizedSharpeAutocorrelationAdjustedCore(
        EquitySample sample,
        decimal rf,
        int annualPeriods,
        SamplingStatus? samplingStatus,
        CancellationToken cancellationToken)
    {
        if (TryReturnPrecondition(sample, out MetricValue failure)) return failure;
        int m = sample.SampleCount;
        if (m < 2) return TooSmall();

        double rfDouble = (double)rf;
        var x = new double[m];
        double mean = 0d;
        for (int i = 0; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            x[i] = sample.Returns[i] - rfDouble;
            mean += x[i];
        }
        mean /= m;
        if (!double.IsFinite(mean)) return NonFinite();

        double longRunVariance = NeweyWestLongRunVariance(
            x,
            mean,
            NeweyWestBandwidth(m),
            cancellationToken);
        if (!double.IsFinite(longRunVariance)) return NonFinite();
        if (longRunVariance <= 0d) return ZeroDivisor();
        if (samplingStatus is { } status && status != SamplingStatus.Verified)
        {
            return SamplingFailure(status);
        }
        return MetricCalculation.FromDouble(
            mean * Math.Sqrt(annualPeriods) / Math.Sqrt(longRunVariance),
            MetricUnit.Dimensionless);
    }

    public static MetricValue ComputeBarSortino(
        EquitySample sample,
        decimal mar,
        CancellationToken cancellationToken = default)
    {
        if (TryReturnPrecondition(sample, out MetricValue failure)) return failure;
        int m = sample.SampleCount;
        if (m < 2) return TooSmall();

        double marDouble = (double)mar;
        var y = new double[m];
        for (int i = 0; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            y[i] = sample.Returns[i] - marDouble;
        }
        (double mean, double downsideVariance) = MeanAndDownsideVariance(y, cancellationToken);
        if (!double.IsFinite(mean) || !double.IsFinite(downsideVariance)) return NonFinite();
        if (downsideVariance == 0d) return DownsideZero();
        return MetricCalculation.FromDouble(mean / Math.Sqrt(downsideVariance), MetricUnit.Dimensionless);
    }

    public static MetricValue ComputeAnnualizedSortino(MetricValue barSortino, int annualPeriods)
    {
        if (barSortino.Status != MetricStatus.Valid)
        {
            return MetricCalculation.Propagate(barSortino, MetricUnit.Dimensionless);
        }
        return MetricCalculation.Run(
            MetricUnit.Dimensionless,
            () => MetricValue.Valid(barSortino.Value!.Value * (decimal)Math.Sqrt(annualPeriods), MetricUnit.Dimensionless));
    }

    public static (MetricValue Point, ConfidenceInterval? Interval) ComputeAnnualizedSortinoAutocorrelationAdjusted(
        EquitySample sample,
        decimal mar,
        int annualPeriods,
        int bootstrapSeed,
        int bootstrapIterations,
        CancellationToken cancellationToken = default)
    {
        BootstrapMetricComputation computation = ComputeAnnualizedSortinoAutocorrelationAdjustedCore(
            sample,
            mar,
            annualPeriods,
            bootstrapSeed,
            bootstrapIterations,
            null,
            cancellationToken);
        return (computation.Point, computation.Interval);
    }

    public static BootstrapMetricComputation ComputeQualifiedAnnualizedSortinoAutocorrelationAdjusted(
        EquitySample sample,
        decimal mar,
        int annualPeriods,
        int bootstrapSeed,
        int bootstrapIterations,
        SamplingStatus samplingStatus,
        CancellationToken cancellationToken = default)
        => ComputeAnnualizedSortinoAutocorrelationAdjustedCore(
            sample,
            mar,
            annualPeriods,
            bootstrapSeed,
            bootstrapIterations,
            samplingStatus,
            cancellationToken);

    private static BootstrapMetricComputation ComputeAnnualizedSortinoAutocorrelationAdjustedCore(
        EquitySample sample,
        decimal mar,
        int annualPeriods,
        int bootstrapSeed,
        int bootstrapIterations,
        SamplingStatus? samplingStatus,
        CancellationToken cancellationToken)
    {
        if (TryReturnPrecondition(sample, out MetricValue failure)) return FailedBootstrap(failure);
        int m = sample.SampleCount;
        if (m < 2) return FailedBootstrap(TooSmall());

        double marDouble = (double)mar;
        var y = new double[m];
        for (int i = 0; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            y[i] = sample.Returns[i] - marDouble;
            if (!double.IsFinite(y[i])) return FailedBootstrap(NonFinite());
        }

        (double mean, double downsideVariance) = MeanAndDownsideVariance(y, cancellationToken);
        if (!double.IsFinite(mean) || !double.IsFinite(downsideVariance)) return FailedBootstrap(NonFinite());
        if (downsideVariance == 0d) return FailedBootstrap(DownsideZero());
        if (m < 20) return FailedBootstrap(TooSmall());

        if (samplingStatus is { } status && status != SamplingStatus.Verified)
        {
            MetricValue gated = SamplingFailure(status);
            return new BootstrapMetricComputation(
                gated,
                null,
                BootstrapDiagnostics.NotComputed(gated.Reason.ToString()));
        }

        double thetaHat = mean / Math.Sqrt(downsideVariance) * Math.Sqrt(annualPeriods);
        if (!double.IsFinite(thetaHat)) return FailedBootstrap(NonFinite());

        bool constant = true;
        for (int i = 1; i < m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            constant &= y[i] == y[0];
        }
        if (constant)
        {
            double pointValue = -Math.Sqrt(annualPeriods);
            MetricValue point = MetricCalculation.FromDouble(pointValue, MetricUnit.Dimensionless);
            ConfidenceInterval? interval = point.Status == MetricStatus.Valid
                ? new ConfidenceInterval(point.Value!.Value, point.Value.Value)
                : null;
            return new BootstrapMetricComputation(
                point,
                interval,
                new BootstrapDiagnostics(BootstrapDiagnostics.CurrentMethodVersion, 1, bootstrapIterations, 0, true,
                    point.Status == MetricStatus.Valid ? null : point.Reason.ToString()));
        }

        if (!PolitisWhiteBlockBootstrap.TryOptimalCircularBlockLength(
                y,
                cancellationToken,
                out int blockLength,
                out MetricReason blockFailure))
        {
            return FailedBootstrap(MetricCalculation.Failure(MetricUnit.Dimensionless, blockFailure));
        }

        var rng = new Random(bootstrapSeed);
        var resampled = new double[m];
        var replicates = new List<double>(bootstrapIterations);
        for (int b = 0; b < bootstrapIterations; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PolitisWhiteBlockBootstrap.CircularBlockResample(y, blockLength, rng, resampled, cancellationToken);
            (double rMean, double rDownsideVariance) = MeanAndDownsideVariance(resampled, cancellationToken);
            if (!double.IsFinite(rMean) || !double.IsFinite(rDownsideVariance)) return FailedBootstrap(NonFinite(), blockLength, replicates.Count, b + 1);
            if (rDownsideVariance == 0d) continue;
            double replicate = rMean / Math.Sqrt(rDownsideVariance) * Math.Sqrt(annualPeriods);
            if (!double.IsFinite(replicate)) return FailedBootstrap(NonFinite(), blockLength, replicates.Count, b + 1);
            replicates.Add(replicate);
        }

        if (HasInsufficientValidReplicates(replicates.Count, bootstrapIterations))
        {
            return FailedBootstrap(DownsideZero(), blockLength, replicates.Count, bootstrapIterations);
        }

        double bootstrapMean = 0d;
        for (int i = 0; i < replicates.Count; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            bootstrapMean += replicates[i];
        }
        bootstrapMean /= replicates.Count;
        double adjusted = 2.0 * thetaHat - bootstrapMean;
        if (!double.IsFinite(adjusted)) return FailedBootstrap(NonFinite(), blockLength, replicates.Count, bootstrapIterations);

        cancellationToken.ThrowIfCancellationRequested();
        replicates.Sort();
        cancellationToken.ThrowIfCancellationRequested();
        double lower = Percentile(replicates, 0.025);
        double upper = Percentile(replicates, 0.975);
        MetricValue adjustedMetric = MetricCalculation.FromDouble(adjusted, MetricUnit.Dimensionless);
        MetricValue lowerMetric = MetricCalculation.FromDouble(lower, MetricUnit.Dimensionless);
        MetricValue upperMetric = MetricCalculation.FromDouble(upper, MetricUnit.Dimensionless);
        if (adjustedMetric.Status != MetricStatus.Valid) return FailedBootstrap(adjustedMetric, blockLength, replicates.Count, bootstrapIterations);
        if (lowerMetric.Status != MetricStatus.Valid || upperMetric.Status != MetricStatus.Valid)
        {
            return FailedBootstrap(NonFinite(), blockLength, replicates.Count, bootstrapIterations);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new BootstrapMetricComputation(
            adjustedMetric,
            new ConfidenceInterval(lowerMetric.Value!.Value, upperMetric.Value!.Value),
            new BootstrapDiagnostics(BootstrapDiagnostics.CurrentMethodVersion, blockLength, replicates.Count, bootstrapIterations, false, null));
    }

    internal static bool HasInsufficientValidReplicates(int validReplicateCount, int bootstrapIterations) =>
        (long)validReplicateCount * 2L < bootstrapIterations;

    private static double Percentile(List<double> sorted, double p)
    {
        int n = sorted.Count;
        double index = p * (n - 1);
        int lowerIdx = (int)Math.Floor(index);
        int upperIdx = (int)Math.Ceiling(index);
        if (lowerIdx == upperIdx) return sorted[lowerIdx];
        double frac = index - lowerIdx;
        return sorted[lowerIdx] + frac * (sorted[upperIdx] - sorted[lowerIdx]);
    }

    public static MetricValue ComputeCalmarFullPeriod(MetricValue cagr, MetricValue maxDrawdown)
    {
        if (cagr.Status != MetricStatus.Valid) return MetricCalculation.Propagate(cagr, MetricUnit.Dimensionless);
        if (maxDrawdown.Status != MetricStatus.Valid) return MetricCalculation.Propagate(maxDrawdown, MetricUnit.Dimensionless);
        if (maxDrawdown.Value!.Value == 0m) return ZeroDivisor();
        return MetricCalculation.Run(
            MetricUnit.Dimensionless,
            () => MetricValue.Valid(cagr.Value!.Value / maxDrawdown.Value.Value, MetricUnit.Dimensionless));
    }

    public static MetricValue ComputeSqn(ImmutableArray<BacktestTrade> trades) =>
        MetricValue.NonValid(MetricStatus.NotApplicable, MetricUnit.Dimensionless, MetricReason.RiskDataMissing);

    public static bool ComputeSqnWarning(int totalTrades) => totalTrades < 30;

    public static MetricValue ComputeRecoveryFactor(MetricValue totalPnL, MetricValue maxDrawdownAmount)
    {
        if (totalPnL.Status != MetricStatus.Valid) return MetricCalculation.Propagate(totalPnL, MetricUnit.Dimensionless);
        if (maxDrawdownAmount.Status != MetricStatus.Valid) return MetricCalculation.Propagate(maxDrawdownAmount, MetricUnit.Dimensionless);
        if (maxDrawdownAmount.Value!.Value == 0m) return ZeroDivisor();
        return MetricCalculation.Run(
            MetricUnit.Dimensionless,
            () => MetricValue.Valid(totalPnL.Value!.Value / maxDrawdownAmount.Value.Value, MetricUnit.Dimensionless));
    }

    private static bool TryReturnPrecondition(EquitySample sample, out MetricValue failure)
    {
        if (sample.HasNegativeEquityPeriod)
        {
            failure = MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.NegativeEquityInPeriod);
            return true;
        }
        if (sample.ReturnFailureReason is { } reason)
        {
            failure = MetricCalculation.Failure(MetricUnit.Dimensionless, reason);
            return true;
        }
        failure = default;
        return false;
    }

    private static MetricValue TooSmall() =>
        MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Dimensionless, MetricReason.SampleTooSmall);

    private static MetricValue SamplingFailure(SamplingStatus status) =>
        MetricValue.NonValid(
            MetricStatus.NotApplicable,
            MetricUnit.Dimensionless,
            status == SamplingStatus.Rejected ? MetricReason.SamplingRejected : MetricReason.SamplingUnverified);

    private static BootstrapMetricComputation FailedBootstrap(
        MetricValue metric,
        int? blockLength = null,
        int? validReplicates = null,
        int executedReplicates = 0) =>
        new(metric, null, new BootstrapDiagnostics(
            BootstrapDiagnostics.CurrentMethodVersion,
            blockLength,
            validReplicates,
            executedReplicates,
            false,
            metric.Reason.ToString()));

    private static MetricValue ZeroDivisor() =>
        MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.ZeroDivisor);

    private static MetricValue DownsideZero() =>
        MetricValue.NonValid(MetricStatus.Undefined, MetricUnit.Dimensionless, MetricReason.DownsideZero);

    private static MetricValue NonFinite() =>
        MetricCalculation.Failure(MetricUnit.Dimensionless, MetricReason.NonFiniteResult);
}
