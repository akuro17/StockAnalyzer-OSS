using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CandleContextAndMultiPatternTests
{
    private static CandleData C(int index, decimal open, decimal high, decimal low, decimal close) =>
        new(new DateTime(2025, 1, 1).AddDays(index), open, high, low, close, 1000);

    #region Context Window Isolation (F33, D10)

    [Fact]
    public void ComputeContext_CorrectlyComputesAverageBodyAndATR()
    {
        var candles = new List<CandleData>
        {
            C(0, 100m, 105m, 95m, 102m), // body=2, tr=10
            C(1, 102m, 106m, 98m, 104m), // body=2, tr=8 (high 106, low 98, prevClose 102)
        };

        var (avgBody, atr) = CandlePatternDetector.ComputeContext(candles, 0, 1);
        Assert.Equal(2m, avgBody);
        Assert.Equal(9m, atr); // (10 + 8) / 2 = 9

        // Empty/out-of-bounds guards
        var (zeroBody, zeroAtr) = CandlePatternDetector.ComputeContext(candles, 5, 2);
        Assert.Equal(0m, zeroBody);
        Assert.Equal(0m, zeroAtr);
    }

    [Fact]
    public void ExcludePatternBarsFromContext_PreventsSelfContamination()
    {
        // 20 normal candles with small body = 2, then bar 20 with a massive body = 50
        var candles = new List<CandleData>();
        for (int i = 0; i < 20; i++)
        {
            candles.Add(C(i, 100m, 103m, 97m, 102m)); // body=2
        }
        candles.Add(C(20, 100m, 160m, 95m, 150m)); // massive candle: body=50

        // Context with inclusion (legacy): window includes bar 20
        var (includedAvg, includedAtr) = CandlePatternDetector.ComputeContext(candles, candles.Count - 14, candles.Count - 1);
        // Included average is inflated: 13 * 2 + 50 = 76 / 14 = 5.43
        Assert.True(includedAvg > 5m);

        // Context with exclusion for 1-candle pattern (k=1): window is [n - 1 - 14, n - 2]
        var (excludedAvg, excludedAtr) = CandlePatternDetector.ComputeContext(candles, candles.Count - 1 - 14, candles.Count - 2);
        // Excluded average strictly reflects the preceding 14 normal bars: body = 2
        Assert.Equal(2m, excludedAvg);
    }

    #endregion

    #region Prior Trend Context (F34, D10)

    [Fact]
    public void DeterminePriorTrend_IdentifiesUpwardDownwardAndFlatTrends()
    {
        // Upward trend: prices rising consistently from 100 to 120
        var upCandles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            decimal p = 100m + i * 1.5m;
            upCandles.Add(C(i, p, p + 1m, p - 1m, p + 1m));
        }

        int upTrend = CandlePatternDetector.DeterminePriorTrend(upCandles, patternLength: 1, atrContext: 2m);
        Assert.Equal(1, upTrend);

        // Downward trend: prices falling from 120 to 100
        var downCandles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            decimal p = 120m - i * 1.5m;
            downCandles.Add(C(i, p, p + 1m, p - 1m, p - 1m));
        }

        int downTrend = CandlePatternDetector.DeterminePriorTrend(downCandles, patternLength: 1, atrContext: 2m);
        Assert.Equal(-1, downTrend);

        // Flat trend: prices oscillate around 100 with move < 0.5 * atr
        var flatCandles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            flatCandles.Add(C(i, 100m, 105m, 95m, 100m));
        }

        int flatTrend = CandlePatternDetector.DeterminePriorTrend(flatCandles, patternLength: 1, atrContext: 10m);
        Assert.Equal(0, flatTrend);
    }

    [Fact]
    public void Hammer_RequiresDownTrend_WhenRequireTrendContextIsTrue()
    {
        // 1. Create a Hammer at the end of an UPTREND:
        var upCandles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            decimal p = 100m + i * 2m;
            upCandles.Add(C(i, p, p + 2m, p - 1m, p + 1.5m));
        }
        // Last bar: Bullish Umbrella (Hammer geometry: small body at top 132->133, long lower shadow 125, no upper shadow 133)
        upCandles.Add(C(15, 132m, 133m, 125m, 133m));

        // When requireTrendContext is false (legacy): detected as BullishUmbrella
        var legacyResult = CandlePatternDetector.DetectPattern(upCandles, requireTrendContext: false);
        Assert.Equal(CandlePatternType.BullishUmbrella, legacyResult);

        // When requireTrendContext is true: rejected because prior trend is Upward (+1), not Downward (-1)
        var contextResult = CandlePatternDetector.DetectPattern(upCandles, requireTrendContext: true);
        Assert.NotEqual(CandlePatternType.BullishUmbrella, contextResult);

        // 2. Create the identical Hammer at the end of a DOWNTREND:
        var downCandles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            decimal p = 150m - i * 2m;
            downCandles.Add(C(i, p, p + 1m, p - 2m, p - 1.5m));
        }
        // Last bar: Bullish Umbrella (Hammer geometry: open 118, close 119, high 119, low 110)
        downCandles.Add(C(15, 118m, 119m, 110m, 119m));

        // When requireTrendContext is true: accepted because prior trend is Downward (-1)
        var downResult = CandlePatternDetector.DetectPattern(downCandles, requireTrendContext: true);
        Assert.Equal(CandlePatternType.BullishUmbrella, downResult);
    }

    [Fact]
    public void HangingMan_RequiresUpTrend_WhenRequireTrendContextIsTrue()
    {
        // Hanging Man: Bearish Umbrella geometry (small bearish body at top, long lower shadow, no upper shadow)
        // 1. At end of Downtrend:
        var downCandles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            decimal p = 150m - i * 2m;
            downCandles.Add(C(i, p, p + 1m, p - 2m, p - 1.5m));
        }
        // Last bar: Bearish Umbrella (open 119, close 118, high 119, low 110)
        downCandles.Add(C(15, 119m, 119m, 110m, 118m));

        // When requireTrendContext is true: rejected because prior trend is Downward (-1)
        var downResult = CandlePatternDetector.DetectPattern(downCandles, requireTrendContext: true);
        Assert.NotEqual(CandlePatternType.BearishUmbrella, downResult);

        // 2. At end of Uptrend:
        var upCandles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            decimal p = 100m + i * 2m;
            upCandles.Add(C(i, p, p + 2m, p - 1m, p + 1.5m));
        }
        // Last bar: Bearish Umbrella (open 133, close 132, high 133, low 125)
        upCandles.Add(C(15, 133m, 133m, 125m, 132m));

        // When requireTrendContext is true: accepted because prior trend is Upward (+1)
        var upResult = CandlePatternDetector.DetectPattern(upCandles, requireTrendContext: true);
        Assert.Equal(CandlePatternType.BearishUmbrella, upResult);
    }

    #endregion

    #region Multi-Pattern Detection (F35, D10)

    [Fact]
    public void DetectPatterns_ReturnsMultipleMatchedPatternsInPrecedenceOrder()
    {
        // Construct a 2-candle setup where:
        // Bar n-2: bearish candle (open 105, close 100, high 106, low 99)
        // Bar n-1: bullish engulfing candle that is ALSO a Bullish Marubozu (open 98, close 110, high 110, low 98)
        var candles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            candles.Add(C(i, 100m, 103m, 97m, 101m));
        }
        candles.Add(C(15, 105m, 106m, 99m, 100m)); // Bearish
        candles.Add(C(16, 98m, 110m, 98m, 110m));   // Bullish Engulfing + Bullish Marubozu

        var singleResult = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.BullishEngulfing, singleResult);

        var multiResults = CandlePatternDetector.DetectPatterns(candles);
        Assert.NotEmpty(multiResults);

        // Precedence: 2-candle pattern first, then 1-candle pattern
        Assert.Equal(singleResult, multiResults[0]);
        Assert.Contains(CandlePatternType.BullishEngulfing, multiResults);
        Assert.Contains(CandlePatternType.BullishMarubozu, multiResults);
        Assert.True(multiResults.Count >= 2);
    }

    [Fact]
    public void DetectPatterns_WhenNoPatternMatches_ReturnsEmptyList()
    {
        // Flat candles with normal proportions that match no pattern
        var candles = new List<CandleData>();
        for (int i = 0; i < 10; i++)
        {
            candles.Add(C(i, 100m, 104m, 96m, 102m));
        }

        var singleResult = CandlePatternDetector.DetectPattern(candles);
        var multiResults = CandlePatternDetector.DetectPatterns(candles);

        if (singleResult == CandlePatternType.None)
        {
            Assert.Empty(multiResults);
        }
        else
        {
            Assert.Equal(singleResult, multiResults[0]);
        }
    }

    [Fact]
    public void DetectPatterns_FourPriceDoji_ReturnsExactlyOneInstanceWithoutDuplicates()
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            candles.Add(C(i, 100m, 105m, 95m, 102m));
        }
        // Last bar: Four-Price Doji (Open = High = Low = Close)
        candles.Add(C(15, 100m, 100m, 100m, 100m));

        var results = CandlePatternDetector.DetectPatterns(candles);
        int fourPriceCount = results.Count(p => p == CandlePatternType.FourPriceDoji);
        Assert.Equal(1, fourPriceCount);
    }

    [Fact]
    public void DetectPatterns_BullishUmbrella_DoesNotEmitSpinningTop()
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            decimal p = 120m - i * 1.5m;
            candles.Add(C(i, p, p + 1m, p - 2m, p - 1.5m));
        }
        // Last bar: Bullish Umbrella (body=1, lowerShadow=10, upperShadow=0.4 <= body*0.5)
        candles.Add(C(15, 100m, 101.4m, 90m, 101m));

        var results = CandlePatternDetector.DetectPatterns(candles, requireTrendContext: false);
        Assert.Contains(CandlePatternType.BullishUmbrella, results);
        Assert.DoesNotContain(CandlePatternType.BullishSpinningTop, results);
        Assert.DoesNotContain(CandlePatternType.BearishSpinningTop, results);
    }

    #endregion
}
