using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// H[j]/D[j] drawdown series and the two scalar drawdown metrics (spec §5.4). Clamping DD to [0,1] is
/// prohibited — short-side equity paths can legitimately exceed a 100% drawdown ratio (spec permits DD&gt;1
/// for short positions). The two scalar drawdown magnitudes need no peak/trough bookkeeping (a plain max() over
/// D[j]/(H[j]-E[j]) is tie-independent); the two duration metrics (<see cref="ComputeMaxDepthDrawdownDuration"/>,
/// <see cref="ComputeLongestDrawdownDuration"/>) do, and apply the spec §5.4 tie rules: first trough, latest equal peak, recovery at the
/// first later E &gt;= peak. Durations are in bars (index differences on E[0..m]); an episode still underwater at E[m] is counted up to
/// the last sample bar (right-censored lower bound), unlike the evaluation artifact's episode whose UnderwaterBars is null then.
/// </summary>
internal static class DrawdownSeriesCalculator
{
    /// <summary>
    /// A drawdown-duration metric and whether it is a right-censored lower bound (the episode behind it was still underwater at the last
    /// sample bar, so its true length is at least the value). <see cref="RightCensored"/> is meaningful only when <see cref="Value"/> is Valid.
    /// </summary>
    internal readonly record struct DrawdownDurationResult(MetricValue Value, bool RightCensored);

    /// <summary>The deepest point of a drawdown series: its <paramref name="Maximum"/> depth and the equity indices of its peak and trough.</summary>
    internal readonly record struct DrawdownTrough<T>(T Maximum, int Peak, int Trough) where T : struct;

    /// <summary>
    /// The single definition of the spec §5.4 tie rules shared by every drawdown episode computation (the two duration metrics here and
    /// the evaluation artifact's ratio and amount episodes): the trough is the FIRST index holding the maximal depth (strict comparison,
    /// so later equal depths never replace it) and the peak is the LATEST index before it whose equity equals the running high
    /// (<c>E[i] &gt;= E[peak]</c>). <see cref="DrawdownTrough{T}.Maximum"/> is zero when there is no drawdown.
    /// </summary>
    public static DrawdownTrough<T> FindFirstMaximalTrough<T>(
        ImmutableArray<decimal> equity,
        ImmutableArray<T> depthSeries,
        CancellationToken cancellationToken = default)
        where T : struct, INumber<T>
    {
        T maximum = T.Zero;
        int latestPeak = 0;
        int peak = 0;
        int trough = 0;
        for (int i = 0; i < depthSeries.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (equity[i] >= equity[latestPeak]) latestPeak = i;
            if (depthSeries[i] > maximum)
            {
                maximum = depthSeries[i];
                peak = latestPeak;
                trough = i;
            }
        }
        return new DrawdownTrough<T>(maximum, peak, trough);
    }

    /// <summary>The first index after <paramref name="trough"/> whose equity regains <c>E[peak]</c> (<c>E[i] &gt;= E[peak]</c>), or null when it never does.</summary>
    public static int? FindRecoveryIndex(
        ImmutableArray<decimal> equity,
        int peak,
        int trough,
        CancellationToken cancellationToken = default)
    {
        for (int i = trough + 1; i < equity.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (equity[i] >= equity[peak]) return i;
        }
        return null;
    }

    /// <summary>D[j] = (H[j]-E[j])/H[j] for j = 0..m, where H[j] = max(E[0..j]). E[0] &gt; 0 is guaranteed by BacktestConfiguration, so H[j] &gt; 0 always.</summary>
    public static ImmutableArray<double> ComputeDrawdownRatioSeries(
        ImmutableArray<decimal> equity,
        CancellationToken cancellationToken = default)
    {
        var series = ImmutableArray.CreateBuilder<double>(equity.Length);
        decimal peak = equity[0];
        for (int j = 0; j < equity.Length; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            if (equity[j] > peak)
            {
                peak = equity[j];
            }
            decimal ratio = (peak - equity[j]) / peak;
            double value = (double)ratio;
            if (!double.IsFinite(value))
            {
                throw new ArithmeticException("Drawdown ratio produced a non-finite value.");
            }
            series.Add(value);
        }
        return series.MoveToImmutable();
    }

    /// <summary>H[j]-E[j] for j = 0..m, in decimal (Rule DT-1: drawdown amount is a currency value).</summary>
    public static ImmutableArray<decimal> ComputeDrawdownAmountSeries(
        ImmutableArray<decimal> equity,
        CancellationToken cancellationToken = default)
    {
        var series = ImmutableArray.CreateBuilder<decimal>(equity.Length);
        decimal peak = equity[0];
        for (int j = 0; j < equity.Length; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            if (equity[j] > peak)
            {
                peak = equity[j];
            }
            series.Add(peak - equity[j]);
        }
        return series.MoveToImmutable();
    }

