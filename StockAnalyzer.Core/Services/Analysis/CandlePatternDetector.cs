using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Static utility class for mathematically detecting classical single-candle patterns.
/// Detects 16 common patterns (Marubozu, Spinning Top, Doji, etc.) 
/// considering the local volatility context (Average Body Size).
/// </summary>
public static class CandlePatternDetector
{
    /// <summary>
    /// Evaluates the most recent candle in a sequence to determine its candlestick pattern.
    /// Uses previous candles to calculate average body size for contextual context.
    /// </summary>
    /// <param name="candles">Read-only list of historical candles, ending with the target candle.</param>
    /// <returns>The recognized <see cref="CandlePatternType"/>, or None if it doesn't match any clear pattern.</returns>
    public static CandlePatternType DetectPattern(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count == 0) return CandlePatternType.None;
        var candle = candles.Last();
        
        // Context: Average body calculation (up to 14 periods)
        int period = Math.Min(ChartConstants.CandlePatternAtrPeriod, candles.Count);
        decimal sumBody = 0;
        decimal sumTrueRange = 0;
        for (int i = candles.Count - period; i < candles.Count; i++)
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
        decimal avgBody = period > 0 ? (sumBody / period) : 0;
        decimal atr = period > 0 ? (sumTrueRange / period) : 0;
        
        decimal body = Math.Abs(candle.Open - candle.Close);
        decimal range = candle.High - candle.Low;
        decimal upperShadow = candle.High - Math.Max(candle.Open, candle.Close);
        decimal lowerShadow = Math.Min(candle.Open, candle.Close) - candle.Low;
        
        // Define robust zero threshold (5% of the range is considered 'zero' visibility)
        decimal zeroTolerance = range * 0.05m;
        if (range == 0) zeroTolerance = 0;

        bool isBullish = candle.Close > candle.Open;
        bool isBearish = candle.Close < candle.Open;
        
        // Doji (body is strictly <= 5% of candle range)
        bool isDoji = range > 0 ? (body <= range * 0.05m) : true;

        // Body Size Classifications
        // "Large" body: notably larger than recent average AND dominates the candle's range,
        // OR significant relative to ATR (captures high-gap environments).
        bool isLargeBody = body >= (avgBody * 1.5m) && body >= (range * 0.6m);
        if (atr > 0 && !isLargeBody) isLargeBody = body >= (atr * ChartConstants.CandlePatternLargeBodyAtrRatio) && body >= (range * 0.6m);
        // "Small" body: small relative to both avgBody AND range,
        // OR small relative to ATR AND avgBody AND range (captures high-ATR environments).
        bool isSmallBody = body <= (avgBody * 0.8m) && body <= (range * 0.5m);
        if (atr > 0 && !isSmallBody) isSmallBody = body <= (avgBody * 0.8m) && body <= (atr * ChartConstants.CandlePatternSmallBodyAtrRatio) && body <= (range * 0.5m);

        // Fallback if no history exists (e.g. only 1 candle provided) or zero volatility
        if (avgBody == 0 || period < 5) 
        {
            isLargeBody = body >= (range * 0.8m);
            isSmallBody = body <= (range * 0.4m);
        }

        // --- 複合パターン (Multi-Candle Patterns) ---
        if (candles.Count >= 2)
        {
            var prev1 = candles[candles.Count - 2];
            decimal prev1Body = Math.Abs(prev1.Open - prev1.Close);
            decimal prev1Range = prev1.High - prev1.Low;
            bool prev1IsBullish = prev1.Close > prev1.Open;
            bool prev1IsBearish = prev1.Close < prev1.Open;
            bool prev1IsLarge = prev1Body >= (avgBody * 1.5m);
            decimal prev1Mid = (prev1.Open + prev1.Close) / 2m;

            if (candles.Count >= 3)
            {
                var prev2 = candles[candles.Count - 3];
                decimal prev2Body = Math.Abs(prev2.Open - prev2.Close);
                bool prev2IsBullish = prev2.Close > prev2.Open;
                bool prev2IsBearish = prev2.Close < prev2.Open;
                bool prev2IsLarge = prev2Body >= (avgBody * 1.5m);
                decimal prev2Mid = (prev2.Open + prev2.Close) / 2m;

                // 明けの明星 (Morning Star)
                // prev2=Bearish(Large), prev1=Small(gap down), current=Bullish(Large, closes into prev2 body)
                bool prev1IsSmall = prev1Body <= (avgBody * 0.8m) && prev1Body <= (prev1Range * 0.5m);
                bool gapDown = Math.Max(prev1.Open, prev1.Close) < prev2.Close;
                if (prev2IsBearish && prev2IsLarge && prev1IsSmall && gapDown && isBullish && isLargeBody && candle.Close > prev2Mid)
                    return CandlePatternType.MorningStar;

                // 宵の明星 (Evening Star)
                // prev2=Bullish(Large), prev1=Small(gap up), current=Bearish(Large, closes into prev2 body)
                bool gapUp = Math.Min(prev1.Open, prev1.Close) > prev2.Close;
                if (prev2IsBullish && prev2IsLarge && prev1IsSmall && gapUp && isBearish && isLargeBody && candle.Close < prev2Mid)
                    return CandlePatternType.EveningStar;

                // 赤三兵 (Three White Soldiers)
                if (prev2IsBullish && prev1IsBullish && isBullish)
                {
                    // opens within previous body and closes higher
                    bool prev1OpenInside = prev1.Open >= prev2.Open && prev1.Open <= prev2.Close;
                    bool currOpenInside = candle.Open >= prev1.Open && candle.Open <= prev1.Close;
                    if (prev1OpenInside && currOpenInside && prev1.Close > prev2.Close && candle.Close > prev1.Close)
                    {
                        if (prev2Body > (avgBody * 0.5m) && prev1Body > (avgBody * 0.5m) && body > (avgBody * 0.5m))
                            return CandlePatternType.ThreeWhiteSoldiers;
                    }
                }

                // 黒三兵 (Three Black Crows)
                if (prev2IsBearish && prev1IsBearish && isBearish)
                {
                    bool prev1OpenInside = prev1.Open <= prev2.Open && prev1.Open >= prev2.Close;
                    bool currOpenInside = candle.Open <= prev1.Open && candle.Open >= prev1.Close;
                    if (prev1OpenInside && currOpenInside && prev1.Close < prev2.Close && candle.Close < prev1.Close)
                    {
                        if (prev2Body > (avgBody * 0.5m) && prev1Body > (avgBody * 0.5m) && body > (avgBody * 0.5m))
                            return CandlePatternType.ThreeBlackCrows;
                    }
                }
            }

            // 強気包み足 (Bullish Engulfing)
            if (prev1IsBearish && isBullish && candle.Open <= prev1.Close && candle.Close >= prev1.Open && body > prev1Body)
                return CandlePatternType.BullishEngulfing;

            // 弱気包み足 (Bearish Engulfing)
            if (prev1IsBullish && isBearish && candle.Open >= prev1.Close && candle.Close <= prev1.Open && body > prev1Body)
                return CandlePatternType.BearishEngulfing;

            // 強気はらみ (Bullish Harami)
            if (prev1IsBearish && prev1IsLarge && isBullish && isSmallBody && candle.Open >= prev1.Close && candle.Close <= prev1.Open)
                return CandlePatternType.BullishHarami;

            // 弱気はらみ (Bearish Harami)
            if (prev1IsBullish && prev1IsLarge && isBearish && isSmallBody && candle.Open <= prev1.Close && candle.Close >= prev1.Open)
                return CandlePatternType.BearishHarami;

            // 貫き線 (Piercing Line)
            if (prev1IsBearish && prev1IsLarge && isBullish && candle.Open < prev1.Low && candle.Close > prev1Mid && candle.Close <= prev1.Open)
                return CandlePatternType.PiercingLine;

            // かぶせ線 (Dark Cloud Cover)
            if (prev1IsBullish && prev1IsLarge && isBearish && candle.Open > prev1.High && candle.Close < prev1Mid && candle.Close >= prev1.Open)
                return CandlePatternType.DarkCloudCover;
        }

