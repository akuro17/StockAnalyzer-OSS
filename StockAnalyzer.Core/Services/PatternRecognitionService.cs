using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Detects canonical chart patterns (Head &amp; Shoulders, Double/Triple Top/Bottom, ...) by matching
/// Z-normalized price windows against stretched, Z-normalized templates with DTW.
/// Pure C# implementation (<see cref="DtwMath"/>); no Python process is required. Prices are converted to double only
/// for this dimensionless shape comparison (Z-normalized values); the reported score is a plain double, not a price.
/// </summary>
public class PatternRecognitionService : IPatternRecognitionService
{
    /// <summary>Decimal places of the reported probability.</summary>
    private const int ProbabilityDecimals = 4;

    /// <summary>
    /// Idealized shapes (abstract Z-score scale) stretched to each window length by linear interpolation.
    /// Single source of truth for pattern names and shapes.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, double[] Shape)> PatternTemplates = new (string, double[])[]
    {
        ("HeadAndShoulders", new[] { 0.0, 1.0, 0.3, 1.5, 0.3, 1.0, 0.0 }),
        ("InverseHeadAndShoulders", new[] { 0.0, -1.0, -0.3, -1.5, -0.3, -1.0, 0.0 }),
        ("DoubleTop", new[] { 0.0, 1.0, 0.3, 1.0, 0.0 }),
        ("DoubleBottom", new[] { 0.0, -1.0, -0.3, -1.0, 0.0 }),
        ("TripleTop", new[] { 0.0, 1.0, 0.3, 1.0, 0.3, 1.0, 0.0 }),
        ("TripleBottom", new[] { 0.0, -1.0, -0.3, -1.0, -0.3, -1.0, 0.0 }),
    };

    /// <summary>
    /// Detects chart patterns in the given candle data using DTW waveform matching.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="minWindow">Minimum window size for pattern detection (bars).</param>
    /// <param name="maxWindow">Maximum window size for pattern detection (bars).</param>
    /// <param name="windowStep">Step size for sliding window (bars).</param>
    /// <param name="threshold">Minimum probability threshold (0.0 to 1.0).</param>
    /// <param name="warpingRadius">Sakoe-Chiba radius in bars; <see cref="DtwMath.UnconstrainedRadius"/> (-1) = unconstrained, 0 = diagonal only.</param>
    /// <param name="shortSpanPenaltyAlpha">Exponent of the short-span penalty; 0 disables it, negative values are rejected.</param>
    /// <returns>
    /// A <see cref="PatternRecognitionResult"/> containing detected patterns, highest similarity score first
    /// (see <see cref="DetectedPattern.Probability"/>; a heuristic score, not a statistical probability).
    /// Fails when minWindow/windowStep &lt; 1, maxWindow &lt; minWindow, warpingRadius &lt; -1 or shortSpanPenaltyAlpha &lt; 0.
    /// The whole candle history is scanned at once (batch analysis), so a pattern's EndIndex is the earliest bar at which it was observable.
    /// </returns>
    public Task<PatternRecognitionResult> DetectAsync(
        IReadOnlyList<CandleData> candles,
        int minWindow = ChartConstants.PatternRecognitionDefaultMinWindow,
        int maxWindow = ChartConstants.PatternRecognitionDefaultMaxWindow,
        int windowStep = ChartConstants.PatternRecognitionDefaultWindowStep,
        double threshold = ChartConstants.PatternRecognitionDefaultThreshold,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius,
        double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha)
    {
        if (candles == null || candles.Count < minWindow)
        {
            return Task.FromResult(PatternRecognitionResult.Success(Array.Empty<DetectedPattern>()));
        }

        if (minWindow < 1 || windowStep < 1)
        {
            return Task.FromResult(PatternRecognitionResult.Failure("minWindow and windowStep must be >= 1."));
        }

        if (maxWindow < minWindow)
        {
            return Task.FromResult(PatternRecognitionResult.Failure("maxWindow must be >= minWindow."));
        }

        if (shortSpanPenaltyAlpha < 0)
        {
            return Task.FromResult(PatternRecognitionResult.Failure("shortSpanPenaltyAlpha must not be negative."));
        }

        if (warpingRadius < DtwMath.UnconstrainedRadius)
        {
            return Task.FromResult(PatternRecognitionResult.Failure($"warpingRadius must be >= {DtwMath.UnconstrainedRadius} ({DtwMath.UnconstrainedRadius} = unconstrained)."));
        }

        double[] close = candles.Select(c => (double)c.Close).ToArray();

        return Task.Run(() =>
        {
            try
            {
                return PatternRecognitionResult.Success(
                    Detect(close, minWindow, maxWindow, windowStep, threshold, warpingRadius, shortSpanPenaltyAlpha));
            }
            catch (Exception ex)
            {
                return PatternRecognitionResult.Failure(ex.Message);
            }
        });
    }

