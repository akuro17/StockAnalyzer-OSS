using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Analysis;

public class CandlePatternDetectorTests
{
    private static List<CandleData> CreateCandles(decimal open, decimal high, decimal low, decimal close, decimal avgBodyContext = 0)
    {
        var candles = new List<CandleData>();
        
        // Add context candles to establish Average Body if requested.
        if (avgBodyContext > 0)
        {
            for (int i = 0; i < 14; i++)
            {
                // Create dummy candles with actual body size = avgBodyContext. Base price is 10m to avoid triggering multi-candle patterns with the target candle at 100m.
                candles.Add(new CandleData(DateTime.Now.AddDays(-15 + i), 10m, 10m + avgBodyContext, 10m, 10m + avgBodyContext, 1000));
            }
        }

        // Add the target candle
        candles.Add(new CandleData(DateTime.Now, open, high, low, close, 1000));
        return candles;
    }

    private static List<CandleData> CreateCandles2(
        decimal p1Open, decimal p1High, decimal p1Low, decimal p1Close,
        decimal cOpen, decimal cHigh, decimal cLow, decimal cClose, 
        decimal avgBodyContext = 0)
    {
        var candles = new List<CandleData>();
        if (avgBodyContext > 0)
        {
            for (int i = 0; i < 14; i++)
                candles.Add(new CandleData(DateTime.Now.AddDays(-20 + i), 100m, 100m + avgBodyContext, 100m, 100m + avgBodyContext, 1000));
        }
        candles.Add(new CandleData(DateTime.Now.AddDays(-1), p1Open, p1High, p1Low, p1Close, 1000));
        candles.Add(new CandleData(DateTime.Now, cOpen, cHigh, cLow, cClose, 1000));
        return candles;
    }

