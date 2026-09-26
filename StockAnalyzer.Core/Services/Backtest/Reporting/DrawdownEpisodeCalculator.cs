using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

internal static class DrawdownEpisodeCalculator
{
    public static (DrawdownEpisodeResult Ratio, DrawdownEpisodeResult Amount) Compute(
        BacktestResult result,
        BacktestReportOptions options,
        CancellationToken cancellationToken)
    {
        EquitySample sample;
        ImmutableArray<DateTime> timestamps;
        try
        {
            sample = EquitySample.Build(result, options, cancellationToken);
            timestamps = BuildTimestamps(result, options, sample.SampleCount);
        }
        catch (OverflowException)
        {
            return Unavailable(DrawdownReasonCodes.ArithmeticOverflow);
        }
        catch (ArithmeticException)
        {
            return Unavailable(DrawdownReasonCodes.NonFiniteResult);
        }
        catch (ArgumentException)
        {
            return Unavailable(DrawdownReasonCodes.InvalidInput);
        }

        // The whole selected timeline is validated before any calculation, including the zero-drawdown return.
        if (!IsValidTimeline(timestamps, cancellationToken)) return Unavailable(DrawdownReasonCodes.InvalidInput);

        // Ratio and amount are independent dependency graphs: one arithmetic failure must not hide the other episode.
        return (
            Isolate(() => ComputeRatio(sample.Equity, timestamps, DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(sample.Equity, cancellationToken), cancellationToken)),
            Isolate(() => ComputeAmount(sample.Equity, timestamps, DrawdownSeriesCalculator.ComputeDrawdownAmountSeries(sample.Equity, cancellationToken), cancellationToken)));
    }

    private static DrawdownEpisodeResult Isolate(Func<DrawdownEpisodeResult> calculation)
    {
        try
        {
            return calculation();
        }
        catch (OverflowException)
        {
            return new DrawdownEpisodeResult(DrawdownEpisodeStatus.Unavailable, DrawdownReasonCodes.ArithmeticOverflow, null);
        }
        catch (ArithmeticException)
        {
            // DivideByZeroException is unreachable here: the drawdown ratio divides by the running peak, which is > 0
            // because InitialCapital > 0 is guaranteed by BacktestConfiguration.
            return new DrawdownEpisodeResult(DrawdownEpisodeStatus.Unavailable, DrawdownReasonCodes.NonFiniteResult, null);
        }
        catch (ArgumentException)
        {
            return new DrawdownEpisodeResult(DrawdownEpisodeStatus.Unavailable, DrawdownReasonCodes.InvalidInput, null);
        }
    }

    /// <summary>
    /// UTC only; the initial-capital time may equal the first observed bar (T0 == T1), but observed bars are
    /// strictly ascending. Timestamps are never sorted, repaired or interpolated.
    /// </summary>
    private static bool IsValidTimeline(ImmutableArray<DateTime> timestamps, CancellationToken cancellationToken)
    {
        for (int i = 0; i < timestamps.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (timestamps[i].Kind != DateTimeKind.Utc) return false;
            if (i == 0) continue;
            if (i == 1 ? timestamps[1] < timestamps[0] : timestamps[i] <= timestamps[i - 1]) return false;
        }
        return true;
    }

    private static ImmutableArray<DateTime> BuildTimestamps(BacktestResult result, BacktestReportOptions options, int sampleCount)
    {
        var timestamps = ImmutableArray.CreateBuilder<DateTime>(sampleCount + 1);
        timestamps.Add(options.EvaluationStartUtc);
        for (int i = 0; i < sampleCount; i++)
        {
            timestamps.Add(result.EquityPoints[options.HistoryStartIndex + i].Timestamp);
        }
        return timestamps.MoveToImmutable();
    }

    private static DrawdownEpisodeResult ComputeRatio(
        ImmutableArray<decimal> equity,
        ImmutableArray<DateTime> timestamps,
        ImmutableArray<double> series,
        CancellationToken cancellationToken)
    {
        double maximum = 0d;
        int peak = 0;
        int latestPeak = 0;
        int trough = 0;
        for (int i = 0; i < series.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (equity[i] >= equity[latestPeak]) latestPeak = i;
            if (series[i] > maximum)
            {
                maximum = series[i];
                peak = latestPeak;
                trough = i;
            }
        }
        if (maximum == 0d) return NoDrawdown();
        MetricValue converted = MetricCalculation.FromDouble(maximum, MetricUnit.DrawdownRatio);
        if (converted.Status != MetricStatus.Valid) return new DrawdownEpisodeResult(DrawdownEpisodeStatus.Unavailable, converted.Reason.ToString(), null);
        return Build(equity, timestamps, peak, trough, converted.Value!.Value, MetricUnit.DrawdownRatio, cancellationToken);
    }

    private static DrawdownEpisodeResult ComputeAmount(
        ImmutableArray<decimal> equity,
        ImmutableArray<DateTime> timestamps,
        ImmutableArray<decimal> series,
        CancellationToken cancellationToken)
    {
        decimal maximum = 0m;
        int peak = 0;
        int latestPeak = 0;
        int trough = 0;
        for (int i = 0; i < series.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (equity[i] >= equity[latestPeak]) latestPeak = i;
            if (series[i] > maximum)
            {
                maximum = series[i];
                peak = latestPeak;
                trough = i;
            }
        }
        if (maximum == 0m) return NoDrawdown();
        return Build(equity, timestamps, peak, trough, maximum, MetricUnit.Currency, cancellationToken);
    }

    private static DrawdownEpisodeResult Build(
        ImmutableArray<decimal> equity,
        ImmutableArray<DateTime> timestamps,
        int peak,
        int trough,
        decimal depth,
        MetricUnit unit,
        CancellationToken cancellationToken)
    {
        if (timestamps.Length != equity.Length) return new DrawdownEpisodeResult(DrawdownEpisodeStatus.Unavailable, DrawdownReasonCodes.InvalidInput, null);

        int? recovery = null;
        for (int i = trough + 1; i < equity.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (equity[i] >= equity[peak])
            {
                recovery = i;
                break;
            }
        }

        DrawdownDuration decline = Duration(timestamps[peak], timestamps[trough]);
        DrawdownDuration recoveryDuration = recovery is { } recoveryIndex
            ? Duration(timestamps[trough], timestamps[recoveryIndex])
            : new DrawdownDuration(null, null);
        DrawdownDuration underwater = recovery is { } underwaterEnd
            ? Duration(timestamps[peak], timestamps[underwaterEnd])
            : new DrawdownDuration(null, null);

        return new DrawdownEpisodeResult(
            DrawdownEpisodeStatus.Available,
            null,
            new DrawdownEpisode(
                peak,
                trough,
                recovery,
                timestamps[peak],
                timestamps[trough],
                recovery is { } r ? timestamps[r] : null,
                equity[peak],
                equity[trough],
                depth,
                unit,
                decline,
                recoveryDuration,
                underwater));
    }

    private static DrawdownDuration Duration(DateTime start, DateTime end)
        => end < start
            ? new DrawdownDuration(null, DrawdownReasonCodes.NonMonotonicTimestamp)
            : new DrawdownDuration(end - start, null);

    private static DrawdownEpisodeResult NoDrawdown() =>
        new(DrawdownEpisodeStatus.NoDrawdown, DrawdownReasonCodes.NoDrawdown, null);

    private static (DrawdownEpisodeResult Ratio, DrawdownEpisodeResult Amount) Unavailable(string reason)
    {
        var result = new DrawdownEpisodeResult(DrawdownEpisodeStatus.Unavailable, reason, null);
        return (result, result);
    }
}
