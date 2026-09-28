using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Static utility class for mathematically detecting classical single and multi-candle patterns.
/// Detects classical reversal and continuation patterns considering local volatility context (Average Body Size & ATR).
/// </summary>
public static class CandlePatternDetector
{
    /// <summary>
    /// Evaluates the sequence of candles ending at the latest candle to determine its candlestick pattern.
    /// Uses previous candles to calculate average body size and ATR for contextual volatility normalization.
    /// Follows a strict descending window size precedence (5-candle -> 4-candle -> 3-candle -> 2-candle -> 1-candle).
    /// </summary>
    /// <param name="candles">Read-only list of historical candles, ending with the target candle.</param>
    /// <returns>The recognized <see cref="CandlePatternType"/>, or None if it doesn't match any clear pattern.</returns>
    public static CandlePatternType DetectPattern(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count == 0) return CandlePatternType.None;
        int n = candles.Count;
        var candle = candles[n - 1];

        // Context: Average body & ATR calculation (up to CandlePatternAtrPeriod periods)
        int period = Math.Min(ChartConstants.CandlePatternAtrPeriod, n);
        decimal sumBody = 0m;
        decimal sumTrueRange = 0m;
        for (int i = n - period; i < n; i++)
        {
            sumBody += Math.Abs(candles[i].Open - candles[i].Close);

            // True Range: max(H-L, |H-prevClose|, |L-prevClose|)
            decimal tr = candles[i].High - candles[i].Low;
            if (i > 0)
            {
                decimal prevClose = candles[i - 1].Close;
                tr = Math.Max(tr, Math.Max(
                    Math.Abs(candles[i].High - prevClose),
                    Math.Abs(candles[i].Low - prevClose)));
            }
            sumTrueRange += tr;
        }
        decimal avgBody = period > 0 ? (sumBody / period) : 0m;
        decimal atr = period > 0 ? (sumTrueRange / period) : 0m;

        // --- 1. 5本足パターン走査 (5-Candle Patterns) ---
        if (n >= 5)
        {
            if (IsMatHold(candles, avgBody, atr)) return CandlePatternType.MatHold;
            if (IsRisingThreeMethods(candles, avgBody, atr)) return CandlePatternType.RisingThreeMethods;
            if (IsFallingThreeMethods(candles, avgBody, atr)) return CandlePatternType.FallingThreeMethods;
            if (IsLadderBottom(candles, avgBody, atr)) return CandlePatternType.LadderBottom;
            if (IsBullishBreakaway(candles, avgBody, atr)) return CandlePatternType.BullishBreakaway;
            if (IsBearishBreakaway(candles, avgBody, atr)) return CandlePatternType.BearishBreakaway;
        }

        // --- 2. 4本足パターン走査 (4-Candle Continuation/Reversal Patterns) ---
        if (n >= 4)
        {
            if (IsConcealingBabySwallow(candles, avgBody, atr)) return CandlePatternType.ConcealingBabySwallow;
            if (IsBullishThreeLineStrike(candles, avgBody, atr)) return CandlePatternType.BullishThreeLineStrike;
            if (IsBearishThreeLineStrike(candles, avgBody, atr)) return CandlePatternType.BearishThreeLineStrike;
        }

        // --- 3. 3本足パターン走査 (3-Candle Patterns) ---
        if (n >= 3)
        {
            var prev2 = candles[n - 3];
            var prev1 = candles[n - 2];
            decimal prev2Body = Body(prev2);
            decimal prev1Body = Body(prev1);
            decimal prev1Range = Range(prev1);
            bool prev2IsLarge = prev2Body >= (avgBody * ChartConstants.CandlePatternLargeBodyRatio);
            decimal prev2Mid = (prev2.Open + prev2.Close) / 2m;
            decimal curBody = Body(candle);
            decimal curRange = Range(candle);
            bool curIsLarge = IsLarge(candle, avgBody, atr);

            // 強気棄て子 / 弱気棄て子 (Bullish / Bearish Abandoned Baby)
            if (IsBullishAbandonedBaby(prev2, prev1, candle, avgBody, atr)) return CandlePatternType.BullishAbandonedBaby;
            if (IsBearishAbandonedBaby(prev2, prev1, candle, avgBody, atr)) return CandlePatternType.BearishAbandonedBaby;

            // 明けの明星 (Morning Star)
            bool prev1IsSmall = prev1Body <= (avgBody * ChartConstants.CandlePatternSmallBodyRatio) && prev1Body <= (prev1Range * ChartConstants.CandlePatternSmallRangeRatio);
            bool gapDown = Math.Max(prev1.Open, prev1.Close) < prev2.Close;
            if (IsBear(prev2) && prev2IsLarge && prev1IsSmall && gapDown && IsBull(candle) && curIsLarge && candle.Close > prev2Mid)
                return CandlePatternType.MorningStar;

            // 宵の明星 (Evening Star)
            bool gapUp = Math.Min(prev1.Open, prev1.Close) > prev2.Close;
            if (IsBull(prev2) && prev2IsLarge && prev1IsSmall && gapUp && IsBear(candle) && curIsLarge && candle.Close < prev2Mid)
                return CandlePatternType.EveningStar;

            // 先詰まり (Advance Block)
            if (IsAdvanceBlock(prev2, prev1, candle, avgBody)) return CandlePatternType.AdvanceBlock;

            // 思案 (Deliberation)
            if (IsDeliberation(prev2, prev1, candle, avgBody, atr)) return CandlePatternType.Deliberation;

            // 同値三羽烏 (Identical Three Crows: 必ず黒三兵より前に評価)
            if (IsIdenticalThreeCrows(prev2, prev1, candle, avgBody)) return CandlePatternType.IdenticalThreeCrows;

            // 赤三兵 (Three White Soldiers)
            if (IsBull(prev2) && IsBull(prev1) && IsBull(candle))
            {
                bool prev1OpenInside = prev1.Open >= prev2.Open && prev1.Open <= prev2.Close;
                bool currOpenInside = candle.Open >= prev1.Open && candle.Open <= prev1.Close;
                if (prev1OpenInside && currOpenInside && prev1.Close > prev2.Close && candle.Close > prev1.Close)
                {
                    if (prev2Body > (avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio) &&
                        prev1Body > (avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio) &&
                        curBody > (avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio))
                        return CandlePatternType.ThreeWhiteSoldiers;
                }
            }

            // 黒三兵 (Three Black Crows)
            if (IsBear(prev2) && IsBear(prev1) && IsBear(candle))
            {
                bool prev1OpenInside = prev1.Open <= prev2.Open && prev1.Open >= prev2.Close;
                bool currOpenInside = candle.Open <= prev1.Open && candle.Open >= prev1.Close;
                if (prev1OpenInside && currOpenInside && prev1.Close < prev2.Close && candle.Close < prev1.Close)
                {
                    if (prev2Body > (avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio) &&
                        prev1Body > (avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio) &&
                        curBody > (avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio))
                        return CandlePatternType.ThreeBlackCrows;
                }
            }

            // スティックサンドイッチ (Stick Sandwich)
            if (IsStickSandwich(prev2, prev1, candle, avgBody)) return CandlePatternType.StickSandwich;

            // 上放れ窓埋め三法 / 下放れ窓埋め三法 (Gap Three Methods)
            if (IsBullishGapThreeMethods(prev2, prev1, candle, avgBody)) return CandlePatternType.BullishGapThreeMethods;
            if (IsBearishGapThreeMethods(prev2, prev1, candle, avgBody)) return CandlePatternType.BearishGapThreeMethods;

            // 上放れタスキ / 下放れタスキ (Tasuki Gap)
            if (IsBullishTasukiGap(prev2, prev1, candle, avgBody)) return CandlePatternType.BullishTasukiGap;
            if (IsBearishTasukiGap(prev2, prev1, candle, avgBody)) return CandlePatternType.BearishTasukiGap;

            // 上放れ並び赤 / 下放れ並び赤 (Side-by-Side White Lines)
            if (IsBullishSideBySideWhiteLines(candles, avgBody, atr)) return CandlePatternType.BullishSideBySideWhiteLines;
            if (IsBearishSideBySideWhiteLines(candles, avgBody, atr)) return CandlePatternType.BearishSideBySideWhiteLines;
        }