    public static MetricValue ComputeMaxDrawdown(
        ImmutableArray<double> ratioSeries,
        CancellationToken cancellationToken = default)
    {
        double max = 0d;
        for (int i = 0; i < ratioSeries.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            double d = ratioSeries[i];
            if (d > max) max = d;
        }
        return MetricCalculation.FromDouble(max, MetricUnit.DrawdownRatio);
    }

    public static MetricValue ComputeMaxDrawdownAmount(
        ImmutableArray<decimal> amountSeries,
        CancellationToken cancellationToken = default)
    {
        decimal max = 0m;
        for (int i = 0; i < amountSeries.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            decimal a = amountSeries[i];
            if (a > max) max = a;
        }
        return MetricValue.Valid(max, MetricUnit.Currency);
    }

    /// <summary>
    /// MaxDepthDrawdownDuration = underwater bars of the maximum-depth episode under the ratio series (the headline MaxDrawdown): peak index
    /// = latest equal peak before the first maximal trough (spec §5.4), end = first later E &gt;= peak, or E[m] when never recovered
    /// (right-censored: <see cref="DrawdownDurationResult.RightCensored"/> is true iff that episode never regains its peak). No drawdown at all
    /// -&gt; Valid(0), not censored. m &lt; 1 -&gt; InsufficientData(EmptyInput).
    /// </summary>
    public static DrawdownDurationResult ComputeMaxDepthDrawdownDuration(
        ImmutableArray<decimal> equity,
        ImmutableArray<double> ratioSeries,
        CancellationToken cancellationToken = default)
    {
        int m = equity.Length - 1;
        if (m < 1)
        {
            return new DrawdownDurationResult(MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput), false);
        }

        DrawdownTrough<double> deepest = FindFirstMaximalTrough(equity, ratioSeries, cancellationToken);
        if (deepest.Maximum == 0d)
        {
            return new DrawdownDurationResult(MetricValue.Valid(0m, MetricUnit.Bars), false);
        }

        int? recovery = FindRecoveryIndex(equity, deepest.Peak, deepest.Trough, cancellationToken);
        return new DrawdownDurationResult(MetricValue.Valid((recovery ?? m) - deepest.Peak, MetricUnit.Bars), RightCensored: recovery is null);
    }

    /// <summary>
    /// LongestDrawdownDuration = the longest underwater span over all drawdown episodes, in bars. An episode runs from a peak index (a bar
    /// whose E &gt;= every earlier E; equal highs move the peak to the latest) to the first later bar with E &gt;= that peak, or to E[m] when
    /// never recovered (right-censored). A peak followed immediately by a new high is not an episode. No drawdown -&gt; Valid(0).
    /// <see cref="DrawdownDurationResult.RightCensored"/> is true iff an unrecovered episode exists and its length equals the returned value,
    /// i.e. the value is then only a lower bound (conservatively also when a completed episode ties with it). m &lt; 1 -&gt; InsufficientData(EmptyInput).
    /// </summary>
    public static DrawdownDurationResult ComputeLongestDrawdownDuration(
        ImmutableArray<decimal> equity,
        CancellationToken cancellationToken = default)
    {
        int m = equity.Length - 1;
        if (m < 1)
        {
            return new DrawdownDurationResult(MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.Bars, MetricReason.EmptyInput), false);
        }

        int peakIndex = 0;
        int longest = 0;
        for (int i = 1; i <= m; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            if (equity[i] >= equity[peakIndex])
            {
                // Every bar strictly between peakIndex and i is below the peak, so i - peakIndex > 1 iff a drawdown occurred.
                if (i - peakIndex > 1 && i - peakIndex > longest) longest = i - peakIndex;
                peakIndex = i;
            }
        }
        int unrecoveredBars = m - peakIndex;
        if (unrecoveredBars > longest) longest = unrecoveredBars;
        return new DrawdownDurationResult(MetricValue.Valid(longest, MetricUnit.Bars), RightCensored: unrecoveredBars > 0 && unrecoveredBars == longest);
    }

    /// <summary>UlcerIndex = sqrt(sum((100*D[j])^2) / m), j = 1..m. m &gt;= 1 required (spec §5.4).</summary>
    public static MetricValue ComputeUlcerIndex(
        ImmutableArray<double> ratioSeries,
        int m,
        CancellationToken cancellationToken = default)
    {
        if (m < 1)
        {
            return MetricValue.NonValid(MetricStatus.InsufficientData, MetricUnit.PercentPoints, MetricReason.EmptyInput);
        }

        double sumOfSquares = 0d;
        for (int j = 1; j <= m; j++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, j);
            double percentPoints = 100d * ratioSeries[j];
            sumOfSquares += percentPoints * percentPoints;
        }
        double ulcer = Math.Sqrt(sumOfSquares / m);
        return MetricCalculation.FromDouble(ulcer, MetricUnit.PercentPoints);
    }
}
