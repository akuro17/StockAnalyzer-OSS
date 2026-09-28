using System;
using System.Collections.Immutable;
using System.Threading;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Approved Politis-White circular block-length selection and deterministic resampling.</summary>
internal static class PolitisWhiteBlockBootstrap
{
    internal static int OptimalCircularBlockLength(
        ImmutableArray<double> x,
        CancellationToken cancellationToken = default)
    {
        if (TryOptimalCircularBlockLength(x.AsSpan(), cancellationToken, out int blockLength, out MetricReason failureReason))
        {
            return blockLength;
        }

        throw new ArithmeticException($"Block-length selection failed: {failureReason}.");
    }

    internal static bool TryOptimalCircularBlockLength(
        ReadOnlySpan<double> x,
        CancellationToken cancellationToken,
        out int blockLength,
        out MetricReason failureReason)
    {
        if (x.Length == 0)
        {
            throw new ArgumentException("The sample must not be empty.", nameof(x));
        }

        double first = x[0];
        if (!double.IsFinite(first))
        {
            blockLength = 0;
            failureReason = MetricReason.NonFiniteResult;
            return false;
        }

        bool constant = true;
        double mean = 0d;
        for (int i = 0; i < x.Length; i++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, i);
            double value = x[i];
            if (!double.IsFinite(value))
            {
                blockLength = 0;
                failureReason = MetricReason.NonFiniteResult;
                return false;
            }
            constant &= value == first;
            mean += value;
        }
        if (constant)
        {
            blockLength = 1;
            failureReason = MetricReason.None;
            return true;
        }
        mean /= x.Length;
        if (!double.IsFinite(mean))
        {
            blockLength = 0;
            failureReason = MetricReason.NonFiniteResult;
            return false;
        }

        int n = x.Length;
        var eps = new double[n];
        for (int t = 0; t < n; t++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, t);
            eps[t] = x[t] - mean;
        }

        double bMax = Math.Ceiling(Math.Min(3.0 * Math.Sqrt(n), n / 3.0));
        int kn = Math.Max(5, (int)Math.Log10(n));
        int mMax = (int)Math.Ceiling(Math.Sqrt(n)) + kn;
        double cv = 2.0 * Math.Sqrt(Math.Log10(n) / n);

        var acv = new double[mMax + 1];
        var absAcorr = new double[mMax + 1];
        int? optM = null;
        for (int i = 0; i <= mMax; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double v1 = 0d;
            for (int t = i + 1; t < n; t++)
            {
                MetricCalculation.CheckCancellation(cancellationToken, t);
                v1 += eps[t] * eps[t];
            }
            double v2 = 0d;
            for (int t = 0; t < n - i - 1; t++)
            {
                MetricCalculation.CheckCancellation(cancellationToken, t);
                v2 += eps[t] * eps[t];
            }
            double crossProd = 0d;
            for (int t = 0; t < n - i; t++)
            {
                MetricCalculation.CheckCancellation(cancellationToken, t);
                crossProd += eps[i + t] * eps[t];
            }

            acv[i] = crossProd / n;
            double denominator = Math.Sqrt(v1 * v2);
            absAcorr[i] = Math.Abs(crossProd) / denominator;
            if (!double.IsFinite(acv[i]) || !double.IsFinite(absAcorr[i]))
            {
                blockLength = 0;
                failureReason = MetricReason.NonFiniteResult;
                return false;
            }

            if (i >= kn && optM is null)
            {
                bool allBelow = true;
                for (int j = i - kn; j < i; j++)
                {
                    if (!double.IsFinite(absAcorr[j]) || !(absAcorr[j] < cv))
                    {
                        allBelow = false;
                        break;
                    }
                }
                if (allBelow) optM = i - kn;
            }
        }

        int m = optM.HasValue ? 2 * Math.Max(optM.Value, 1) : mMax;
        m = Math.Min(m, mMax);

        double g = 0d;
        double lrAcv = acv[0];
        for (int k = 1; k <= m; k++)
        {
            MetricCalculation.CheckCancellation(cancellationToken, k);
            double kOverM = (double)k / m;
            double lam = kOverM <= 0.5 ? 1.0 : 2.0 * (1.0 - kOverM);
            g += 2.0 * lam * k * acv[k];
            lrAcv += 2.0 * lam * acv[k];
        }

        double dCb = 4.0 / 3.0 * lrAcv * lrAcv;
        if (!(dCb > 0d) || !double.IsFinite(dCb))
        {
            blockLength = 0;
            failureReason = double.IsFinite(dCb) ? MetricReason.UnexpectedZeroDivisor : MetricReason.NonFiniteResult;
            return false;
        }

        double bCb = Math.Pow(2.0 * g * g / dCb, 1.0 / 3.0) * Math.Pow(n, 1.0 / 3.0);
        bCb = Math.Min(bCb, bMax);
        if (!double.IsFinite(bCb))
        {
            blockLength = 0;
            failureReason = MetricReason.NonFiniteResult;
            return false;
        }

        blockLength = Math.Max(1, (int)Math.Round(bCb, MidpointRounding.AwayFromZero));
        failureReason = MetricReason.None;
        return true;
    }

    internal static ImmutableArray<double> CircularBlockResample(
        ImmutableArray<double> x,
        int blockLength,
        Random rng,
        CancellationToken cancellationToken = default)
    {
        if (x.IsDefaultOrEmpty) throw new ArgumentException("The sample must not be empty.", nameof(x));
        var buffer = new double[x.Length];
        CircularBlockResample(x.AsSpan(), blockLength, rng, buffer, cancellationToken);
        return ImmutableArray.Create(buffer);
    }

    internal static void CircularBlockResample(
        ReadOnlySpan<double> x,
        int blockLength,
        Random rng,
        Span<double> buffer,
        CancellationToken cancellationToken = default)
    {
        if (x.Length == 0) throw new ArgumentException("The sample must not be empty.", nameof(x));
        if (blockLength < 1 || blockLength > x.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(blockLength), blockLength, "blockLength must be in [1, sample length].");
        }
        ArgumentNullException.ThrowIfNull(rng);
        if (buffer.Length != x.Length)
        {
            throw new ArgumentException("The output buffer length must equal the sample length.", nameof(buffer));
        }

        int count = 0;
        while (count < x.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = rng.Next(0, x.Length);
            for (int j = 0; j < blockLength && count < x.Length; j++)
            {
                MetricCalculation.CheckCancellation(cancellationToken, count);
                buffer[count++] = x[(start + j) % x.Length];
            }
        }
    }
}