    private static List<CandleData> CreateCandles3(
        decimal p2Open, decimal p2High, decimal p2Low, decimal p2Close,
        decimal p1Open, decimal p1High, decimal p1Low, decimal p1Close,
        decimal cOpen, decimal cHigh, decimal cLow, decimal cClose, 
        decimal avgBodyContext = 0)
    {
        var candles = new List<CandleData>();
        if (avgBodyContext > 0)
        {
            for (int i = 0; i < 14; i++)
                candles.Add(new CandleData(DateTime.Now.AddDays(-20 + i), 100m, 100m + avgBodyContext, 100m, 100m + avgBodyContext, 1000));
        }
        candles.Add(new CandleData(DateTime.Now.AddDays(-2), p2Open, p2High, p2Low, p2Close, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-1), p1Open, p1High, p1Low, p1Close, 1000));
        candles.Add(new CandleData(DateTime.Now, cOpen, cHigh, cLow, cClose, 1000));
        return candles;
    }

    [Theory]
    // 陽線の坊主系 (Bullish Marubozu)
    // AvgBody = 2, CurrentBody = 4 (100 to 104) is > avgBody*1.5. Range = 4. Body = 4 >= Range*0.6
    [InlineData(100, 104, 100, 104, 2, CandlePatternType.BullishMarubozu)] 
    // 大引坊主 (Closing Marubozu) - No upper shadow, has lower shadow (Low = 98)
    [InlineData(100, 104, 98, 104, 2, CandlePatternType.BullishClosingMarubozu)]
    // 寄付坊主 (Opening Marubozu) - Has upper shadow (High = 106), no lower shadow
    [InlineData(100, 106, 100, 104, 2, CandlePatternType.BullishOpeningMarubozu)]

    // 陰線の坊主系 (Bearish Marubozu)
    [InlineData(104, 104, 100, 100, 2, CandlePatternType.BearishMarubozu)]
    // 大引坊主 - No lower shadow, has upper shadow (High = 106)
    [InlineData(104, 106, 100, 100, 2, CandlePatternType.BearishClosingMarubozu)]
    // 寄付坊主 - Has lower shadow (Low = 98), no upper shadow
    [InlineData(104, 104, 98, 100, 2, CandlePatternType.BearishOpeningMarubozu)]

    // コマ (Spinning Top) - Small body, has both shadows
    // AvgBody = 4, CurrentBody = 1. High = 103, Low = 98. 
    [InlineData(100, 103, 98, 101, 4, CandlePatternType.BullishSpinningTop)]
    [InlineData(101, 103, 98, 100, 4, CandlePatternType.BearishSpinningTop)]

    // カラカサ (Umbrella) - Small body at top, long lower shadow, no upper shadow
    // Body = 1 (100-101), Lower Shadow = 4 (96-100), Upper Shadow = 0
    [InlineData(100, 101, 96, 101, 4, CandlePatternType.BullishUmbrella)]
    [InlineData(101, 101, 96, 100, 4, CandlePatternType.BearishUmbrella)]

    // トンカチ (Inverted Umbrella) - Small body at bottom, long upper shadow, no lower shadow
    // Body = 1 (100-101), Lower Shadow = 0, Upper Shadow = 4 (101-105)
    [InlineData(100, 105, 100, 101, 4, CandlePatternType.BullishInvertedUmbrella)]
    [InlineData(101, 105, 100, 100, 4, CandlePatternType.BearishInvertedUmbrella)]

    // 同時線 (Doji)
    // 十字線 (Cross Doji) - Open == Close, both shadows
    [InlineData(100, 102, 98, 100, 2, CandlePatternType.CrossDoji)]
    // トンボ (Dragonfly Doji) - Open == Close == High, long lower shadow
    [InlineData(100, 100, 95, 100, 2, CandlePatternType.DragonflyDoji)]
    // トウバ (Gravestone Doji) - Open == Close == Low, long upper shadow
    [InlineData(100, 105, 100, 100, 2, CandlePatternType.GravestoneDoji)]
    // 四値同時 (Four-Price Doji) - Open == High == Low == Close
    [InlineData(100, 100, 100, 100, 2, CandlePatternType.FourPriceDoji)]

    // 判定不能 (None) - Body is too large for spinning top, but too small for marubozu.
    // Body = 2, Avg = 2. Range = 6 (Shadows are Large).
    [InlineData(100, 103, 97, 102, 2, CandlePatternType.None)]

    public void DetectPattern_IdentifiesCorrectPattern(decimal open, decimal high, decimal low, decimal close, decimal avgBody, CandlePatternType expected)
    {
        // Arrange
        var candles = CreateCandles(open, high, low, close, avgBody);

        // Act
        var result = CandlePatternDetector.DetectPattern(candles);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    // 強気包み足 (Bullish Engulfing): prev Bear(105->100), cur Bull(98->107). Body 9 > 5. Open <= PrevClose, Close >= PrevOpen.
    [InlineData(105, 106, 99, 100, 98, 108, 97, 107, 4, CandlePatternType.BullishEngulfing)]
    // 弱気包み足 (Bearish Engulfing): prev Bull(100->105), cur Bear(107->98).
    [InlineData(100, 106, 99, 105, 107, 108, 97, 98, 4, CandlePatternType.BearishEngulfing)]
    // 強気はらみ (Bullish Harami): prev Bear Large(110->100, avg=4), cur Bull Small(102->105). curRange must be >= body*2
    [InlineData(110, 112, 98, 100, 102, 108, 100, 105, 4, CandlePatternType.BullishHarami)]
    // 弱気はらみ (Bearish Harami): prev Bull Large(100->110, avg=4), cur Bear Small(108->105). curRange must be >= body*2
    [InlineData(100, 112, 98, 110, 108, 110, 102, 105, 4, CandlePatternType.BearishHarami)]
    // 貫き線 (Piercing Line): prev Bear Large(110->100), cur Bull Open<PrevLow(98), Close>Mid(105), Close<=PrevOpen(110). Open=97, Close=106
    [InlineData(110, 112, 98, 100, 97, 108, 96, 106, 4, CandlePatternType.PiercingLine)]
    // かぶせ線 (Dark Cloud Cover): prev Bull Large(100->110), cur Bear Open>PrevHigh(112), Close<Mid(105), Close>=PrevOpen(100). Open=113, Close=104
    [InlineData(100, 112, 98, 110, 113, 115, 103, 104, 4, CandlePatternType.DarkCloudCover)]
    public void DetectPattern_DoubleCandle_IdentifiesCorrectPattern(
        decimal pOpen, decimal pHigh, decimal pLow, decimal pClose,
        decimal cOpen, decimal cHigh, decimal cLow, decimal cClose,
        decimal avgBody, CandlePatternType expected)
    {
        var candles = CreateCandles2(pOpen, pHigh, pLow, pClose, cOpen, cHigh, cLow, cClose, avgBody);
        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(expected, result);
    }

    [Theory]
    // 明けの明星 (Morning Star): p2 BearLarge(110->100), p1 SmallGapDown(97->98, high99 < p2Close100), c BullLarge(100->108, close>p2Mid105)
    [InlineData(110, 112, 98, 100,  97, 99, 96, 98,  100, 109, 99, 108, 4, CandlePatternType.MorningStar)]
    // 宵の明星 (Evening Star): p2 BullLarge(100->110), p1 SmallGapUp(113->112, low111 > p2Close110), c BearLarge(110->102, close<p2Mid105)
    [InlineData(100, 112, 98, 110,  113, 115, 111, 112,  110, 111, 101, 102, 4, CandlePatternType.EveningStar)]
    // 赤三兵 (Three White Soldiers): 3 Bull Large, open within prev body, close higher.
    // p2:100->105, p1:103->108, c:106->111
    [InlineData(100, 106, 99, 105,  103, 109, 102, 108,  106, 112, 105, 111, 2, CandlePatternType.ThreeWhiteSoldiers)]
    // 黒三兵 (Three Black Crows): 3 Bear Large, open within prev body, close lower.
    // p2:111->106, p1:108->103, c:105->100
    [InlineData(111, 112, 105, 106,  108, 109, 102, 103,  105, 106, 99, 100, 2, CandlePatternType.ThreeBlackCrows)]
    public void DetectPattern_TripleCandle_IdentifiesCorrectPattern(
        decimal p2O, decimal p2H, decimal p2L, decimal p2C,
        decimal p1O, decimal p1H, decimal p1L, decimal p1C,
        decimal cO, decimal cH, decimal cL, decimal cC,
        decimal avgBody, CandlePatternType expected)
    {
        var candles = CreateCandles3(p2O, p2H, p2L, p2C, p1O, p1H, p1L, p1C, cO, cH, cL, cC, avgBody);
        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(expected, result);
    }
}
