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
    ThreeBlackCrows,

    // --- 継続系パターン (Continuation Patterns: 3〜5本組) ---

    /// <summary>上昇三法 (Rising Three Methods): 大陽線の後に3本の小足がレンジ内で調整し、5本目の大陽線で高値更新する上昇継続パターン。</summary>
    RisingThreeMethods,

    /// <summary>下降三法 (Falling Three Methods): 大陰線の後に3本の小足がレンジ内で戻りを試すが、5本目の大陰線で安値更新する下降継続パターン。</summary>
    FallingThreeMethods,

    /// <summary>マットホールド (Mat Hold): 上放れした後に浅い調整を挟み、5本目で最高値を更新して急騰する強力な上昇継続パターン。</summary>
    MatHold,

    /// <summary>上放れ並び赤 (Bullish Side-by-Side White Lines): 上昇トレンド中に窓を開けてほぼ同サイズの陽線が2本連続する上昇継続パターン。</summary>
    BullishSideBySideWhiteLines,

    /// <summary>下放れ並び赤 (Bearish Side-by-Side White Lines): 下降トレンド中に窓を開けてほぼ同サイズの陽線が2本連続する下降継続パターン。</summary>
    BearishSideBySideWhiteLines,

    /// <summary>強気三手一撃 (Bullish Three-Line Strike): 3本連続の上昇陽線の後に、それらを一撃で包み込む巨大な陰線が出現する上昇継続パターン。</summary>
    BullishThreeLineStrike,

    /// <summary>弱気三手一撃 (Bearish Three-Line Strike): 3本連続の下降陰線の後に、それらを一撃で包み込む巨大な陽線が出現する下降継続パターン。</summary>
    BearishThreeLineStrike,

    // --- 高度な反転系パターン (Advanced Reversal Patterns: Prompt 70-2) ---

    /// <summary>強気棄て子 (Bullish Abandoned Baby): 大陰線の後、窓下に孤立した十字線が出現し、窓上に大陽線が反発する極めて強力な底値反転パターン。</summary>
    BullishAbandonedBaby,

    /// <summary>弱気棄て子 (Bearish Abandoned Baby): 大陽線の後、窓上に孤立した十字線が出現し、窓下に大陰線が反落する極めて強力な天井反転パターン。</summary>
    BearishAbandonedBaby,

    /// <summary>先詰まり / 赤三兵先詰まり (Advance Block): 3本連続陽線だが、徐々に実体が縮小し上ヒゲが伸長して上昇エネルギーが枯渇する弱気警告パターン。</summary>
    AdvanceBlock,

    /// <summary>思案 / 赤三兵思案 (Deliberation): 2本の大陽線の後、3本目が上放れ小陽線または十字線となり買いが躊躇される弱気警告パターン。</summary>
    Deliberation,

    /// <summary>スティックサンドイッチ (Stick Sandwich): 2本の陰線が中間の陽線を挟み込み、同一終値水準で強固な支持線を形成する強気反転パターン。</summary>
    StickSandwich,

    /// <summary>梯子底 (Ladder Bottom): 3本連続陰線の後、4本目にヒゲを持つ反転の兆し足、5本目に窓開けブレイク大陽線が出現する5本組の強力な底打ちパターン。</summary>
    LadderBottom,

    /// <summary>家路を辿る鳩 (Homing Pigeon): 大陰線の実体内に小陰線が完全に収まる（陰線のはらみ足）、下降トレンド終焉を示唆する強気反転パターン。</summary>
    HomingPigeon,

    // --- 窓・ギャップ系パターン (Gap Patterns: Prompt 70-3) ---

    /// <summary>上放れタスキ (Bullish Tasuki Gap): 上放れ陽線の後、陰線が窓に侵入するが窓を埋めきらずに引ける上昇継続パターン。</summary>
    BullishTasukiGap,

    /// <summary>下放れタスキ (Bearish Tasuki Gap): 下放れ陰線の後、陽線が窓に侵入するが窓を埋めきらずに引ける下降継続パターン。</summary>
    BearishTasukiGap,

    /// <summary>上放れ窓埋め三法 (Bullish Gap Three Methods): 上放れ陽線の後、陰線が下落して窓を完全に埋め1本目実体内で引ける上昇継続パターン。</summary>
    BullishGapThreeMethods,

    /// <summary>下放れ窓埋め三法 (Bearish Gap Three Methods): 下放れ陰線の後、陽線が上昇して窓を完全に埋め1本目実体内で引ける下降継続パターン。</summary>
    BearishGapThreeMethods,

    /// <summary>強気離脱 (Bullish Breakaway): 大陰線から窓開け下放れ＋2本小足停滞後、大陽線でレンジを上抜け元の窓内に突入して引ける底値反転パターン。</summary>
    BullishBreakaway,

    /// <summary>弱気離脱 (Bearish Breakaway): 大陽線から窓開け上放れ＋2本小足停滞後、大陰線でレンジを下抜け元の窓内に突入して引ける天井反転パターン。</summary>
    BearishBreakaway,

    // --- 特殊・マイナー型パターン (Exotic & Minor Patterns: Prompt 70-4) ---

    /// <summary>強気蹴り上げ (Bullish Kicking): 大陰線丸坊主の翌日に上放れて大陽線丸坊主が出現する極めて強力な強気反転シグナル。</summary>
    BullishKicking,

    /// <summary>弱気蹴り下げ (Bearish Kicking): 大陽線丸坊主の翌日に下放れて大陰線丸坊主が出現する極めて強力な弱気反転シグナル。</summary>
    BearishKicking,

    /// <summary>隠れ飲込み (Concealing Baby Swallow): 2本の陰線丸坊主と上ヒゲ陰線の後、4本目の大陰線が3本目を完全に包み込む特異な強気底打ちパターン。</summary>
    ConcealingBabySwallow,

    /// <summary>同値三羽烏 (Identical Three Crows): 3本の陰線がそれぞれ前日終値と同値水準から寄付き下落する容赦のない弱気急落パターン。</summary>
    IdenticalThreeCrows
}

