using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Multi-factor corporate action and unadjusted split detector (TASK-BT-04).
/// Analyzes price sudden jumps, volume surges, and typical split ratio integrality
/// to identify suspicious corporate actions without throwing exceptions or corrupting simulation state.
/// </summary>
public static class CorporateActionDetector
{
    // Typical split and reverse split multipliers:
    // Splits: 2:1, 3:1, 4:1, 5:1, 10:1
    // Reverse splits: 1:2 (0.5), 1:3 (0.3333..), 1:4 (0.25), 1:5 (0.2), 1:10 (0.1)
    private static readonly decimal[] TargetRatios =
    [
        2.0m,
        3.0m,
        4.0m,
        5.0m,
        10.0m,
        0.5m,
        1.0m / 3.0m,
        0.25m,
        0.2m,
        0.1m,
    ];

    private const decimal MinPriceJumpRatio = 0.25m;      // >= 25% price change
    private const decimal VolumeSurgeMultiplier = 2.5m;   // >= 2.5x median volume
    private const decimal RatioTolerance = 0.05m;         // +/- 5% tolerance

    public static ImmutableArray<BacktestWarning> DetectPotentialSplits(ImmutableArray<CandleData> bars)
    {
        if (bars.IsDefaultOrEmpty || bars.Length < 2)
        {
            return ImmutableArray<BacktestWarning>.Empty;
        }

        var warnings = ImmutableArray.CreateBuilder<BacktestWarning>();
        int n = bars.Length;

        for (int t = 1; t < n; t++)
        {
            CandleData prevBar = bars[t - 1];
            CandleData currBar = bars[t];

            if (prevBar.Close <= 0m || currBar.Close <= 0m)
            {
                continue;
            }

            // 1. Price jump: |Close_t / Close_{t-1} - 1| >= 0.25
            decimal priceChangeRatio = Math.Abs((currBar.Close / prevBar.Close) - 1m);
            if (priceChangeRatio < MinPriceJumpRatio)
            {
                continue;
            }

            // 2. Volume surge: Volume_t >= 2.5 * Median(Volume_{t-K..t-1}) (K = min(20, t))
            int k = Math.Min(20, t);
            decimal medianVolume = CalculateMedianVolume(bars, t - k, k);
            bool volumeSurged = medianVolume > 0m
                ? currBar.Volume >= (long)Math.Ceiling(VolumeSurgeMultiplier * medianVolume)
                : currBar.Volume > 0;

            if (!volumeSurged)
            {
                continue;
            }

            // 3. Ratio integrality: Close_{t-1} / Close_t is close to a standard corporate action ratio
            decimal observedRatio = prevBar.Close / currBar.Close;
            if (!MatchesTargetRatio(observedRatio, out decimal matchedTarget))
            {
                continue;
            }

            decimal surgeFactor = medianVolume > 0m ? (decimal)currBar.Volume / medianVolume : 1m;
            string message = FormattableString.Invariant(
                $"Potential unadjusted stock split detected at bar {t} ({currBar.Timestamp:yyyy-MM-dd}): Price ratio {observedRatio:F2} (near {matchedTarget:F2}), volume surge {surgeFactor:F1}x.");

            warnings.Add(new BacktestWarning(
                WarningCode.PotentialUnadjustedSplit,
                t,
                currBar.Timestamp,
                message));
        }

        return warnings.ToImmutable();
    }

    private static bool MatchesTargetRatio(decimal ratio, out decimal matchedTarget)
    {
        foreach (decimal target in TargetRatios)
        {
            if (Math.Abs(ratio - target) / target <= RatioTolerance)
            {
                matchedTarget = target;
                return true;
            }
        }

        matchedTarget = 0m;
        return false;
    }

    private static decimal CalculateMedianVolume(ImmutableArray<CandleData> bars, int startIndex, int count)
    {
        Span<long> buffer = count <= 64 ? stackalloc long[count] : new long[count];
        for (int i = 0; i < count; i++)
        {
            buffer[i] = bars[startIndex + i].Volume;
        }

        buffer.Sort();
        int mid = count / 2;
        if (count % 2 != 0)
        {
            return buffer[mid];
        }

        return ((decimal)buffer[mid - 1] + (decimal)buffer[mid]) / 2m;
    }

    /// <summary>
    /// Applies backward adjustment to bars preceding detected unadjusted stock splits.
    /// For a split detected at index t with ratio K (Close_{t-1} / Close_t):
    /// All bars k &lt; t have their OHLC divided by K, and Volume multiplied by K (integer rounded).
    /// </summary>
    public static ImmutableArray<CandleData> AdjustSplits(ImmutableArray<CandleData> bars, ImmutableArray<BacktestWarning> warnings)
    {
        if (bars.IsDefaultOrEmpty || warnings.IsDefaultOrEmpty)
        {
            return bars;
        }

        var splitEvents = new List<(int BarIndex, decimal Ratio)>();
        foreach (BacktestWarning warning in warnings)
        {
            if (warning.Code == WarningCode.PotentialUnadjustedSplit && warning.BarIndex > 0 && warning.BarIndex < bars.Length)
            {
                decimal prevClose = bars[warning.BarIndex - 1].Close;
                decimal currClose = bars[warning.BarIndex].Close;
                if (prevClose > 0m && currClose > 0m && MatchesTargetRatio(prevClose / currClose, out decimal matchedTarget))
                {
                    splitEvents.Add((warning.BarIndex, matchedTarget));
                }
            }
        }

        if (splitEvents.Count == 0)
        {
            return bars;
        }

        CandleData[] result = bars.ToArray();
        foreach (var (splitBarIndex, ratio) in splitEvents)
        {
            for (int k = 0; k < splitBarIndex; k++)
            {
                CandleData b = result[k];
                decimal adjOpen = b.Open / ratio;
                decimal adjHigh = b.High / ratio;
                decimal adjLow = b.Low / ratio;
                decimal adjClose = b.Close / ratio;
                long adjVolume = (long)Math.Floor(b.Volume * ratio);

                result[k] = new CandleData(
                    Timestamp: b.Timestamp,
                    Open: adjOpen,
                    High: adjHigh,
                    Low: adjLow,
                    Close: adjClose,
                    Volume: adjVolume);
            }
        }

        return result.ToImmutableArray();
    }
}

/// <summary>
/// Thrown when market data integrity checks fail (e.g., unadjusted stock splits detected under AbortOnDetection mode).
/// </summary>
public sealed class DataIntegrityException : InvalidOperationException
{
    public DataIntegrityException(string message) : base(message) { }
    public DataIntegrityException(string message, Exception innerException) : base(message, innerException) { }
}