        // --- 四値同時 (Four-Price Doji) ---
        if (range == 0 || (body == 0 && upperShadow == 0 && lowerShadow == 0))
            return CandlePatternType.FourPriceDoji;

        // --- 同時線系 (Doji Patterns) ---
        if (isDoji)
        {
            bool noUpper = upperShadow <= zeroTolerance;
            bool noLower = lowerShadow <= zeroTolerance;
            
            if (noUpper && noLower) return CandlePatternType.FourPriceDoji; // Fallback
            if (noUpper) return CandlePatternType.DragonflyDoji; // トンボ
            if (noLower) return CandlePatternType.GravestoneDoji; // トウバ
            return CandlePatternType.CrossDoji; // 十字線
        }

        // --- 坊主系 (Marubozu Patterns) ---
        if (isLargeBody)
        {
            bool noUpper = upperShadow <= zeroTolerance;
            bool noLower = lowerShadow <= zeroTolerance;
            
            if (isBullish)
            {
                if (noUpper && noLower) return CandlePatternType.BullishMarubozu; // 大陽線_丸坊主
                if (noUpper && !noLower) return CandlePatternType.BullishClosingMarubozu; // 大陽線_大引坊主
                if (!noUpper && noLower) return CandlePatternType.BullishOpeningMarubozu; // 大陽線_寄付坊主
            }
            else if (isBearish)
            {
                if (noUpper && noLower) return CandlePatternType.BearishMarubozu; // 大陰線_丸坊主
                if (!noUpper && noLower) return CandlePatternType.BearishClosingMarubozu; // 大陰線_大引坊主
                if (noUpper && !noLower) return CandlePatternType.BearishOpeningMarubozu; // 大陰線_寄付坊主
            }
        }

        // --- カラカサ / トンカチ / コマ (Umbrella / Tonkachi / Spinning Top) ---
        if (isSmallBody && !isDoji)
        {
            bool hasLongLower = lowerShadow >= (body * 2.0m);
            bool hasLongUpper = upperShadow >= (body * 2.0m);
            
            // For Umbrella/Tonkachi, the "absent" shadow must be extremely small, even smaller than standard zero tolerance.
            bool noUpper = upperShadow <= zeroTolerance || upperShadow <= (body * 0.5m);
            bool noLower = lowerShadow <= zeroTolerance || lowerShadow <= (body * 0.5m);

            if (isBullish)
            {
                if (hasLongLower && noUpper) return CandlePatternType.BullishUmbrella; // 下影陽線_カラカサ
                if (hasLongUpper && noLower) return CandlePatternType.BullishInvertedUmbrella; // 上影陽線_トンカチ
            }
            else if (isBearish)
            {
                if (hasLongLower && noUpper) return CandlePatternType.BearishUmbrella; // 下影陰線_カラカサ
                if (hasLongUpper && noLower) return CandlePatternType.BearishInvertedUmbrella; // 上影陰線_トンカチ
            }
            
            // コマ (Spinning Top) - Has both shadows relative to the small body
            if (upperShadow > zeroTolerance && lowerShadow > zeroTolerance)
            {
                if (isBullish) return CandlePatternType.BullishSpinningTop; // 小陽線_コマ
                if (isBearish) return CandlePatternType.BearishSpinningTop; // 小陰線_コマ
            }
        }

        return CandlePatternType.None;
    }
}