    private static List<DetectedPattern> Detect(
        double[] close,
        int minWindow,
        int maxWindow,
        int windowStep,
        double threshold,
        int warpingRadius,
        double shortSpanPenaltyAlpha)
    {
        int n = close.Length;
        int count = PatternTemplates.Count;
        var bestProb = new double[count];
        var bestStart = new int[count];
        var bestEnd = new int[count];

        var stretched = new double[count][];
        double[] zSegment = new double[Math.Min(maxWindow, n)];

        for (int w = minWindow; w <= Math.Min(maxWindow, n); w += windowStep)
        {
            for (int t = 0; t < count; t++)
            {
                stretched[t] = StretchAndNormalize(PatternTemplates[t].Shape, w);
            }

            double sqrtW = Math.Sqrt(w);
            bool applyPenalty = shortSpanPenaltyAlpha > 0 && w < maxWindow;
            double penalty = applyPenalty ? Math.Pow((double)w / maxWindow, shortSpanPenaltyAlpha) : 1.0;

            for (int start = 0; start + w <= n; start += windowStep)
            {
                var segment = new ReadOnlySpan<double>(close, start, w);
                Span<double> z = zSegment.AsSpan(0, w);
                if (!ZNormalization.TryNormalize(segment, z))
                {
                    continue;
                }

                // The segment normalization is shared by all templates; only the DTW target differs.
                for (int t = 0; t < count; t++)
                {
                    double dist = DtwMath.Calculate(z, stretched[t], warpingRadius);
                    double prob = Math.Exp(-(dist / sqrtW)) * penalty;

                    if (prob > bestProb[t])
                    {
                        bestProb[t] = prob;
                        bestStart[t] = start;
                        bestEnd[t] = start + w - 1;
                    }
                }
            }
        }

        var detected = new List<DetectedPattern>();
        for (int t = 0; t < count; t++)
        {
            if (bestProb[t] >= threshold)
            {
                detected.Add(new DetectedPattern
                {
                    Name = PatternTemplates[t].Name,
                    Probability = Math.Round(bestProb[t], ProbabilityDecimals),
                    StartIndex = bestStart[t],
                    EndIndex = bestEnd[t]
                });
            }
        }

        // Stable sort keeps template order for equal probabilities.
        return detected.OrderByDescending(d => d.Probability).ToList();
    }

    /// <summary>
    /// Linearly stretches <paramref name="template"/> to <paramref name="length"/> points
    /// (equivalent to np.interp(np.linspace(0,1,length), np.linspace(0,1,template.Length), template))
    /// and Z-normalizes it unless it is flat.
    /// </summary>
    private static double[] StretchAndNormalize(double[] template, int length)
    {
        var stretched = new double[length];
        LinearInterpolateToLength(template, stretched);
        ZNormalization.TryNormalize(stretched, stretched);
        return stretched;
    }

    private static void LinearInterpolateToLength(ReadOnlySpan<double> source, Span<double> output)
    {
        int srcLen = source.Length;
        int tgtLen = output.Length;

        if (tgtLen == 1)
        {
            output[0] = source[0];
            return;
        }

        for (int i = 0; i < tgtLen; i++)
        {
            double t = (double)i / (tgtLen - 1) * (srcLen - 1);
            int lo = (int)t;
            int hi = Math.Min(lo + 1, srcLen - 1);
            double frac = t - lo;
            output[i] = source[lo] + frac * (source[hi] - source[lo]);
        }
    }
}
