using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// MESA Adaptive Moving Average (MAMA / FAMA) driven by a Burg maximum-entropy spectrum of the trailing window.
/// Output unit: price-native. Causal: bar t uses bars t-WindowSize+1..t only. Allocates nothing per call
/// beyond a one-time static frequency table.
/// </summary>
public static class MesaMath
{
    /// <summary>Trailing window (bars) whose spectrum estimates the dominant cycle.</summary>
    public const int WindowSize = 32;

    /// <summary>Order of the Burg autoregressive model.</summary>
    public const int BurgOrder = 10;

    /// <summary>Number of frequency points in [0, π) at which the model spectrum is evaluated.</summary>
    public const int SpectrumPoints = 256;

    /// <summary>Lowest spectrum bin searched for the peak: the first <c>MinPeakBinFraction</c> of the band is skipped (low-frequency drift).</summary>
    public const double MinPeakBinFraction = 0.02;

    /// <summary>Weight applied to alpha when FAMA follows MAMA.</summary>
    public const double FamaAlphaFactor = 0.5;

    private static readonly int MinPeakBin = Math.Max((int)(SpectrumPoints * MinPeakBinFraction), 1);

    // CosTable[k * BurgOrder + (p - 1)] = cos(p·π·k/SpectrumPoints); SinTable likewise (frequency grid of the spectrum).
    private static readonly double[] CosTable = BuildTable(Math.Cos);
    private static readonly double[] SinTable = BuildTable(Math.Sin);

    private static double[] BuildTable(Func<double, double> trig)
    {
        var table = new double[SpectrumPoints * BurgOrder];
        for (int k = 0; k < SpectrumPoints; k++)
        {
            for (int p = 1; p <= BurgOrder; p++)
            {
                table[k * BurgOrder + (p - 1)] = trig(p * Math.PI * k / SpectrumPoints);
            }
        }

        return table;
    }

    /// <summary>
    /// Bars before the first full window (and every bar when the series is shorter than <see cref="WindowSize"/>) carry the price itself.
    /// From the first full window on: alpha = clamp(2/(period+1), slowLimit, fastLimit) with period = the dominant cycle
    /// (2π / the spectrum-peak frequency), MAMA = alpha·price + (1−alpha)·MAMA, FAMA = 0.5·alpha·MAMA + (1−0.5·alpha)·FAMA,
    /// both seeded with the price of the first full window's last bar.
    /// </summary>
    /// <exception cref="ArgumentException">An output span is shorter than <paramref name="prices"/>.</exception>
    public static void CalculateMesa(ReadOnlySpan<double> prices, double fastLimit, double slowLimit, Span<double> outMama, Span<double> outFama)
    {
        if (outMama.Length < prices.Length || outFama.Length < prices.Length)
        {
            throw new ArgumentException("Output spans must be at least as long as the prices.");
        }

        if (prices.Length < WindowSize)
        {
            prices.CopyTo(outMama);
            prices.CopyTo(outFama);
            return;
        }

        prices[..(WindowSize - 1)].CopyTo(outMama);
        prices[..(WindowSize - 1)].CopyTo(outFama);

        double mama = prices[WindowSize - 1];
        double fama = prices[WindowSize - 1];

        Span<double> forward = stackalloc double[WindowSize];
        Span<double> backward = stackalloc double[WindowSize];
        Span<double> rows = stackalloc double[2 * (BurgOrder + 1)];

        for (int i = WindowSize - 1; i < prices.Length; i++)
        {
            ReadOnlySpan<double> window = prices.Slice(i - WindowSize + 1, WindowSize);
            double alpha = ComputeAlpha(window, fastLimit, slowLimit, forward, backward, rows);

            mama = alpha * prices[i] + (1.0 - alpha) * mama;
            fama = FamaAlphaFactor * alpha * mama + (1.0 - FamaAlphaFactor * alpha) * fama;
            outMama[i] = mama;
            outFama[i] = fama;
        }
    }

    private static double ComputeAlpha(
        ReadOnlySpan<double> window,
        double fastLimit,
        double slowLimit,
        Span<double> forward,
        Span<double> backward,
        Span<double> rows)
    {
        double sum = 0.0;
        for (int j = 0; j < window.Length; j++)
        {
            sum += window[j];
        }
        double mean = sum / window.Length;

        for (int j = 0; j < window.Length; j++)
        {
            forward[j] = window[j] - mean;
            backward[j] = forward[j];
        }

        rows.Clear();
        Span<double> previousRow = rows[..(BurgOrder + 1)];
        Span<double> currentRow = rows[(BurgOrder + 1)..];

        // Burg recursion: row m of the AR coefficient triangle depends only on row m-1.
        for (int m = 1; m <= BurgOrder; m++)
        {
            double numerator = 0.0;
            double denominator = 0.0;
            for (int j = m; j < window.Length; j++)
            {
                numerator += forward[j] * backward[j - 1];
                denominator += forward[j] * forward[j] + backward[j - 1] * backward[j - 1];
            }

            double reflection = denominator != 0.0 ? -2.0 * numerator / denominator : 0.0;

            currentRow.Clear();
            currentRow[m] = reflection;
            for (int k = 1; k < m; k++)
            {
                currentRow[k] = previousRow[k] + reflection * previousRow[m - k];
            }

            for (int j = m; j < window.Length; j++)
            {
                double f = forward[j];
                double b = backward[j - 1];
                forward[j] = f + reflection * b;
                backward[j - 1] = b + reflection * f;
            }

            Span<double> swap = previousRow;
            previousRow = currentRow;
            currentRow = swap;
        }

        // previousRow now holds row BurgOrder: A(z) = 1 + Σ_p a_p·z^-p. Spectrum = 1/|A|², so the peak is the minimum of |A|².
        int peakBin = MinPeakBin;
        double minPower = double.PositiveInfinity;
        for (int k = MinPeakBin; k < SpectrumPoints; k++)
        {
            double re = 1.0;
            double im = 0.0;
            int offset = k * BurgOrder;
            for (int p = 1; p <= BurgOrder; p++)
            {
                double coefficient = previousRow[p];
                re += coefficient * CosTable[offset + p - 1];
                im -= coefficient * SinTable[offset + p - 1];
            }

            double power = re * re + im * im;
            if (power < minPower)
            {
                minPower = power;
                peakBin = k;
            }
        }

        // Bin k sits at frequency k/(2·SpectrumPoints) cycles per bar, so the period is 2·SpectrumPoints/k bars.
        double period = 2.0 * SpectrumPoints / peakBin;
        double alpha = 2.0 / (period + 1.0);
        return Math.Max(slowLimit, Math.Min(fastLimit, alpha));
    }
}
