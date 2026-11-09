using System;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// High-performance, zero-allocation computation engine for non-parametric rank correlation
/// (Spearman's rho and Kendall's tau-b) with tie adjustments.
/// </summary>
public static class RankCorrelationEngine
{
    private const double ZeroTolerance = 1e-12;

    /// <summary>
    /// Computes fractional mid-ranks for a series with strict IEEE 754 equivalence.
    /// Average rank is assigned to ties.
    /// </summary>
    /// <param name="values">Input values of length N.</param>
    /// <param name="ranks">Destination span of length &gt;= N for computed mid-ranks.</param>
    public static void ComputeMidRanks(ReadOnlySpan<double> values, Span<double> ranks)
    {
        int n = values.Length;
        for (int k = 0; k < n; k++)
        {
            double vk = values[k];
            int less = 0;
            int equal = 0;

            for (int j = 0; j < n; j++)
            {
                if (j == k) continue;
                double diff = vk - values[j];
                if (Math.Abs(diff) <= ZeroTolerance)
                {
                    equal++;
                }
                else if (diff > ZeroTolerance)
                {
                    less++;
                }
            }

            ranks[k] = 1.0 + less + 0.5 * equal;
        }
    }

    /// <summary>
    /// Computes Spearman's rank correlation coefficient (rho) by applying Pearson correlation
    /// directly to the mid-rank vectors. Guarantees output range [-1.0, 1.0] even under heavy ties.
    /// </summary>
    public static double CalculateSpearman(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        Span<double> rankXBuffer,
        Span<double> rankYBuffer)
    {
        int n = x.Length;
        if (n < 2 || y.Length != n)
        {
            return 0.0;
        }

        ComputeMidRanks(x, rankXBuffer);
        ComputeMidRanks(y, rankYBuffer);

        // Sum of mid-ranks is invariant: sum = n * (n + 1) / 2
        double meanRank = (n + 1) * 0.5;

        double cov = 0.0;
        double varX = 0.0;
        double varY = 0.0;

        for (int k = 0; k < n; k++)
        {
            double dx = rankXBuffer[k] - meanRank;
            double dy = rankYBuffer[k] - meanRank;

            cov += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }

        if (!(varX > ZeroTolerance) || !(varY > ZeroTolerance))
        {
            return 0.0;
        }

        double r = cov / Math.Sqrt(varX * varY);
        return Math.Clamp(r, -1.0, 1.0);
    }

    /// <summary>
    /// Computes Kendall's rank correlation coefficient (tau-b) with pairwise tie adjustments.
    /// </summary>
    public static double CalculateKendallTauB(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        int n = x.Length;
        if (n < 2 || y.Length != n)
        {
            return 0.0;
        }

        long concordant = 0;
        long discordant = 0;
        long tiesX = 0;
        long tiesY = 0;

        for (int j = 0; j < n - 1; j++)
        {
            double xj = x[j];
            double yj = y[j];

            for (int k = j + 1; k < n; k++)
            {
                double dx = x[k] - xj;
                double dy = y[k] - yj;

                bool tieX = Math.Abs(dx) <= ZeroTolerance;
                bool tieY = Math.Abs(dy) <= ZeroTolerance;

                if (tieX && tieY)
                {
                    // Joint tie (T_XY): omitted from both numerator and denominator in tau-b
                    continue;
                }

                if (tieX)
                {
                    tiesX++;
                }
                else if (tieY)
                {
                    tiesY++;
                }
                else if ((dx > 0.0 && dy > 0.0) || (dx < 0.0 && dy < 0.0))
                {
                    concordant++;
                }
                else
                {
                    discordant++;
                }
            }
        }

        double denomX = concordant + discordant + tiesX;
        double denomY = concordant + discordant + tiesY;

        if (!(denomX > 0.0) || !(denomY > 0.0))
        {
            return 0.0;
        }

        double tau = (concordant - discordant) / Math.Sqrt(denomX * denomY);
        return Math.Clamp(tau, -1.0, 1.0);
    }
}
