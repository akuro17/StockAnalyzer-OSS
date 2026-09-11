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

    private static List<CandleData> CreateCandles4(
        decimal p3Open, decimal p3High, decimal p3Low, decimal p3Close,
        decimal p2Open, decimal p2High, decimal p2Low, decimal p2Close,
        decimal p1Open, decimal p1High, decimal p1Low, decimal p1Close,
        decimal cOpen, decimal cHigh, decimal cLow, decimal cClose,
        decimal avgBodyContext = 0)
    {
        var candles = new List<CandleData>();
        if (avgBodyContext > 0)
        {
            for (int i = 0; i < 14; i++)
                candles.Add(new CandleData(DateTime.Now.AddDays(-30 + i), 100m, 100m + avgBodyContext, 100m, 100m + avgBodyContext, 1000));
        }
        candles.Add(new CandleData(DateTime.Now.AddDays(-3), p3Open, p3High, p3Low, p3Close, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-2), p2Open, p2High, p2Low, p2Close, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-1), p1Open, p1High, p1Low, p1Close, 1000));
        candles.Add(new CandleData(DateTime.Now, cOpen, cHigh, cLow, cClose, 1000));
        return candles;
    }

    private static List<CandleData> CreateCandles5(
        decimal p4Open, decimal p4High, decimal p4Low, decimal p4Close,
        decimal p3Open, decimal p3High, decimal p3Low, decimal p3Close,
        decimal p2Open, decimal p2High, decimal p2Low, decimal p2Close,
        decimal p1Open, decimal p1High, decimal p1Low, decimal p1Close,
        decimal cOpen, decimal cHigh, decimal cLow, decimal cClose,
        decimal avgBodyContext = 0)
    {
        var candles = new List<CandleData>();
        if (avgBodyContext > 0)
        {
            for (int i = 0; i < 14; i++)
                candles.Add(new CandleData(DateTime.Now.AddDays(-30 + i), 100m, 100m + avgBodyContext, 100m, 100m + avgBodyContext, 1000));
        }
        candles.Add(new CandleData(DateTime.Now.AddDays(-4), p4Open, p4High, p4Low, p4Close, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-3), p3Open, p3High, p3Low, p3Close, 1000));
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
    // 家路を辿る鳩 (Homing Pigeon): prev Bear Large(110->100, avg=4), cur Bear Small(106->103, range=7). Open inside (106 < 110), Close inside (103 > 100).
    [InlineData(110, 112, 98, 100, 106, 108, 101, 103, 4, CandlePatternType.HomingPigeon)]
    // 強気蹴り上げ (Bullish Kicking): C1 Bear Marubozu (104->100), C2 Bull Marubozu (107->113), Gap (107-104=3.0 >= max(0.8, 2.2))
    [InlineData(104, 104, 100, 100, 107, 113, 107, 113, 4, CandlePatternType.BullishKicking)]
    // 弱気蹴り下げ (Bearish Kicking): C1 Bull Marubozu (100->104), C2 Bear Marubozu (97->91), Gap (100-97=3.0 >= max(0.8, 2.2))
    [InlineData(100, 104, 100, 104, 97, 97, 91, 91, 4, CandlePatternType.BearishKicking)]
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
    // 同値三羽烏 (Identical Three Crows): 3 Bear Large, opens at previous close, close lower.
    [InlineData(110, 110.1, 104, 104,  104.05, 104.1, 98, 98,  98.05, 98.1, 92, 92, 4, CandlePatternType.IdenticalThreeCrows)]
    // 上放れ並び赤 (Bullish Side-by-Side White Lines)
    [InlineData(100, 107, 99, 106,  109, 114, 108, 113,  109.2, 114, 108.5, 113.1, 4, CandlePatternType.BullishSideBySideWhiteLines)]
    // 下放れ並び赤 (Bearish Side-by-Side White Lines)
    [InlineData(110, 111, 103, 104,  100, 103, 99, 102,  100.1, 103, 99.5, 102.2, 4, CandlePatternType.BearishSideBySideWhiteLines)]
    // 強気棄て子 (Bullish Abandoned Baby): p2 BearLarge(110->100, low98), p1 DojiGapDown(95->95.05, high96 < low98), c BullLargeGapUp(97->108, low97 > high96, close108 > mid105)
    [InlineData(110, 112, 98, 100,  95, 96, 94, 95.05,  97, 109, 97, 108, 4, CandlePatternType.BullishAbandonedBaby)]
    // 弱気棄て子 (Bearish Abandoned Baby): p2 BullLarge(100->110, high112), p1 DojiGapUp(115->115.05, low114 > high112), c BearLargeGapDown(113->102, high113 < low114, close102 < mid105)
    [InlineData(100, 112, 98, 110,  115, 116, 114, 115.05,  113, 113, 101, 102, 4, CandlePatternType.BearishAbandonedBaby)]
    // 先詰まり (Advance Block): 3 Bull rising, diminishing bodies (6 > 4 > 3), upper shadow growing & long (u3=5 >= b3*0.5=1.5, u3>u1=1)
    [InlineData(100, 107, 99, 106,  103, 110, 102, 107,  105, 113, 104, 108, 4, CandlePatternType.AdvanceBlock)]
    // 思案 (Deliberation): 2 Bull Large (body=8, 8 >= 4*0.7=2.8), 3rd small star Bull (body=1 <= 8*0.4=3.2), open112 >= 112 - 4*0.15=111.4
    [InlineData(100, 109, 99, 108,  104, 113, 103, 112,  112, 114, 111.5, 113, 4, CandlePatternType.Deliberation)]
    // スティックサンドイッチ (Stick Sandwich): p2 Bear (108->100), p1 Bull (102->107), c Bear (106.8->100, close equal to p2.Close)
    [InlineData(108, 109, 99, 100,  102, 108, 101, 107,  106.8, 107, 99, 100, 4, CandlePatternType.StickSandwich)]
    // 上放れタスキ (Bullish Tasuki Gap): C1(100,107,99,106), C2(107,113,106,112), C3(111,112,106.2,106.5) [avgBody=4]
    [InlineData(100, 107, 99, 106,  107, 113, 106, 112,  111, 112, 106.2, 106.5, 4, CandlePatternType.BullishTasukiGap)]
    // 下放れタスキ (Bearish Tasuki Gap): C1(110,111,103,104), C2(103,104,97,98), C3(99,103.8,98.5,103.5) [avgBody=4]
    [InlineData(110, 111, 103, 104,  103, 104, 97, 98,  99, 103.8, 98.5, 103.5, 4, CandlePatternType.BearishTasukiGap)]
    // 上放れ窓埋め三法 (Bullish Gap Three Methods): C1(100,107,99,106), C2(107,113,106,112), C3(111,112,103.5,104) [avgBody=4]
    [InlineData(100, 107, 99, 106,  107, 113, 106, 112,  111, 112, 103.5, 104, 4, CandlePatternType.BullishGapThreeMethods)]
    // 下放れ窓埋め三法 (Bearish Gap Three Methods): C1(110,111,103,104), C2(103,104,97,98), C3(99,106.5,98.5,106) [avgBody=4]
    [InlineData(110, 111, 103, 104,  103, 104, 97, 98,  99, 106.5, 98.5, 106, 4, CandlePatternType.BearishGapThreeMethods)]
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

    [Theory]
    // 隠れ飲込み (Concealing Baby Swallow): 2 Bear Marubozu + 1 Bear with gap & upper shadow entering C2 + 1 Bear engulfing full range of C3
    [InlineData(120, 120, 112, 112,  112, 112, 104, 104,  98, 108, 95, 96,  110, 110, 94, 94,  4, CandlePatternType.ConcealingBabySwallow)]
    // 強気三手一撃 (Bullish Three-Line Strike): 3 Bull rising + 1 huge Strike Bear engulfing all 3
    [InlineData(100, 105, 99, 104,  103, 108, 102, 107,  106, 111, 105, 110,  110, 111, 97, 98,  4, CandlePatternType.BullishThreeLineStrike)]
    // 弱気三手一撃 (Bearish Three-Line Strike): 3 Bear falling + 1 huge Strike Bull engulfing all 3
    [InlineData(110, 111, 105, 106,  107, 108, 102, 103,  104, 105, 99, 100,  100, 113, 99, 112,  4, CandlePatternType.BearishThreeLineStrike)]
    public void DetectPattern_Continuation4Candle_IdentifiesCorrectPattern(
        decimal p3O, decimal p3H, decimal p3L, decimal p3C,
        decimal p2O, decimal p2H, decimal p2L, decimal p2C,
        decimal p1O, decimal p1H, decimal p1L, decimal p1C,
        decimal cO, decimal cH, decimal cL, decimal cC,
        decimal avgBody, CandlePatternType expected)
    {
        var candles = CreateCandles4(p3O, p3H, p3L, p3C, p2O, p2H, p2L, p2C, p1O, p1H, p1L, p1C, cO, cH, cL, cC, avgBody);
        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(expected, result);
    }

    [Theory]
    // マットホールド (Mat Hold): C1 Large Bull, C2 Gap Up Small, C3/C4 Small adjustments, C5 Bull breakout
    [InlineData(100, 111, 99, 110,  112, 113.5, 111.5, 113,  112.5, 113.5, 111.5, 111.5,  111.5, 112.5, 110.5, 111,  111, 117, 110.5, 116,  4, CandlePatternType.MatHold)]
    // 上昇三法 (Rising Three Methods): C1 Large Bull, C2/C3/C4 Small adjustments inside C1 range, C5 Bull breakout
    [InlineData(100, 111, 99, 110,  109, 110, 106, 107,  107, 108, 104, 105,  105, 106, 102, 103,  103, 113, 102.5, 112,  4, CandlePatternType.RisingThreeMethods)]
    // 下降三法 (Falling Three Methods): C1 Large Bear, C2/C3/C4 Small adjustments inside C1 range, C5 Bear breakdown
    [InlineData(110, 111, 99, 100,  101, 104.5, 100.5, 103,  103, 106.5, 102.5, 105,  105, 108.5, 104.5, 107,  107, 107.5, 97, 98,  4, CandlePatternType.FallingThreeMethods)]
    // 梯子底 (Ladder Bottom): 3 Bear falling (120->114, 116->108, 110->102), C4 Inverted Hammer (101->100, high105, u4=4 >= b4*1.0), C5 Breakout Bull (102->110 > C4.High 105)
    [InlineData(120, 121, 113, 114,  116, 117, 107, 108,  110, 111, 101, 102,  101, 105, 99.5, 100,  102, 111, 101.5, 110, 4, CandlePatternType.LadderBottom)]
    // 強気離脱 (Bullish Breakaway): C1(120,121,109,110), C2(108,108.2,104.5,105), C3(104.5,105.5,101.5,103), C4(103,103.8,100.8,102), C5(101.5,110,101,108.5) [avgBody=4]
    [InlineData(120, 121, 109, 110,  108, 108.2, 104.5, 105,  104.5, 105.5, 101.5, 103,  103, 103.8, 100.8, 102,  101.5, 110, 101, 108.5, 4, CandlePatternType.BullishBreakaway)]
    // 弱気離脱 (Bearish Breakaway): C1(100,111,99,110), C2(112,115.5,111.8,115), C3(115.5,118.5,114.5,117), C4(117,119.2,116.2,118), C5(118.5,119,110,111.5) [avgBody=4]
    [InlineData(100, 111, 99, 110,  112, 115.5, 111.8, 115,  115.5, 118.5, 114.5, 117,  117, 119.2, 116.2, 118,  118.5, 119, 110, 111.5, 4, CandlePatternType.BearishBreakaway)]
    public void DetectPattern_Continuation5Candle_IdentifiesCorrectPattern(
        decimal p4O, decimal p4H, decimal p4L, decimal p4C,
        decimal p3O, decimal p3H, decimal p3L, decimal p3C,
        decimal p2O, decimal p2H, decimal p2L, decimal p2C,
        decimal p1O, decimal p1H, decimal p1L, decimal p1C,
        decimal cO, decimal cH, decimal cL, decimal cC,
        decimal avgBody, CandlePatternType expected)
    {
        var candles = CreateCandles5(p4O, p4H, p4L, p4C, p3O, p3H, p3L, p3C, p2O, p2H, p2L, p2C, p1O, p1H, p1L, p1C, cO, cH, cL, cC, avgBody);
        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(expected, result);
    }

    #region Negative, Boundary and Precedence Tests

    [Fact]
    public void MatHold_SmallGap_Fails()
    {
        // C2 has tiny gap (Open = 110.1 <= C1.Close 110 + 4*0.10=110.4)
        var candles = CreateCandles5(
            100, 111, 99, 110,
            110.1m, 112m, 109.5m, 111m,
            111m, 112m, 109m, 110m,
            110m, 111.5m, 108m, 109m,
            109m, 117m, 108.5m, 116m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.MatHold, result);
    }

    [Fact]
    public void RisingThreeMethods_BreaksLow_Fails()
    {
        // C3.Low (98) breaks C1.Low (99)
        var candles = CreateCandles5(
            100, 111, 99, 110,
            109, 110, 106, 107,
            107, 108, 98, 105,
            105, 106, 102, 103,
            103, 113, 102.5m, 112,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.RisingThreeMethods, result);
    }

    [Fact]
    public void ThreeLineStrike_NoOverlap_Fails()
    {
        // C2 opens with a gap up above C1.Close (105 > 104) instead of inside C1 body
        var candles = CreateCandles4(
            100, 105, 99, 104,
            105, 108, 104.5m, 107,
            106, 111, 105, 110,
            110, 111, 97, 98,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishThreeLineStrike, result);
    }

    [Fact]
    public void ThreeLineStrike_StrikeNotEngulfing_Fails()
    {
        // C4.Close is 101 >= C1.Open (100), not fully engulfing
        var candles = CreateCandles4(
            100, 105, 99, 104,
            103, 108, 102, 107,
            106, 111, 105, 110,
            110, 111, 100, 101,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishThreeLineStrike, result);
    }

    [Fact]
    public void SideBySide_BodyMismatch_Fails()
    {
        // C3 body (116 - 109 = 7) deviates by > 30% from C2 body (113 - 109 = 4)
        var candles = CreateCandles3(
            100, 107, 99, 106,
            109, 114, 108, 113,
            109, 117, 108.5m, 116,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishSideBySideWhiteLines, result);
    }

    [Fact]
    public void InsufficientBars_Count4_For5Pattern()
    {
        // Only 4 candles provided (no context)
        var candles = new List<CandleData>
        {
            new(DateTime.Now.AddDays(-3), 100, 111, 99, 110, 1000),
            new(DateTime.Now.AddDays(-2), 109, 110, 106, 107, 1000),
            new(DateTime.Now.AddDays(-1), 107, 108, 104, 105, 1000),
            new(DateTime.Now, 103, 113, 102.5m, 112, 1000)
        };

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.RisingThreeMethods, result);
        Assert.NotEqual(CandlePatternType.MatHold, result);
        Assert.NotEqual(CandlePatternType.FallingThreeMethods, result);
    }

    [Fact]
    public void ZeroVolatility_AllIdentical_ReturnsFourPriceDoji()
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < 15; i++)
        {
            candles.Add(new CandleData(DateTime.Now.AddDays(-15 + i), 100m, 100m, 100m, 100m, 1000));
        }

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.FourPriceDoji, result);
    }

    [Fact]
    public void Precedence_Strike_Vs_Engulfing()
    {
        // 4-candle BullishThreeLineStrike: 3 Bull + 1 Bear engulfing
        var candles = CreateCandles4(
            100, 105, 99, 104,
            103, 108, 102, 107,
            106, 111, 105, 110,
            110, 111, 97, 98,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.BullishThreeLineStrike, result);
    }

    [Fact]
    public void Precedence_MatHold_Vs_RisingThree()
    {
        // 5-candle MatHold: C2 has gap up
        var candles = CreateCandles5(
            100, 111, 99, 110,
            112, 113.5m, 111.5m, 113,
            112.5m, 113.5m, 111.5m, 111.5m,
            111.5m, 112.5m, 110.5m, 111,
            111, 117, 110.5m, 116,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.MatHold, result);
    }

    [Fact]
    public void IsLarge_ZeroRangeOrBody_ReturnsFalse()
    {
        var zeroCandle = new CandleData(DateTime.Now, 100m, 100m, 100m, 100m, 1000);
        Assert.False(CandlePatternDetector.IsLarge(zeroCandle, avgBody: 2m, atr: 3m));
        Assert.False(CandlePatternDetector.IsLarge(zeroCandle, avgBody: 0m, atr: 0m));
    }

    [Fact]
    public void IsSmall_ZeroRangeOrBody_ReturnsTrue()
    {
        var zeroCandle = new CandleData(DateTime.Now, 100m, 100m, 100m, 100m, 1000);
        Assert.True(CandlePatternDetector.IsSmall(zeroCandle, avgBody: 2m, atr: 3m));
        Assert.True(CandlePatternDetector.IsSmall(zeroCandle, avgBody: 0m, atr: 0m));
    }

    [Fact]
    public void IsLarge_AtrFallback_IdentifiesLargeCandle()
    {
        // When avgBody is 0 (e.g. no prior body context), but ATR is positive
        var largeCandle = new CandleData(DateTime.Now, 100m, 120m, 100m, 120m, 1000); // Body=20, Range=20
        Assert.True(CandlePatternDetector.IsLarge(largeCandle, avgBody: 0m, atr: 25m)); // Body 20 >= 25 * 0.50 = 12.5
    }

    [Fact]
    public void BullishThreeLineStrike_ExactDominanceRatio_Passes()
    {
        // Prior 3 bodies: 4 + 4 + 4 = 12.
        // Strike body required: >= 12 * 0.85 = 10.2, and >= 4 * 1.5 = 6.0
        // C4: Open=112, Close=99 -> Body = 13 >= 10.2
        var candles = CreateCandles4(
            100, 105, 99, 104,
            103, 108, 102, 107,
            106, 111, 105, 110,
            112, 112, 98, 99,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.BullishThreeLineStrike, result);
    }

    [Fact]
    public void AbandonedBaby_WickOverlap_Fails_ReturnsMorningStar()
    {
        // C2.High (98) is NOT < C1.Low (98) (wick touches/overlaps C1 low)
        var candles = CreateCandles3(
            110, 112, 98, 100,
            95, 98, 94, 95.05m,
            97, 109, 97, 108,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishAbandonedBaby, result);
        Assert.Equal(CandlePatternType.MorningStar, result);
    }

    [Fact]
    public void AdvanceBlock_BodyNotDecreasing_Fails()
    {
        // Body3 (108-103=5) > Body2 (107-103=4) (bodies not decreasing)
        var candles = CreateCandles3(
            100, 107, 99, 106,
            103, 110, 102, 107,
            103, 113, 102, 108,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.AdvanceBlock, result);
    }

    [Fact]
    public void Deliberation_C3Bearish_Fails()
    {
        // C3 is Bearish (113 -> 112)
        var candles = CreateCandles3(
            100, 109, 99, 108,
            104, 113, 103, 112,
            113, 114, 111.5m, 112,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.Deliberation, result);
    }

    [Fact]
    public void StickSandwich_CloseMismatch_Fails()
    {
        // C3.Close is 101.5, deviating from C1.Close (100) by 1.5 > max(4*0.05, 100*0.002)=0.20
        var candles = CreateCandles3(
            108, 109, 99, 100,
            102, 108, 101, 107,
            106.8m, 107, 99, 101.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.StickSandwich, result);
    }

    [Fact]
    public void LadderBottom_C4NoWick_Fails()
    {
        // C4 has no upper/lower wick (Marubozu-like: Open=101, Close=100, High=101, Low=100)
        var candles = CreateCandles5(
            120, 121, 113, 114,
            113, 114, 107, 108,
            107, 108, 101, 102,
            101, 101, 100, 100,
            102, 111, 101.5m, 110,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.LadderBottom, result);
    }

    [Fact]
    public void HomingPigeon_C2Bullish_ReturnsBullishHarami()
    {
        // C2 is Bullish (102 -> 105), which is BullishHarami, NOT HomingPigeon
        var candles = CreateCandles2(
            110, 112, 98, 100,
            102, 108, 100, 105,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.HomingPigeon, result);
        Assert.Equal(CandlePatternType.BullishHarami, result);
    }

    [Fact]
    public void Precedence_AbandonedBaby_Vs_MorningStar()
    {
        // Fully gapped Abandoned Baby should have precedence over MorningStar
        var candles = CreateCandles3(
            110, 112, 98, 100,
            95, 96, 94, 95.05m,
            97, 109, 97, 108,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.BullishAbandonedBaby, result);
    }

    [Fact]
    public void Deliberation_WithTinyBearishDoji_Passes()
    {
        // C3 is a tiny bearish Doji: Open=112.02, Close=112.00, High=114, Low=111.5 -> Body=0.02 <= Range(2.5)*0.05=0.125
        var candles = CreateCandles3(
            100, 109, 99, 108,
            104, 113, 103, 112,
            112.02m, 114, 111.5m, 112.00m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.Deliberation, result);
    }

    [Fact]
    public void AdvanceBlock_MicroReduction_Fails()
    {
        // Body1 = 10 (100->110), Body2 = 9.9 (104->113.9), Body3 = 9.8 (107->116.8)
        // Monotonically decreasing (10 > 9.9 > 9.8), but Body3 (9.8) > Body1 (10) * 0.85 = 8.5 -> Fails 15% reduction requirement
        var candles = CreateCandles3(
            100, 111, 99, 110,
            104, 116, 103, 113.9m,
            107, 122, 106, 116.8m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.AdvanceBlock, result);
    }

    [Fact]
    public void LadderBottom_WithoutLowerLow_Fails()
    {
        // C3.Low (108) is >= C2.Low (107), violating C1.Low > C2.Low > C3.Low
        var candles = CreateCandles5(
            120, 121, 113, 114,
            116, 117, 107, 108,
            110, 111, 108, 102,
            101, 105, 99.5m, 100,
            102, 111, 101.5m, 110,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.LadderBottom, result);
    }

    [Fact]
    public void Tasuki_vs_GapThreeMethods_ExactBoundary()
    {
        // C1(100, 107, 99, 106), C2(107, 113, 106, 112), C3(111, 112, 105.5, 106)
        // C3.Close == C1.Close (106) -> Exactly touches C1.Close, should be BullishGapThreeMethods, NOT BullishTasukiGap
        var candles = CreateCandles3(
            100, 107, 99, 106,
            107, 113, 106, 112,
            111, 112, 105.5m, 106,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.BullishGapThreeMethods, result);
        Assert.NotEqual(CandlePatternType.BullishTasukiGap, result);
    }

    [Fact]
    public void Breakaway_CloseExceedsGap_Fails()
    {
        // C1(120->110), C2(108->105), C3(105->103), C4(103.5->102), C5(102.5->110.5 > C1.Close 110)
        var candles = CreateCandles5(
            120, 121, 109, 110,
            108, 109, 104.5m, 105,
            105, 106, 102.5m, 103,
            103.5m, 104, 101.5m, 102,
            102.5m, 111, 102, 110.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishBreakaway, result);
    }

    [Fact]
    public void Breakaway_IntermediateHighNotBroken_Fails()
    {
        // C2 High = 109, C5.Close = 108 <= 109
        var candles = CreateCandles5(
            120, 121, 109, 110,
            108, 109, 104.5m, 105,
            105, 106, 102.5m, 103,
            103.5m, 104, 101.5m, 102,
            102.5m, 109, 102, 108,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishBreakaway, result);
    }

    [Fact]
    public void AvgBodyZero_ReturnsFalse()
    {
        // When candles list is empty or null, returns None
        Assert.Equal(CandlePatternType.None, CandlePatternDetector.DetectPattern(new List<CandleData>()));
    }

    [Fact]
    public void InsufficientCount_ReturnsFalse()
    {
        // Less than 5 candles (e.g. 4 candles) cannot detect Breakaway
        var candles = CreateCandles4(
            120, 121, 109, 110,
            108, 109, 104.5m, 105,
            105, 106, 102.5m, 103,
            102.5m, 110, 102, 108.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishBreakaway, result);
        Assert.NotEqual(CandlePatternType.BearishBreakaway, result);
    }

    [Fact]
    public void Tasuki_C3OpenBelowC2Open_Fails()
    {
        // C1(100->106), C2(107->112), C3(106.8 < C2.Open 107 -> 106.5)
        // With strict lower bound (no -tol), opening below C2.Open must be rejected
        var candles = CreateCandles3(
            100, 107, 99, 106,
            107, 113, 106, 112,
            106.8m, 107, 106.2m, 106.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishTasukiGap, result);
    }

    [Fact]
    public void Tasuki_C3OpenAtC2Open_Passes()
    {
        // C1(100->106), C2(107->112), C3(107.0 == C2.Open 107 -> 106.5)
        // Opening exactly at C2.Open is allowed
        var candles = CreateCandles3(
            100, 107, 99, 106,
            107, 113, 106, 112,
            107.0m, 107.5m, 106.2m, 106.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.BullishTasukiGap, result);
    }

    [Fact]
    public void GapThreeMethods_C3OpenAboveLimit_Fails()
    {
        // C1(100->106), C2(107->112), C3(113.5 > C2.Close 112 + tol 0.8 = 112.8 -> 104)
        // Opening above C2.Close + tolerance must be rejected
        var candles = CreateCandles3(
            100, 107, 99, 106,
            107, 113, 106, 112,
            113.5m, 114, 103.5m, 104,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishGapThreeMethods, result);
    }

    [Fact]
    public void Breakaway_EarlyGapBreach_Fails()
    {
        // C3.Close = 108.5 >= C2.Open (108) -> Premature breach of gap before C5 must be rejected
        var candles = CreateCandles5(
            120, 121, 109, 110,
            108, 109, 104.5m, 105,
            105, 109, 102.5m, 108.5m,
            103.5m, 104, 101.5m, 102,
            102.5m, 110, 102, 108.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishBreakaway, result);
    }

    [Fact]
    public void Breakaway_BearishEarlyGapBreach_Fails()
    {
        // C3.Close = 111.5 <= C2.Open (112) -> Premature breach of gap before C5 must be rejected
        var candles = CreateCandles5(
            100, 111, 99, 110,
            112, 115.5m, 111.8m, 115,
            115.5m, 116, 111.0m, 111.5m,
            117, 119.2m, 116.2m, 118,
            118.5m, 119, 110, 111.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BearishBreakaway, result);
    }

    [Fact]
    public void BullishKicking_SmallGap_Fails()
    {
        // C2.Low (104.5) - C1.High (104) = 0.5 < max(4*0.20=0.8, atr*0.50=2.0)
        var candles = CreateCandles2(
            104, 104, 100, 100,
            104.5m, 108.5m, 104.5m, 108.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishKicking, result);
    }

    [Fact]
    public void BearishKicking_SmallGap_Fails()
    {
        // C1.Low (100) - C2.High (99.5) = 0.5 < 2.0
        var candles = CreateCandles2(
            100, 104, 100, 104,
            99.5m, 99.5m, 95.5m, 95.5m,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BearishKicking, result);
    }

    [Fact]
    public void Kicking_WithLargeShadow_Fails()
    {
        // C1 has lower shadow = 1.0 (range=5, shadow ratio = 20% > 5%)
        var candles = CreateCandles2(
            104, 104, 99, 100,
            106, 110, 106, 110,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.BullishKicking, result);
    }

    [Fact]
    public void ConcealingBabySwallow_C3HighDeepPenetration_Passes()
    {
        // C3.High (113) > C2.Open (112) -> Deep penetration of upper shadow exceeds C2 top, standard TA-Lib/Nison allows
        var candles = CreateCandles4(
            120, 120, 112, 112,
            112, 112, 104, 104,
            98, 113, 95, 96,
            115, 115, 94, 94,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.ConcealingBabySwallow, result);
    }

    [Fact]
    public void ConcealingBabySwallow_C2OpenInsideC1Body_Passes()
    {
        // C2.Open (116) <= C1.Open (120) and C2.Close (104) < C1.Close (112) -> Standard consecutive falling marubozu
        var candles = CreateCandles4(
            120, 120, 112, 112,
            116, 116, 104, 104,
            98, 108, 95, 96,
            110, 110, 94, 94,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.ConcealingBabySwallow, result);
    }

    [Fact]
    public void ConcealingBabySwallow_C3HighBelowC2Close_Fails()
    {
        // C3.High (103) <= C2.Close (104) -> Does not enter C2 body
        var candles = CreateCandles4(
            120, 120, 112, 112,
            112, 112, 104, 104,
            98, 103, 95, 96,
            105, 105, 94, 94,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.ConcealingBabySwallow, result);
    }

    [Fact]
    public void ConcealingBabySwallow_C4NotFullEngulfing_Fails()
    {
        // C4.Open (107) <= C3.High (108) -> Does not engulf C3 top wick
        var candles = CreateCandles4(
            120, 120, 112, 112,
            112, 112, 104, 104,
            98, 108, 95, 96,
            107, 107, 94, 94,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.ConcealingBabySwallow, result);
    }

    [Fact]
    public void IdenticalThreeCrows_vs_ThreeBlackCrows_Precedence()
    {
        // Exactly at previous close: should be IdenticalThreeCrows, NOT ThreeBlackCrows
        var candles = CreateCandles3(
            110, 110.1m, 104, 104,
            104.05m, 104.1m, 98, 98,
            98.05m, 98.1m, 92, 92,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.Equal(CandlePatternType.IdenticalThreeCrows, result);
        Assert.NotEqual(CandlePatternType.ThreeBlackCrows, result);
    }

    [Fact]
    public void IdenticalThreeCrows_OpenInsideBody_ReturnsThreeBlackCrows()
    {
        // C2.Open (108) is well inside C1 body (110->104), far beyond tolerance
        var candles = CreateCandles3(
            110, 110.1m, 104, 104,
            108m, 108.1m, 98, 98,
            98.05m, 98.1m, 92, 92,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.IdenticalThreeCrows, result);
        Assert.Equal(CandlePatternType.ThreeBlackCrows, result);
    }

    [Fact]
    public void IdenticalThreeCrows_LongLowerShadow_Fails()
    {
        // C3 has long lower shadow: Low=88, Close=92 (shadow=4 > Body(6.05)*0.15 = 0.9075)
        var candles = CreateCandles3(
            110, 110.1m, 104, 104,
            104.05m, 104.1m, 98, 98,
            98.05m, 98.1m, 88, 92,
            avgBodyContext: 4);

        var result = CandlePatternDetector.DetectPattern(candles);
        Assert.NotEqual(CandlePatternType.IdenticalThreeCrows, result);
    }

    #endregion
}

