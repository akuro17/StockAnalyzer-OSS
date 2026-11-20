using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

public enum BootstrapStatKind : byte
{
    Mean = 0,
    Median = 1,
    StdDev = 2,
    Sharpe = 3,
}

public enum BootstrapBlockMode : byte
{
    CircularBlock = 0,
    Stationary = 1,
}

public readonly record struct BootstrapResult(
    decimal Estimate,
    decimal Lower,
    decimal Upper,
    decimal StandardError);

/// <summary>
/// Deterministic circular-block and Politis-Romano stationary bootstrap confidence intervals
/// backed by <see cref="DeterministicRng"/> and pooled temporary buffers.
/// </summary>
public static class DslResamplingMath
{
    public const int MinSeriesLength = 10;
    public const int MaxSeriesLength = DslArrayArena.MaxArrayLength; // 512
    public const int MinResamples = 50;
    public const int MaxResamples = 1_000;
    public const double MinAlpha = 0.001;
    public const double MaxAlpha = 0.5;
    public const double MinTailResolutionProduct = 2.0;

    public static MathResult<BootstrapResult> BootConfidenceInterval(
        scoped ReadOnlySpan<decimal> series,
        BootstrapStatKind statKind,
        int nResamples,
        int blockLen,
        double alpha,
        ulong seed,
        BootstrapBlockMode blockMode,
        ref DslExecutionBudget budget)
    {
        int n = series.Length;
        if (n < MinSeriesLength || n > MaxSeriesLength ||
            (byte)statKind > (byte)BootstrapStatKind.Sharpe ||
            nResamples < MinResamples || nResamples > MaxResamples ||
            blockLen < 1 || blockLen > n / 2 ||
            !double.IsFinite(alpha) || alpha < MinAlpha || alpha > MaxAlpha ||
            (nResamples * alpha) < MinTailResolutionProduct - 1e-12 ||
            (byte)blockMode > (byte)BootstrapBlockMode.Stationary)
        {
            return MathResult<BootstrapResult>.Fail(MathErrorCode.DomainError);
        }

        double[] baseRent = ArrayPool<double>.Shared.Rent(n);
        double[] sampleRent = ArrayPool<double>.Shared.Rent(n);
        double[] statsRent = ArrayPool<double>.Shared.Rent(nResamples);
        try
        {
            Span<double> baseVals = baseRent.AsSpan(0, n);
            Span<double> sampleBuf = sampleRent.AsSpan(0, n);
            Span<double> bootStats = statsRent.AsSpan(0, nResamples);

            for (int i = 0; i < n; i++)
            {
                double v = (double)series[i];
                if (!double.IsFinite(v))
                {
                    return MathResult<BootstrapResult>.Fail(MathErrorCode.NonFiniteResult);
                }

                baseVals[i] = v;
            }

            baseVals.CopyTo(sampleBuf);
            MathResult<double> estRes = ComputeStat(sampleBuf, statKind);
            if (!estRes.IsOk)
            {
                return MathResult<BootstrapResult>.Fail(estRes.Error);
            }

            var rng = new DeterministicRng(seed);
            double stationaryRestartProb = 1.0 / blockLen;

            for (int r = 0; r < nResamples; r++)
            {
                if (!budget.TryConsume(1))
                {
                    return MathResult<BootstrapResult>.Fail(MathErrorCode.BudgetExceeded);
                }

                if (blockMode == BootstrapBlockMode.CircularBlock)
                {
                    int pos = 0;
                    while (pos < n)
                    {
                        int start = rng.NextInt(n);
                        int take = Math.Min(blockLen, n - pos);
                        for (int k = 0; k < take; k++)
                        {
                            sampleBuf[pos++] = baseVals[(start + k) % n];
                        }
                    }
                }
                else
                {
                    int idx = rng.NextInt(n);
                    sampleBuf[0] = baseVals[idx];
                    for (int t = 1; t < n; t++)
                    {
                        if (rng.NextUnitDouble() < stationaryRestartProb)
                        {
                            idx = rng.NextInt(n);
                        }
                        else
                        {
                            idx++;
                            if (idx >= n)
                            {
                                idx = 0;
                            }
                        }

                        sampleBuf[t] = baseVals[idx];
                    }
                }

                MathResult<double> statRes = ComputeStat(sampleBuf, statKind);
                if (!statRes.IsOk)
                {
                    return MathResult<BootstrapResult>.Fail(statRes.Error);
                }

                bootStats[r] = statRes.Value;
            }

            double? lowerRaw = OrderStatisticsMath.QuantileType7(bootStats, alpha * 0.5);
            double? upperRaw = OrderStatisticsMath.QuantileType7(bootStats, 1.0 - (alpha * 0.5));
            if (lowerRaw is not { } loVal || upperRaw is not { } hiVal ||
                !WindowMoments.TryCompute(bootStats, FlatWindowRule.Returns, out WindowMoments statMoments))
            {
                return MathResult<BootstrapResult>.Fail(MathErrorCode.NonFiniteResult);
            }

            double seVal = statMoments.SampleStandardDeviation;
            MathResult<decimal> decEst = DslElementaryMath.NarrowDouble(estRes.Value);
            MathResult<decimal> decLo = DslElementaryMath.NarrowDouble(loVal);
            MathResult<decimal> decHi = DslElementaryMath.NarrowDouble(hiVal);
            MathResult<decimal> decSe = DslElementaryMath.NarrowDouble(seVal);

            if (!decEst.IsOk) return MathResult<BootstrapResult>.Fail(decEst.Error);
            if (!decLo.IsOk) return MathResult<BootstrapResult>.Fail(decLo.Error);
            if (!decHi.IsOk) return MathResult<BootstrapResult>.Fail(decHi.Error);
            if (!decSe.IsOk) return MathResult<BootstrapResult>.Fail(decSe.Error);

            return MathResult<BootstrapResult>.Ok(new BootstrapResult(decEst.Value, decLo.Value, decHi.Value, decSe.Value));
        }
        finally
        {
            ArrayPool<double>.Shared.Return(baseRent, clearArray: true);
            ArrayPool<double>.Shared.Return(sampleRent, clearArray: true);
            ArrayPool<double>.Shared.Return(statsRent, clearArray: true);
        }
    }

    private static MathResult<double> ComputeStat(Span<double> values, BootstrapStatKind kind)
    {
        if (kind == BootstrapStatKind.Median)
        {
            double? med = OrderStatisticsMath.Median(values);
            return med is { } validMed && double.IsFinite(validMed)
                ? MathResult<double>.Ok(validMed)
                : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (!WindowMoments.TryCompute(values, FlatWindowRule.Series, out WindowMoments moments))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        return kind switch
        {
            BootstrapStatKind.Mean => MathResult<double>.Ok(moments.Mean),
            BootstrapStatKind.StdDev => double.IsFinite(moments.SampleStandardDeviation)
                ? MathResult<double>.Ok(moments.SampleStandardDeviation)
                : MathResult<double>.Fail(MathErrorCode.NonFiniteResult),
            BootstrapStatKind.Sharpe => moments.IsFlat || !(moments.SampleStandardDeviation > 0.0)
                ? MathResult<double>.Fail(MathErrorCode.DivisionByZero)
                : MathResult<double>.Ok(moments.Mean / moments.SampleStandardDeviation),
            _ => MathResult<double>.Fail(MathErrorCode.DomainError),
        };
    }
}