        // --- 4. 2本組パターン走査 (2-Candle Patterns) ---
        if (n >= 2)
        {
            var prev1 = candles[n - 2];
            decimal prev1Body = Body(prev1);
            bool prev1IsLarge = prev1Body >= (avgBody * ChartConstants.CandlePatternLargeBodyRatio);
            decimal prev1Mid = (prev1.Open + prev1.Close) / 2m;
            decimal curBody = Body(candle);
            bool curIsSmall = IsSmall(candle, avgBody, atr);

            // 強気蹴り上げ / 弱気蹴り下げ (Kicking Patterns: 2本足最上位で評価)
            if (IsBullishKicking(prev1, candle, avgBody, atr)) return CandlePatternType.BullishKicking;
            if (IsBearishKicking(prev1, candle, avgBody, atr)) return CandlePatternType.BearishKicking;

            // 強気包み足 (Bullish Engulfing)
            if (IsBear(prev1) && IsBull(candle) && candle.Open <= prev1.Close && candle.Close >= prev1.Open && curBody > prev1Body)
                return CandlePatternType.BullishEngulfing;

            // 弱気包み足 (Bearish Engulfing)
            if (IsBull(prev1) && IsBear(candle) && candle.Open >= prev1.Close && candle.Close <= prev1.Open && curBody > prev1Body)
                return CandlePatternType.BearishEngulfing;

            // 家路を辿る鳩 (Homing Pigeon: 陰線はらみ)
            if (IsHomingPigeon(prev1, candle, avgBody, atr)) return CandlePatternType.HomingPigeon;

            // 強気はらみ (Bullish Harami)
            if (IsBear(prev1) && prev1IsLarge && IsBull(candle) && curIsSmall && candle.Open >= prev1.Close && candle.Close <= prev1.Open)
                return CandlePatternType.BullishHarami;

            // 弱気はらみ (Bearish Harami)
            if (IsBull(prev1) && prev1IsLarge && IsBear(candle) && curIsSmall && candle.Open <= prev1.Close && candle.Close >= prev1.Open)
                return CandlePatternType.BearishHarami;

            // 貫き線 (Piercing Line)
            if (IsBear(prev1) && prev1IsLarge && IsBull(candle) && candle.Open < prev1.Low && candle.Close > prev1Mid && candle.Close <= prev1.Open)
                return CandlePatternType.PiercingLine;

            // かぶせ線 (Dark Cloud Cover)
            if (IsBull(prev1) && prev1IsLarge && IsBear(candle) && candle.Open > prev1.High && candle.Close < prev1Mid && candle.Close >= prev1.Open)
                return CandlePatternType.DarkCloudCover;
        }

        // --- 5. 1本足パターン走査 (Single Candle Patterns) ---
        decimal body = Body(candle);
        decimal range = Range(candle);
        decimal upperShadow = candle.High - Math.Max(candle.Open, candle.Close);
        decimal lowerShadow = Math.Min(candle.Open, candle.Close) - candle.Low;

        // Zero tolerance for wick visibility (5% of range)
        decimal zeroTolerance = range * 0.05m;
        if (range == 0) zeroTolerance = 0;

        bool isBullish = IsBull(candle);
        bool isBearish = IsBear(candle);
        bool isDoji = range > 0 ? (body <= range * 0.05m) : true;

        bool isLargeBody = IsLarge(candle, avgBody, atr);
        bool isSmallBody = IsSmall(candle, avgBody, atr);

        // Fallback for short histories or zero volatility
        if (avgBody == 0 || period < 5)
        {
            if (!isLargeBody && range > 0) isLargeBody = body >= (range * 0.8m);
            if (!isSmallBody && range > 0) isSmallBody = body <= (range * 0.4m);
        }

        // 四値同時 (Four-Price Doji)
        if (range == 0 || (body == 0 && upperShadow == 0 && lowerShadow == 0))
            return CandlePatternType.FourPriceDoji;

        // 同時線系 (Doji Patterns)
        if (isDoji)
        {
            bool noUpper = upperShadow <= zeroTolerance;
            bool noLower = lowerShadow <= zeroTolerance;

            if (noUpper && noLower) return CandlePatternType.FourPriceDoji;
            if (noUpper) return CandlePatternType.DragonflyDoji;
            if (noLower) return CandlePatternType.GravestoneDoji;
            return CandlePatternType.CrossDoji;
        }

        // 坊主系 (Marubozu Patterns)
        if (isLargeBody)
        {
            bool noUpper = upperShadow <= zeroTolerance;
            bool noLower = lowerShadow <= zeroTolerance;

            if (isBullish)
            {
                if (noUpper && noLower) return CandlePatternType.BullishMarubozu;
                if (noUpper && !noLower) return CandlePatternType.BullishClosingMarubozu;
                if (!noUpper && noLower) return CandlePatternType.BullishOpeningMarubozu;
            }
            else if (isBearish)
            {
                if (noUpper && noLower) return CandlePatternType.BearishMarubozu;
                if (!noUpper && noLower) return CandlePatternType.BearishClosingMarubozu;
                if (noUpper && !noLower) return CandlePatternType.BearishOpeningMarubozu;
            }
        }

