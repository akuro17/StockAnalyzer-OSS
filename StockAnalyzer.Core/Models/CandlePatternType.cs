namespace StockAnalyzer.Core.Models;

/// <summary>
/// Defines classic single-candle charting patterns (Candlestick Patterns).
/// </summary>
public enum CandlePatternType
{
    /// <summary>No recognized pattern</summary>
    None = 0,

    // --- 陽線のパターン (Bullish Candle Patterns) ---

    /// <summary>大陽線_丸坊主 (Bullish Marubozu): Large body, no upper or lower shadows.</summary>
    BullishMarubozu,
    
    /// <summary>大陽線_大引坊主 (Bullish Closing Marubozu): Large body, no upper shadow, has lower shadow.</summary>
    BullishClosingMarubozu,
    
    /// <summary>大陽線_寄付坊主 (Bullish Opening Marubozu): Large body, has upper shadow, no lower shadow.</summary>
    BullishOpeningMarubozu,
    
    /// <summary>小陽線_コマ (Bullish Spinning Top): Small body, small upper and lower shadows.</summary>
    BullishSpinningTop,
    
    /// <summary>下影陽線_カラカサ (Bullish Umbrella / Hammer): Small body at top, long lower shadow, no upper shadow.</summary>
    BullishUmbrella,
    
    /// <summary>上影陽線_トンカチ (Bullish Inverted Umbrella / Inverted Hammer): Small body at bottom, long upper shadow, no lower shadow.</summary>
    BullishInvertedUmbrella,

    // --- 陰線のパターン (Bearish Candle Patterns) ---

    /// <summary>大陰線_丸坊主 (Bearish Marubozu): Large body, no upper or lower shadows.</summary>
    BearishMarubozu,
    
    /// <summary>大陰線_大引坊主 (Bearish Closing Marubozu): Large body, no lower shadow, has upper shadow.</summary>
    BearishClosingMarubozu,
    
    /// <summary>大陰線_寄付坊主 (Bearish Opening Marubozu): Large body, has lower shadow, no upper shadow.</summary>
    BearishOpeningMarubozu,
    
    /// <summary>小陰線_コマ (Bearish Spinning Top): Small body, small upper and lower shadows.</summary>
    BearishSpinningTop,
    
    /// <summary>下影陰線_カラカサ (Bearish Umbrella / Hanging Man): Small body at top, long lower shadow, no upper shadow.</summary>
    BearishUmbrella,
    
    /// <summary>上影陰線_トンカチ (Bearish Inverted Umbrella / Shooting Star): Small body at bottom, long upper shadow, no lower shadow.</summary>
    BearishInvertedUmbrella,

    // --- その他のパターン (Doji Patterns) ---

    /// <summary>トンボ (Dragonfly Doji): Open and close are at high, long lower shadow.</summary>
    DragonflyDoji,
    
    /// <summary>トウバ (Gravestone Doji): Open and close are at low, long upper shadow.</summary>
    GravestoneDoji,
    
    /// <summary>十字線 (Cross Doji): Open and close are equal, both upper and lower shadows present.</summary>
    CrossDoji,
    
    /// <summary>四値同時 (Four-Price Doji): Open, High, Low, and Close are all equal (no range).</summary>
    FourPriceDoji,

    // --- 2本組のパターン (Double-Candle Patterns) ---

    /// <summary>強気包み足 (Bullish Engulfing): Bearish candle followed by a larger bullish candle that fully engulfs its body.</summary>
    BullishEngulfing,

    /// <summary>弱気包み足 (Bearish Engulfing): Bullish candle followed by a larger bearish candle that fully engulfs its body.</summary>
    BearishEngulfing,

    /// <summary>強気はらみ (Bullish Harami): Large bearish candle followed by a smaller bullish candle contained within its body.</summary>
    BullishHarami,

    /// <summary>弱気はらみ (Bearish Harami): Large bullish candle followed by a smaller bearish candle contained within its body.</summary>
    BearishHarami,

    /// <summary>貫き線 (Piercing Line): Bearish candle followed by a bullish candle that opens below the previous low but closes above its midpoint.</summary>
    PiercingLine,

    /// <summary>かぶせ線 (Dark Cloud Cover): Bullish candle followed by a bearish candle that opens above the previous high but closes below its midpoint.</summary>
    DarkCloudCover,

    // --- 3本組のパターン (Triple-Candle Patterns) ---

    /// <summary>明けの明星 (Morning Star): Large bearish, small body (gap down), large bullish (gap up).</summary>
    MorningStar,

    /// <summary>宵の明星 (Evening Star): Large bullish, small body (gap up), large bearish (gap down).</summary>
    EveningStar,

    /// <summary>赤三兵 (Three White Soldiers): Three consecutive long bullish candles, each opening within previous body and closing higher.</summary>
    ThreeWhiteSoldiers,

    /// <summary>黒三兵 (Three Black Crows): Three consecutive long bearish candles, each opening within previous body and closing lower.</summary>
    ThreeBlackCrows
}
