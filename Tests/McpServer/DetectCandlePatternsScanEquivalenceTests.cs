using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.McpServer.Tools;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// <c>sa_detect_candle_patterns</c> used to hand the detector a fresh copy of the prefix (<c>GetRange(0, i + 1)</c>) for every scanned
/// bar and ran the detector a second time for the final bar. The scan now gives the detector a zero-copy prefix view and reuses the
/// last scan result. These tests pin the output to the old copying implementation, which is kept here as the reference.
/// </summary>
public sealed class DetectCandlePatternsScanEquivalenceTests
{
    private const int SeriesCount = 500;
    private const int MinBars = 12;
    private const int MaxBars = 160;
    private const int RandomSeed = 20261104;
    private const decimal PriceStep = 0.1m;
    private const int MinimumDistinctPatternTypes = 10;
    private const int MinimumTotalMatches = 1000;

    private static readonly (bool Exclude, bool RequireTrend)[] Combinations =
    {
        (false, false),
        (true, false),
        (false, true),
        (true, true),
    };

    [Fact]
    public void Scan_EqualsTheCopyingReference_OnRandomSeries_ForEveryContextSetting()
    {
        var random = new Random(RandomSeed);
        var seenTypes = new HashSet<CandlePatternType>();
        int totalMatches = 0;
        int comparisons = 0;

        for (int s = 0; s < SeriesCount; s++)
        {
            List<CandleData> candles = RandomSeries(random, random.Next(MinBars, MaxBars + 1));
            int[] windows = { 1, 2, 5, candles.Count / 2, candles.Count, candles.Count + 7 };

            foreach (int window in windows)
            {
                foreach (var (exclude, requireTrend) in Combinations)
                {
                    var expected = ReferenceScan(candles, window, exclude, requireTrend);
                    var actual = DetectCandlePatternsTool.Scan(candles, window, exclude, requireTrend);

                    Assert.Equal(expected.Matches, actual.Matches);
                    Assert.Equal(expected.Latest, actual.Latest);

                    totalMatches += actual.Matches.Count;
                    foreach (var match in actual.Matches)
                    {
                        seenTypes.Add(match.Pattern);
                    }

                    comparisons++;
                }
            }
        }

        // Non-vacuity: the random series must actually produce many matches of many pattern types, otherwise equal empty results prove nothing.
        Assert.Equal(SeriesCount * 6 * Combinations.Length, comparisons);
        Assert.True(totalMatches >= MinimumTotalMatches, $"only {totalMatches} matches");
        Assert.True(seenTypes.Count >= MinimumDistinctPatternTypes, $"only {seenTypes.Count} distinct pattern types: {string.Join(",", seenTypes)}");
    }

    [Fact]
    public void Scan_FinalBarPatterns_EqualDirectDetectionOfTheWholeSeries()
    {
        var random = new Random(RandomSeed + 1);
        for (int s = 0; s < 100; s++)
        {
            List<CandleData> candles = RandomSeries(random, random.Next(MinBars, MaxBars + 1));
            foreach (var (exclude, requireTrend) in Combinations)
            {
                string[] direct = CandlePatternDetector.DetectPatterns(candles, exclude, requireTrend)
                    .Where(p => p != CandlePatternType.None)
                    .Select(p => p.ToString())
                    .ToArray();

                Assert.Equal(direct, DetectCandlePatternsTool.Scan(candles, 3, exclude, requireTrend).Latest);
            }
        }
    }

    [Fact]
    public void Scan_DoesNotModifyOrReorderTheInput()
    {
        var random = new Random(RandomSeed + 2);
        List<CandleData> candles = RandomSeries(random, 80);
        List<CandleData> snapshot = candles.ToList();

        DetectCandlePatternsTool.Scan(candles, 40, excludePatternBars: true, requireTrend: true);

        Assert.Equal(snapshot, candles);
    }

    [Fact]
    public void Scan_EmptyInput_ReturnsEmptyResults()
    {
        var result = DetectCandlePatternsTool.Scan(new List<CandleData>(), 5, excludePatternBars: false, requireTrend: false);

        Assert.Empty(result.Matches);
        Assert.Empty(result.Latest);
    }

    /// <summary>The previous implementation of the tool, copied verbatim: a fresh prefix copy per bar and a second detection for the final bar.</summary>
    private static (List<(int Index, DateTime Date, CandlePatternType Pattern)> Matches, List<string> Latest) ReferenceScan(
        List<CandleData> loaded, int windowBars, bool excludePatternBars, bool requireTrend)
    {
        var patternMatches = new List<(int Index, DateTime Date, CandlePatternType Pattern)>();
        int startIdx = Math.Max(0, loaded.Count - windowBars);

        for (int i = startIdx; i < loaded.Count; i++)
        {
            var slice = loaded.GetRange(0, i + 1);
            var detected = CandlePatternDetector.DetectPatterns(slice, excludePatternBars, requireTrend);
            foreach (var p in detected)
            {
                if (p != CandlePatternType.None)
                {
                    patternMatches.Add((i, loaded[i].Timestamp, p));
                }
            }
        }

        var latestDetected = CandlePatternDetector.DetectPatterns(loaded, excludePatternBars, requireTrend)
            .Where(p => p != CandlePatternType.None)
            .Select(p => p.ToString())
            .ToList();
        return (patternMatches, latestDetected);
    }

    /// <summary>
    /// Random OHLC bars on a coarse price grid, so equalities (doji, matching highs/lows, gaps) occur. Bars switch between
    /// small-body, large-body and doji regimes to reach single- and multi-candle patterns.
    /// </summary>
    private static List<CandleData> RandomSeries(Random random, int count)
    {
        var bars = new List<CandleData>(count);
        var date = new DateTime(2020, 1, 6);
        decimal close = 100m;
        for (int i = 0; i < count; i++)
        {
            decimal open = random.Next(5) == 0 ? close + Steps(random, -6, 6) : close;
            int kind = random.Next(6);
            decimal body = kind switch
            {
                0 => 0m,                          // doji
                1 => Steps(random, 1, 3),         // small
                2 or 3 => Steps(random, 4, 15),   // large
                _ => Steps(random, 1, 8),
            };
            if (random.Next(2) == 0)
            {
                body = -body;
            }

            close = Math.Max(PriceStep * 20, open + body);
            decimal high = Math.Max(open, close) + Steps(random, 0, 6);
            decimal low = Math.Max(PriceStep, Math.Min(open, close) - Steps(random, 0, 6));
            bars.Add(new CandleData(date.AddDays(i), open, high, low, close, 1000 + random.Next(500)));
        }

        return bars;
    }

    private static decimal Steps(Random random, int minInclusive, int maxInclusive) => random.Next(minInclusive, maxInclusive + 1) * PriceStep;
}