        // カラカサ / トンカチ / コマ (Umbrella / Tonkachi / Spinning Top)
        if (isSmallBody && !isDoji)
        {
            bool hasLongLower = lowerShadow >= (body * 2.0m);
            bool hasLongUpper = upperShadow >= (body * 2.0m);

            bool noUpper = upperShadow <= zeroTolerance || upperShadow <= (body * 0.5m);
            bool noLower = lowerShadow <= zeroTolerance || lowerShadow <= (body * 0.5m);

            if (isBullish)
            {
                if (hasLongLower && noUpper) return CandlePatternType.BullishUmbrella;
                if (hasLongUpper && noLower) return CandlePatternType.BullishInvertedUmbrella;
            }
            else if (isBearish)
            {
                if (hasLongLower && noUpper) return CandlePatternType.BearishUmbrella;
                if (hasLongUpper && noLower) return CandlePatternType.BearishInvertedUmbrella;
            }

            // コマ (Spinning Top)
            if (upperShadow > zeroTolerance && lowerShadow > zeroTolerance)
            {
                if (isBullish) return CandlePatternType.BullishSpinningTop;
                if (isBearish) return CandlePatternType.BearishSpinningTop;
            }
        }

        return CandlePatternType.None;
    }

    #region Helper Primitives

    private static decimal Body(CandleData c) => Math.Abs(c.Open - c.Close);

    private static decimal Range(CandleData c) => c.High - c.Low;

    private static bool IsBull(CandleData c) => c.Close > c.Open;

    private static bool IsBear(CandleData c) => c.Close < c.Open;

    private static int BearCount(CandleData a, CandleData b, CandleData c) =>
        (IsBear(a) ? 1 : 0) + (IsBear(b) ? 1 : 0) + (IsBear(c) ? 1 : 0);

    private static int BullCount(CandleData a, CandleData b, CandleData c) =>
        (IsBull(a) ? 1 : 0) + (IsBull(b) ? 1 : 0) + (IsBull(c) ? 1 : 0);

    /// <summary>
    /// Determines whether the candle has a large body relative to recent volatility and range.
    /// Includes zero-degeneration guards.
    /// </summary>
    public static bool IsLarge(CandleData c, decimal avgBody, decimal atr)
    {
        decimal b = Math.Abs(c.Open - c.Close);
        decimal r = c.High - c.Low;
        if (b <= 0m || r <= 0m) return false;

        if (avgBody > 0m && b >= avgBody * ChartConstants.CandlePatternLargeBodyRatio && b >= r * ChartConstants.CandlePatternLargeRangeRatio)
            return true;

        if (atr > 0m && b >= atr * ChartConstants.CandlePatternLargeBodyAtrRatio && b >= r * ChartConstants.CandlePatternLargeRangeRatio)
            return true;

        return false;
    }

    /// <summary>
    /// Determines whether the candle has a small body relative to recent volatility and range.
    /// Includes zero-degeneration guards.
    /// </summary>
    public static bool IsSmall(CandleData c, decimal avgBody, decimal atr)
    {
        decimal b = Math.Abs(c.Open - c.Close);
        decimal r = c.High - c.Low;
        if (r <= 0m || b <= 0m) return true;

        if (avgBody > 0m)
            return b <= avgBody * ChartConstants.CandlePatternSmallBodyRatio && b <= r * ChartConstants.CandlePatternSmallRangeRatio;

        if (atr > 0m)
            return b <= atr * ChartConstants.CandlePatternSmallBodyAtrRatio && b <= r * ChartConstants.CandlePatternSmallRangeRatio;

        return false;
    }

    #endregion

    #region Continuation Pattern Methods (Zero-Allocation)

    /// <summary>
    /// マットホールド (Mat Hold): 5-candle strong bullish continuation.
    /// </summary>
    private static bool IsMatHold(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 5];
        var c2 = candles[n - 4];
        var c3 = candles[n - 3];
        var c4 = candles[n - 2];
        var c5 = candles[n - 1];

        // 1. C1 is Large Bullish
        if (!IsBull(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2 has clear Gap Up and is Small
        decimal gapMin = avgBody > 0m ? (avgBody * ChartConstants.CandlePatternGapMinRatio) : 0m;
        if (c2.Open <= c1.Close + gapMin || c2.Open <= c1.Close || !IsSmall(c2, avgBody, atr)) return false;

        // 3. C3, C4 are Small, and at least one adjustment bar is Bearish
        if (!IsSmall(c3, avgBody, atr) || !IsSmall(c4, avgBody, atr)) return false;
        if (!IsBear(c2) && !IsBear(c3) && !IsBear(c4)) return false;

        // 4. Shallow pullback range hold
        decimal minLow = Math.Min(c2.Low, Math.Min(c3.Low, c4.Low));
        if (minLow < c1.Open) return false;

        decimal minClose = Math.Min(c2.Close, Math.Min(c3.Close, c4.Close));
        decimal c1Mid = (c1.Open + c1.Close) / 2m;
        if (minClose <= c1Mid) return false;

        // 5. C5 is Bullish breakout
        if (!IsBull(c5)) return false;
        bool c5Strong = Body(c5) >= (avgBody * ChartConstants.CandlePatternBreakoutBodyMinRatio) || IsLarge(c5, avgBody, atr);
        if (!c5Strong) return false;

        decimal maxHigh = Math.Max(c1.High, Math.Max(c2.High, Math.Max(c3.High, c4.High)));
        if (c5.Open < c4.Close || c5.Close <= maxHigh) return false;

        return true;
    }

    /// <summary>
    /// 上昇三法 (Rising Three Methods): 5-candle bullish continuation.
    /// </summary>
    private static bool IsRisingThreeMethods(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 5];
        var c2 = candles[n - 4];
        var c3 = candles[n - 3];
        var c4 = candles[n - 2];
        var c5 = candles[n - 1];

        // 1. C1 is Large Bullish
        if (!IsBull(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2, C3, C4 are 3 consecutive Small candles
        if (!IsSmall(c2, avgBody, atr) || !IsSmall(c3, avgBody, atr) || !IsSmall(c4, avgBody, atr)) return false;

        // 3. Adjustment trend: Bear count >= 2 or downward slope
        if (BearCount(c2, c3, c4) < 2 && c4.Close >= c2.Close) return false;

        // 4. Contained within C1 range
        decimal maxAdjustmentHigh = Math.Max(c2.High, Math.Max(c3.High, c4.High));
        decimal minAdjustmentLow = Math.Min(c2.Low, Math.Min(c3.Low, c4.Low));
        if (maxAdjustmentHigh > c1.High || minAdjustmentLow < c1.Low) return false;

        decimal minBodyPoint = Math.Min(
            Math.Min(Math.Min(c2.Open, c2.Close), Math.Min(c3.Open, c3.Close)),
            Math.Min(c4.Open, c4.Close));
        if (minBodyPoint < c1.Open) return false;

        // 5. C5 is Bullish breakout
        if (!IsBull(c5)) return false;
        bool c5Strong = Body(c5) >= (avgBody * ChartConstants.CandlePatternBreakoutBodyMinRatio) || IsLarge(c5, avgBody, atr);
        if (!c5Strong) return false;

        if (c5.Open < c4.Close || c5.Close <= c1.High) return false;

        return true;
    }

    /// <summary>
    /// 下降三法 (Falling Three Methods): 5-candle bearish continuation.
    /// </summary>
    private static bool IsFallingThreeMethods(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 5];
        var c2 = candles[n - 4];
        var c3 = candles[n - 3];
        var c4 = candles[n - 2];
        var c5 = candles[n - 1];

        // 1. C1 is Large Bearish
        if (!IsBear(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2, C3, C4 are 3 consecutive Small candles
        if (!IsSmall(c2, avgBody, atr) || !IsSmall(c3, avgBody, atr) || !IsSmall(c4, avgBody, atr)) return false;

        // 3. Retracement trend: Bull count >= 2 or upward slope
        if (BullCount(c2, c3, c4) < 2 && c4.Close <= c2.Close) return false;

        // 4. Contained within C1 range
        decimal maxAdjustmentHigh = Math.Max(c2.High, Math.Max(c3.High, c4.High));
        decimal minAdjustmentLow = Math.Min(c2.Low, Math.Min(c3.Low, c4.Low));
        if (maxAdjustmentHigh > c1.High || minAdjustmentLow < c1.Low) return false;

        decimal maxBodyPoint = Math.Max(
            Math.Max(Math.Max(c2.Open, c2.Close), Math.Max(c3.Open, c3.Close)),
            Math.Max(c4.Open, c4.Close));
        if (maxBodyPoint > c1.Open) return false;

        // 5. C5 is Bearish breakdown
        if (!IsBear(c5)) return false;
        bool c5Strong = Body(c5) >= (avgBody * ChartConstants.CandlePatternBreakoutBodyMinRatio) || IsLarge(c5, avgBody, atr);
        if (!c5Strong) return false;

        if (c5.Open > c4.Close || c5.Close >= c1.Low) return false;

        return true;
    }

    /// <summary>
    /// 強気三手一撃 (Bullish Three-Line Strike): 4-candle bullish continuation.
    /// </summary>
    private static bool IsBullishThreeLineStrike(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 4];
        var c2 = candles[n - 3];
        var c3 = candles[n - 2];
        var c4 = candles[n - 1];

        // 1. C1, C2, C3 are 3 consecutive rising Bullish candles (Three White Soldiers structure)
        if (!IsBull(c1) || !IsBull(c2) || !IsBull(c3)) return false;
        if (c1.Close >= c2.Close || c2.Close >= c3.Close) return false;
        if (c2.Open < c1.Open || c2.Open > c1.Close) return false;
        if (c3.Open < c2.Open || c3.Open > c2.Close) return false;

        decimal noiseMin = avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio;
        if (Body(c1) < noiseMin || Body(c2) < noiseMin || Body(c3) < noiseMin) return false;

        // 2. C4 is huge Strike Bearish candle engulfing all 3 prior candles
        if (!IsBear(c4)) return false;

        decimal openTolerance = avgBody > 0m ? (avgBody * ChartConstants.CandlePatternStrikeOpenToleranceRatio) : 0m;
        if (c4.Open < c3.Close - openTolerance) return false;
        if (c4.Close >= c1.Open) return false;

        decimal total3Body = Body(c1) + Body(c2) + Body(c3);
        decimal minStrikeBody = Math.Max(
            total3Body * ChartConstants.CandlePatternStrikeBodyDominanceRatio,
            avgBody * ChartConstants.CandlePatternLargeBodyRatio);
        if (Body(c4) < minStrikeBody) return false;

        return true;
    }

    /// <summary>
    /// 弱気三手一撃 (Bearish Three-Line Strike): 4-candle bearish continuation.
    /// </summary>
    private static bool IsBearishThreeLineStrike(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 4];
        var c2 = candles[n - 3];
        var c3 = candles[n - 2];
        var c4 = candles[n - 1];

        // 1. C1, C2, C3 are 3 consecutive falling Bearish candles (Three Black Crows structure)
        if (!IsBear(c1) || !IsBear(c2) || !IsBear(c3)) return false;
        if (c1.Close <= c2.Close || c2.Close <= c3.Close) return false;
        if (c2.Open > c1.Open || c2.Open < c1.Close) return false;
        if (c3.Open > c2.Open || c3.Open < c2.Close) return false;

        decimal noiseMin = avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio;
        if (Body(c1) < noiseMin || Body(c2) < noiseMin || Body(c3) < noiseMin) return false;

        // 2. C4 is huge Strike Bullish candle engulfing all 3 prior candles
        if (!IsBull(c4)) return false;

        decimal openTolerance = avgBody > 0m ? (avgBody * ChartConstants.CandlePatternStrikeOpenToleranceRatio) : 0m;
        if (c4.Open > c3.Close + openTolerance) return false;
        if (c4.Close <= c1.Open) return false;

        decimal total3Body = Body(c1) + Body(c2) + Body(c3);
        decimal minStrikeBody = Math.Max(
            total3Body * ChartConstants.CandlePatternStrikeBodyDominanceRatio,
            avgBody * ChartConstants.CandlePatternLargeBodyRatio);
        if (Body(c4) < minStrikeBody) return false;

        return true;
    }

    /// <summary>
    /// 上放れ並び赤 (Bullish Side-by-Side White Lines): 3-candle bullish continuation.
    /// </summary>
    private static bool IsBullishSideBySideWhiteLines(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 3];
        var c2 = candles[n - 2];
        var c3 = candles[n - 1];

        // 1. C1 is normal/large Bullish
        if (!IsBull(c1) || Body(c1) < avgBody * ChartConstants.CandlePatternSmallBodyRatio) return false;

        // 2. C2 is Gap Up Bullish
        decimal gapMin = avgBody > 0m ? (avgBody * ChartConstants.CandlePatternGapMinRatio) : 0m;
        if (!IsBull(c2) || c2.Open <= c1.Close + gapMin || c2.Open <= c1.Close) return false;

        // 3. C3 is similar Bullish (Side-by-Side)
        if (!IsBull(c3)) return false;

        decimal openTolerance = Math.Max(
            avgBody * ChartConstants.CandlePatternSimilarOpenToleranceRatio,
            Body(c2) * ChartConstants.CandlePatternSimilarOpenToleranceRatio);
        if (Math.Abs(c3.Open - c2.Open) > openTolerance) return false;

        decimal bodyTolerance = Body(c2) * ChartConstants.CandlePatternSimilarBodyToleranceRatio;
        if (Math.Abs(Body(c3) - Body(c2)) > bodyTolerance) return false;

        if (Math.Min(c3.Open, c3.Close) <= c1.Close) return false;

        return true;
    }

    /// <summary>
    /// 下放れ並び赤 (Bearish Side-by-Side White Lines): 3-candle bearish continuation.
    /// </summary>
    private static bool IsBearishSideBySideWhiteLines(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 3];
        var c2 = candles[n - 2];
        var c3 = candles[n - 1];

        // 1. C1 is normal/large Bearish
        if (!IsBear(c1) || Body(c1) < avgBody * ChartConstants.CandlePatternSmallBodyRatio) return false;

        // 2. C2 is Gap Down Bullish (early short cover)
        decimal gapMin = avgBody > 0m ? (avgBody * ChartConstants.CandlePatternGapMinRatio) : 0m;
        if (!IsBull(c2) || c2.Close >= c1.Close - gapMin || c2.Close >= c1.Close) return false;

        // 3. C3 is similar Bullish (gap fill failure)
        if (!IsBull(c3)) return false;

        decimal openTolerance = Math.Max(
            avgBody * ChartConstants.CandlePatternSimilarOpenToleranceRatio,
            Body(c2) * ChartConstants.CandlePatternSimilarOpenToleranceRatio);
        if (Math.Abs(c3.Open - c2.Open) > openTolerance) return false;

        decimal bodyTolerance = Body(c2) * ChartConstants.CandlePatternSimilarBodyToleranceRatio;
        if (Math.Abs(Body(c3) - Body(c2)) > bodyTolerance) return false;

        if (Math.Max(c3.Open, c3.Close) >= c1.Close) return false;

        return true;
    }

    #endregion

    #region Advanced Reversal Pattern Methods (Zero-Allocation)

    /// <summary>
    /// 強気棄て子 (Bullish Abandoned Baby): 3-candle extremely powerful bullish bottom reversal.
    /// </summary>
    private static bool IsBullishAbandonedBaby(CandleData c1, CandleData c2, CandleData c3, decimal avgBody, decimal atr)
    {
        // 1. C1: 大陰線
        if (!IsBear(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2: 完全孤立した十字線 (Doji) かつ C2.High < C1.Low (完全な下方窓)
        decimal c2Range = Range(c2);
        bool c2IsDoji = c2Range > 0m ? (Body(c2) <= c2Range * 0.05m) : true;
        if (!c2IsDoji) return false;
        if (c2.High >= c1.Low) return false;

        // 3. C3: 窓上に開いた大陽線 (C3.Low > C2.High) かつ C1実体中央値超え
        if (!IsBull(c3) || !IsLarge(c3, avgBody, atr)) return false;
        if (c3.Low <= c2.High) return false;

        decimal c1Mid = (c1.Open + c1.Close) / 2m;
        if (c3.Close <= c1Mid) return false;

        return true;
    }

    /// <summary>
    /// 弱気棄て子 (Bearish Abandoned Baby): 3-candle extremely powerful bearish top reversal.
    /// </summary>
    private static bool IsBearishAbandonedBaby(CandleData c1, CandleData c2, CandleData c3, decimal avgBody, decimal atr)
    {
        // 1. C1: 大陽線
        if (!IsBull(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2: 完全孤立した十字線 (Doji) かつ C2.Low > C1.High (完全な上方窓)
        decimal c2Range = Range(c2);
        bool c2IsDoji = c2Range > 0m ? (Body(c2) <= c2Range * 0.05m) : true;
        if (!c2IsDoji) return false;
        if (c2.Low <= c1.High) return false;

        // 3. C3: 窓下に開いた大陰線 (C3.High < C2.Low) かつ C1実体中央値未満
        if (!IsBear(c3) || !IsLarge(c3, avgBody, atr)) return false;
        if (c3.High >= c2.Low) return false;

        decimal c1Mid = (c1.Open + c1.Close) / 2m;
        if (c3.Close >= c1Mid) return false;

        return true;
    }

    /// <summary>
    /// 先詰まり (Advance Block): 3-candle bearish warning pattern with diminishing bodies.
    /// </summary>
    private static bool IsAdvanceBlock(CandleData c1, CandleData c2, CandleData c3, decimal avgBody)
    {
        // 1. 3本連続陽線
        if (!IsBull(c1) || !IsBull(c2) || !IsBull(c3)) return false;

        // 2. 高値・終値の連続切り上げ
        if (c1.Close >= c2.Close || c2.Close >= c3.Close) return false;
        if (c1.High >= c2.High || c2.High >= c3.High) return false;

        // 3. 寄付きが前足実体内 (閉区間)
        if (c2.Open < c1.Open || c2.Open > c1.Close) return false;
        if (c3.Open < c2.Open || c3.Open > c2.Close) return false;

        // 4. ノイズ除外
        decimal noiseMin = avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio;
        if (Body(c1) < noiseMin) return false;

        // 5. 実体の単調縮小および累積縮小率 (15%以上の縮小)
        decimal b1 = Body(c1);
        decimal b2 = Body(c2);
        decimal b3 = Body(c3);
        bool bodyDecreasing = (b1 > b2) && (b2 > b3) && (b3 <= b1 * ChartConstants.CandlePatternAdvanceBlockBodyReductionRatio);

        // 6. 上ヒゲ伸長
        decimal u1 = c1.High - c1.Close;
        decimal u2 = c2.High - c2.Close;
        decimal u3 = c3.High - c3.Close;
        bool longUpperShadow = (u2 >= b2 * ChartConstants.CandlePatternAdvanceBlockShadowRatio) ||
                               (u3 >= b3 * ChartConstants.CandlePatternAdvanceBlockShadowRatio);
        bool shadowGrowing = u3 > u1;

        return bodyDecreasing && longUpperShadow && shadowGrowing;
    }

    /// <summary>
    /// 思案 (Deliberation): 3-candle bearish warning pattern with stalled star candle.
    /// </summary>
    private static bool IsDeliberation(CandleData c1, CandleData c2, CandleData c3, decimal avgBody, decimal atr)
    {
        // 1. C1, C2: 連続する正常〜大陽線
        if (!IsBull(c1) || !IsBull(c2)) return false;
        decimal minMainBody = avgBody * ChartConstants.CandlePatternDeliberationMainBodyRatio;
        if (Body(c1) < minMainBody || Body(c2) < minMainBody) return false;

        // 2. C2: 高値更新かつ寄付きはC1実体内
        if (c2.Close <= c1.Close) return false;
        if (c2.Open < c1.Open || c2.Open > c1.Close) return false;

        // 3. C3: 陽線または十字線 (微小陰線のDojiも許容)
        decimal c3Range = Range(c3);
        bool c3IsDoji = c3Range > 0m && (Body(c3) <= c3Range * 0.05m);
        if (c3.Close < c3.Open && !c3IsDoji) return false;

        // 4. C3: 小実体またはDoji
        decimal b2 = Body(c2);
        decimal b3 = Body(c3);
        bool isSmallStar = (b3 <= b2 * ChartConstants.CandlePatternDeliberationStarBodyRatio) &&
                           (b3 <= avgBody * ChartConstants.CandlePatternSmallBodyRatio);
        if (!isSmallStar) return false;

        // 5. C3: 高値圏寄付き
        decimal openBuffer = avgBody * ChartConstants.CandlePatternDeliberationOpenBufferRatio;
        if (c3.Open < c2.Close - openBuffer) return false;

        return true;
    }

    /// <summary>
    /// スティックサンドイッチ (Stick Sandwich): 3-candle bullish reversal pattern with equal close support.
    /// </summary>
    private static bool IsStickSandwich(CandleData c1, CandleData c2, CandleData c3, decimal avgBody)
    {
        // 1. C1: 陰線
        if (!IsBear(c1) || Body(c1) < avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio) return false;

        // 2. C2: 陽線 (高値引け)
        if (!IsBull(c2)) return false;
        if (c2.Open <= c1.Close || c2.Close <= c1.Close) return false;

        // 3. C3: 陰線 (反落)
        if (!IsBear(c3)) return false;
        if (c3.Open < c2.Close - (avgBody * ChartConstants.CandlePatternStickSandwichOpenBufferRatio)) return false;

        // 4. C1とC3の終値が同値水準
        decimal tolerance = Math.Max(
            avgBody * ChartConstants.CandlePatternPriceEqualityToleranceRatio,
            c1.Close * ChartConstants.CandlePatternPriceEqualityAbsoluteMinRatio);
        if (Math.Abs(c3.Close - c1.Close) > tolerance) return false;
        if (c3.Close > c2.Open) return false;

        return true;
    }

    /// <summary>
    /// 梯子底 (Ladder Bottom): 5-candle major bullish bottom reversal pattern.
    /// </summary>
    private static bool IsLadderBottom(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        var c1 = candles[n - 5];
        var c2 = candles[n - 4];
        var c3 = candles[n - 3];
        var c4 = candles[n - 2];
        var c5 = candles[n - 1];

        // 1. C1, C2, C3: 3本連続陰線かつ安値・終値の連続切り下げ
        if (!IsBear(c1) || !IsBear(c2) || !IsBear(c3)) return false;
        if (c1.Close <= c2.Close || c2.Close <= c3.Close) return false;
        if (c1.Low <= c2.Low || c2.Low <= c3.Low) return false;
        if (c2.Open > c1.Open || c2.Open < c1.Close) return false;
        if (c3.Open > c2.Open || c3.Open < c2.Close) return false;

        decimal noiseMin = avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio;
        if (Body(c1) < noiseMin || Body(c2) < noiseMin || Body(c3) < noiseMin) return false;

        // 2. C4: 下落局面の極致でヒゲ（上ヒゲまたは下ヒゲ）を持つ足
        if (c4.Open > c3.Open && c4.Close > c3.Open) return false;
        decimal u4 = c4.High - Math.Max(c4.Open, c4.Close);
        decimal l4 = Math.Min(c4.Open, c4.Close) - c4.Low;
        decimal b4 = Body(c4);
        bool hasLongWick = (u4 >= b4 * ChartConstants.CandlePatternLadderShadowRatio) ||
                           (l4 >= b4 * ChartConstants.CandlePatternLadderShadowRatio);
        if (!hasLongWick && b4 > 0m) return false;

        // 3. C5: 窓開け反発大陽線
        if (!IsBull(c5)) return false;
        if (c5.Open <= c4.Close && c5.Open <= c4.Open) return false;
        if (c5.Close <= c4.High) return false;

        bool c5Strong = Body(c5) >= (avgBody * ChartConstants.CandlePatternBreakoutBodyMinRatio);
        if (!c5Strong) return false;

        return true;
    }

    /// <summary>
    /// 家路を辿る鳩 (Homing Pigeon): 2-candle bullish reversal pattern (bearish harami).
    /// </summary>
    private static bool IsHomingPigeon(CandleData c1, CandleData c2, decimal avgBody, decimal atr)
    {
        // 1. C1: 大陰線
        if (!IsBear(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2: 小陰線
        if (!IsBear(c2) || !IsSmall(c2, avgBody, atr)) return false;

        // 3. C2の実体がC1の実体内に完全に収まる (陰線はらみ)
        if (c2.Open >= c1.Open || c2.Close <= c1.Close) return false;

        return true;
    }

    #endregion

    #region Gap Pattern Methods (Prompt 70-3 Zero-Allocation)

    /// <summary>
    /// 上放れタスキ (Bullish Tasuki Gap / Upside Tasuki Gap): 3-candle bullish continuation pattern.
    /// </summary>
    private static bool IsBullishTasukiGap(CandleData c1, CandleData c2, CandleData c3, decimal avgBody)
    {
        if (avgBody <= 0m) return false;

        // 1. C1: 陽線 (ノイズ除外)
        if (!IsBull(c1) || Body(c1) < avgBody * ChartConstants.CandlePatternSmallBodyRatio) return false;

        // 2. C2: 上放れ陽線 (窓開け)
        decimal gapMin = avgBody * ChartConstants.CandlePatternGapMinRatio;
        if (!IsBull(c2) || c2.Open <= c1.Close + gapMin) return false;

        // 3. C3: 陰線
        if (!IsBear(c3)) return false;

        // 4. C3: 始値はC2の実体内〜上端近傍 (下限バッファ撤廃、上限バッファのみ許容)
        decimal openTolerance = avgBody * ChartConstants.CandlePatternTasukiOpenToleranceRatio;
        if (c3.Open < c2.Open || c3.Open > c2.Close + openTolerance) return false;

        // 5. C3: 終値はC2の始値を下回るが、C1の終値より上で引ける (窓を埋めきらない)
        // 境界: c3.Close == c1.Close は GapThreeMethods に倒すため > を使用
        if (c3.Close >= c2.Open || c3.Close <= c1.Close) return false;

        return true;
    }

    /// <summary>
    /// 下放れタスキ (Bearish Tasuki Gap / Downside Tasuki Gap): 3-candle bearish continuation pattern.
    /// </summary>
    private static bool IsBearishTasukiGap(CandleData c1, CandleData c2, CandleData c3, decimal avgBody)
    {
        if (avgBody <= 0m) return false;

        // 1. C1: 陰線 (ノイズ除外)
        if (!IsBear(c1) || Body(c1) < avgBody * ChartConstants.CandlePatternSmallBodyRatio) return false;

        // 2. C2: 下放れ陰線 (窓開け)
        decimal gapMin = avgBody * ChartConstants.CandlePatternGapMinRatio;
        if (!IsBear(c2) || c2.Open >= c1.Close - gapMin) return false;

        // 3. C3: 陽線
        if (!IsBull(c3)) return false;

        // 4. C3: 始値はC2の実体内〜下端近傍 (上限バッファ撤廃、下限バッファのみ許容)
        decimal openTolerance = avgBody * ChartConstants.CandlePatternTasukiOpenToleranceRatio;
        if (c3.Open > c2.Open || c3.Open < c2.Close - openTolerance) return false;

        // 5. C3: 終値はC2の始値を上回るが、C1の終値より下で引ける (窓を埋めきらない)
        // 境界: c3.Close == c1.Close は GapThreeMethods に倒すため < を使用
        if (c3.Close <= c2.Open || c3.Close >= c1.Close) return false;

        return true;
    }

    /// <summary>
    /// 上放れ窓埋め三法 (Bullish Gap Three Methods / Upside Gap Three Methods): 3-candle bullish continuation pattern.
    /// </summary>
    private static bool IsBullishGapThreeMethods(CandleData c1, CandleData c2, CandleData c3, decimal avgBody)
    {
        if (avgBody <= 0m) return false;

        // 1. C1: 陽線 (ノイズ除外)
        if (!IsBull(c1) || Body(c1) < avgBody * ChartConstants.CandlePatternSmallBodyRatio) return false;

        // 2. C2: 上放れ陽線 (窓開け)
        decimal gapMin = avgBody * ChartConstants.CandlePatternGapMinRatio;
        if (!IsBull(c2) || c2.Open <= c1.Close + gapMin) return false;

        // 3. C3: 陰線
        if (!IsBear(c3)) return false;

        // 4. C3: 始値はC2の実体内〜上端近傍 (タスキと対称に閉区間 [C2.Open, C2.Close + tolerance] に統一)
        decimal openTolerance = avgBody * ChartConstants.CandlePatternTasukiOpenToleranceRatio;
        if (c3.Open < c2.Open || c3.Open > c2.Close + openTolerance) return false;

        // 5. C3: 終値は窓を完全に埋め、C1の実体内で引ける (境界値 c1.Close を含む)
        if (c3.Close > c1.Close || c3.Close < c1.Open) return false;

        return true;
    }

    /// <summary>
    /// 下放れ窓埋め三法 (Bearish Gap Three Methods / Downside Gap Three Methods): 3-candle bearish continuation pattern.
    /// </summary>
    private static bool IsBearishGapThreeMethods(CandleData c1, CandleData c2, CandleData c3, decimal avgBody)
    {
        if (avgBody <= 0m) return false;

        // 1. C1: 陰線 (ノイズ除外)
        if (!IsBear(c1) || Body(c1) < avgBody * ChartConstants.CandlePatternSmallBodyRatio) return false;

        // 2. C2: 下放れ陰線 (窓開け)
        decimal gapMin = avgBody * ChartConstants.CandlePatternGapMinRatio;
        if (!IsBear(c2) || c2.Open >= c1.Close - gapMin) return false;

        // 3. C3: 陽線
        if (!IsBull(c3)) return false;

        // 4. C3: 始値はC2の実体内〜下端近傍 (タスキと対称に閉区間 [C2.Close - tolerance, C2.Open] に統一)
        decimal openTolerance = avgBody * ChartConstants.CandlePatternTasukiOpenToleranceRatio;
        if (c3.Open > c2.Open || c3.Open < c2.Close - openTolerance) return false;

        // 5. C3: 終値は窓を完全に埋め、C1の実体内で引ける (境界値 c1.Close を含む)
        if (c3.Close < c1.Close || c3.Close > c1.Open) return false;

        return true;
    }

    /// <summary>
    /// 強気離脱 (Bullish Breakaway): 5-candle major bullish bottom reversal pattern.
    /// </summary>
    private static bool IsBullishBreakaway(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        if (n < 5 || avgBody <= 0m || atr <= 0m) return false;

        var c1 = candles[n - 5];
        var c2 = candles[n - 4];
        var c3 = candles[n - 3];
        var c4 = candles[n - 2];
        var c5 = candles[n - 1];

        // 1. C1: 大陰線
        if (!IsBear(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2: 窓開け下放れ陰線 (下落加速)
        decimal gapMin = avgBody * ChartConstants.CandlePatternGapMinRatio;
        if (!IsBear(c2) || c2.Open >= c1.Close - gapMin) return false;

        // 3. C3, C4: 小足 (保ち合いまたは下落継続傾向) かつ 窓への早期侵入を排除
        if (!IsSmall(c3, avgBody, atr) || !IsSmall(c4, avgBody, atr)) return false;
        if (Math.Max(c3.Close, c4.Close) >= c2.Open) return false; // C5以前の窓への逆行侵入を排除
        if (c4.Low > c3.Low && c4.Close > c2.Close) return false; // 明確な反発上昇継続を排除

        // 4. C5: 大陽線ブレイクアウト
        if (!IsBull(c5)) return false;
        bool c5Strong = Body(c5) >= (avgBody * ChartConstants.CandlePatternBreakawayBodyRatio) || IsLarge(c5, avgBody, atr);
        if (!c5Strong) return false;

        // 5. C5: C2〜C4の中間高値を上抜け、かつ C1とC2の窓領域 [C2.Open, C1.Close] の中に突入して引ける
        decimal intermediateHigh = Math.Max(c2.High, Math.Max(c3.High, c4.High));
        if (c5.Close <= intermediateHigh) return false;
        if (c5.Close <= c2.Open || c5.Close >= c1.Close) return false;

        return true;
    }

    /// <summary>
    /// 弱気離脱 (Bearish Breakaway): 5-candle major bearish top reversal pattern.
    /// </summary>
    private static bool IsBearishBreakaway(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        if (n < 5 || avgBody <= 0m || atr <= 0m) return false;

        var c1 = candles[n - 5];
        var c2 = candles[n - 4];
        var c3 = candles[n - 3];
        var c4 = candles[n - 2];
        var c5 = candles[n - 1];

        // 1. C1: 大陽線
        if (!IsBull(c1) || !IsLarge(c1, avgBody, atr)) return false;

        // 2. C2: 窓開け上放れ陽線 (上昇加速)
        decimal gapMin = avgBody * ChartConstants.CandlePatternGapMinRatio;
        if (!IsBull(c2) || c2.Open <= c1.Close + gapMin) return false;

        // 3. C3, C4: 小足 (保ち合いまたは上昇継続傾向) かつ 窓への早期侵入を排除
        if (!IsSmall(c3, avgBody, atr) || !IsSmall(c4, avgBody, atr)) return false;
        if (Math.Min(c3.Close, c4.Close) <= c2.Open) return false; // C5以前の窓への逆行侵入を排除
        if (c4.High < c3.High && c4.Close < c2.Close) return false; // 明確な反落下降継続を排除

        // 4. C5: 大陰線ブレイクダウン
        if (!IsBear(c5)) return false;
        bool c5Strong = Body(c5) >= (avgBody * ChartConstants.CandlePatternBreakawayBodyRatio) || IsLarge(c5, avgBody, atr);
        if (!c5Strong) return false;

        // 5. C5: C2〜C4の中間安値を下抜け、かつ C1とC2の窓領域 [C1.Close, C2.Open] の中に突入して引ける
        decimal intermediateLow = Math.Min(c2.Low, Math.Min(c3.Low, c4.Low));
        if (c5.Close >= intermediateLow) return false;
        if (c5.Close >= c2.Open || c5.Close <= c1.Close) return false;

        return true;
    }

    #endregion

    #region Exotic & Minor Pattern Methods (Prompt 70-4 Zero-Allocation)

    /// <summary>
    /// 丸坊主 (Marubozu) 判定: 実体がレンジの大半を占め、上下ヒゲがレンジの5%以下の大足。
    /// </summary>
    private static bool IsMarubozu(CandleData c, decimal avgBody, decimal atr)
    {
        decimal body = Body(c);
        decimal range = Range(c);
        if (range <= 0m || body <= 0m) return false; // ゼロ除算・Doji除外ガード

        if (!IsLarge(c, avgBody, atr)) return false;

        decimal upperShadow = c.High - Math.Max(c.Open, c.Close);
        decimal lowerShadow = Math.Min(c.Open, c.Close) - c.Low;
        decimal maxShadow = range * ChartConstants.CandlePatternMarubozuShadowMaxRatio;

        return upperShadow <= maxShadow && lowerShadow <= maxShadow;
    }

    /// <summary>
    /// 強気蹴り上げ (Bullish Kicking): 陰線丸坊主の翌日に上放れて陽線丸坊主が出現する極めて強力な強気反転シグナル。
    /// </summary>
    private static bool IsBullishKicking(CandleData c1, CandleData c2, decimal avgBody, decimal atr)
    {
        if (avgBody <= 0m && atr <= 0m) return false;

        // 1. C1: 陰線丸坊主, C2: 陽線丸坊主
        if (!IsBear(c1) || !IsBull(c2)) return false;
        if (!IsMarubozu(c1, avgBody, atr) || !IsMarubozu(c2, avgBody, atr)) return false;

        // 2. 窓開け (ヒゲ間ギャップ: C2.Low - C1.High >= gapMin)
        decimal gapMin = Math.Max(
            avgBody * ChartConstants.CandlePatternKickingGapBodyRatio,
            atr * ChartConstants.CandlePatternKickingGapAtrRatio);

        return (c2.Low - c1.High) >= gapMin;
    }

    /// <summary>
    /// 弱気蹴り下げ (Bearish Kicking): 陽線丸坊主の翌日に下放れて陰線丸坊主が出現する極めて強力な弱気反転シグナル。
    /// </summary>
    private static bool IsBearishKicking(CandleData c1, CandleData c2, decimal avgBody, decimal atr)
    {
        if (avgBody <= 0m && atr <= 0m) return false;

        // 1. C1: 陽線丸坊主, C2: 陰線丸坊主
        if (!IsBull(c1) || !IsBear(c2)) return false;
        if (!IsMarubozu(c1, avgBody, atr) || !IsMarubozu(c2, avgBody, atr)) return false;

        // 2. 窓開け (ヒゲ間ギャップ: C1.Low - C2.High >= gapMin)
        decimal gapMin = Math.Max(
            avgBody * ChartConstants.CandlePatternKickingGapBodyRatio,
            atr * ChartConstants.CandlePatternKickingGapAtrRatio);

        return (c1.Low - c2.High) >= gapMin;
    }

    /// <summary>
    /// 隠れ飲込み (Concealing Baby Swallow): 2本の陰線丸坊主と上ヒゲ陰線の後、4本目の大陰線が3本目を完全に包み込む特異な強気底打ちパターン。
    /// </summary>
    private static bool IsConcealingBabySwallow(IReadOnlyList<CandleData> candles, decimal avgBody, decimal atr)
    {
        int n = candles.Count;
        if (n < 4 || (avgBody <= 0m && atr <= 0m)) return false;

        var c1 = candles[n - 4];
        var c2 = candles[n - 3];
        var c3 = candles[n - 2];
        var c4 = candles[n - 1];

        // 1. C1, C2: 連続する大陰線丸坊主かつ連続下落 (C2.Open <= C1.Open && C2.Close < C1.Close)
        if (!IsBear(c1) || !IsMarubozu(c1, avgBody, atr)) return false;
        if (!IsBear(c2) || !IsMarubozu(c2, avgBody, atr)) return false;
        if (c2.Open > c1.Open || c2.Close >= c1.Close) return false;

        // 2. C3: 窓開け下落で寄付き、長い上ヒゲが C2 終値を上回り実体内に侵入する陰線
        if (!IsBear(c3)) return false;
        decimal gapMin = avgBody * ChartConstants.CandlePatternGapMinRatio;
        if (c3.Open > c2.Close - gapMin) return false; // 窓開け下落

        decimal u3 = c3.High - c3.Open;
        decimal b3 = Body(c3);
        if (b3 <= 0m || u3 < b3 * ChartConstants.CandlePatternConcealSwallowShadowRatio) return false; // 長い上ヒゲ (80%以上)
        if (c3.High <= c2.Close) return false; // C2 終値への侵入 (High > C2.Close)

        // 3. C4: 大陰線であり、C3 のヒゲを含む全レンジを完全に包み込む (Full-Range Engulfing)
        if (!IsBear(c4)) return false;
        if (c4.Open <= c3.High || c4.Close >= c3.Low) return false; // 全レンジ完全包み込み
        if (Body(c4) < avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio) return false;

        return true;
    }

    /// <summary>
    /// 同値三羽烏 (Identical Three Crows): 3本の陰線がそれぞれ前日終値と同値水準から寄付き下落する容赦のない弱気急落パターン。
    /// </summary>
    private static bool IsIdenticalThreeCrows(CandleData c1, CandleData c2, CandleData c3, decimal avgBody)
    {
        if (avgBody <= 0m) return false;

        // 1. 3本連続陰線かつ終値連続切り下げ
        if (!IsBear(c1) || !IsBear(c2) || !IsBear(c3)) return false;
        if (c1.Close <= c2.Close || c2.Close <= c3.Close) return false;

        // 2. 寄付き同値性 (各足の直前終値トレランス比較)
        decimal tol1 = Math.Max(
            avgBody * ChartConstants.CandlePatternIdenticalCrowsOpenToleranceRatio,
            c1.Close * ChartConstants.CandlePatternPriceEqualityAbsoluteMinRatio);
        if (Math.Abs(c2.Open - c1.Close) > tol1) return false;

        decimal tol2 = Math.Max(
            avgBody * ChartConstants.CandlePatternIdenticalCrowsOpenToleranceRatio,
            c2.Close * ChartConstants.CandlePatternPriceEqualityAbsoluteMinRatio);
        if (Math.Abs(c3.Open - c2.Close) > tol2) return false;

        // 3. ノイズ除外 (実体サイズ)
        decimal minBody = avgBody * ChartConstants.CandlePatternNoiseBodyMinRatio;
        if (Body(c1) < minBody || Body(c2) < minBody || Body(c3) < minBody) return false;

        // 4. 下ヒゲの制限 (容赦ない売り: 実体の15%以下)
        decimal maxShadowRatio = ChartConstants.CandlePatternIdenticalCrowsShadowMaxRatio;
        if ((c1.Close - c1.Low) > Body(c1) * maxShadowRatio) return false;
        if ((c2.Close - c2.Low) > Body(c2) * maxShadowRatio) return false;
        if ((c3.Close - c3.Low) > Body(c3) * maxShadowRatio) return false;

        return true;
    }

    #endregion
}

