namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// Tier-2 specification constants for the 43 Candlestick Pattern engines (<c>06_Candlestick_Patterns\*.md</c> and <c>00_COMMON_MATHEMATICAL_DEFINITIONS.md</c> §§3–4;
/// ledgers <c>docs/sa_pattern_literal_ledger_V2-8_CandlesPart1</c>..V2-10). Tunable user settings live in <c>PatternFlagParameterLimits.Candles.cs</c>.
/// </summary>
public static partial class PatternSpecConstants
{
    // ---------------------------------------------------------------- Common §§3–4 Candlestick Constants

    /// <summary>Common §3.1: minimum context bars before the k-bar pattern (max of ATR 14+1, ST 10+1, Zone 20, Vol 20, EMA20 = 20 -> N_min = 20 + k).</summary>
    public const int CandleMinBaseBars = 20;

    /// <summary>Common §4.1 / decision U2-01 A: slow EMA period used by T3/T4 continuation prior-trend check (EMA10 &gt; EMA20).</summary>
    public const int CandleContinuationEmaSlowPeriod = 20;

    /// <summary>Decision U2-03 A / 06_Candlestick T3: continuation bullish zone minimum (<c>Pos(C_s, s, 20) &gt;= 0.60</c>).</summary>
    public const decimal CandleContinuationBullZoneMin = 0.60m;

    /// <summary>Decision U2-03 A / 06_Candlestick T4: continuation bearish zone maximum (<c>Pos(C_s, s, 20) &lt;= 0.40</c>).</summary>
    public const decimal CandleContinuationBearZoneMax = 0.40m;

    /// <summary>Decision U2-03 A / 06/13 Cross Doji §1: extreme high zone (<c>Pos(H_t, t, 20) &gt;= 0.80</c>).</summary>
    public const decimal CandleDojiHighZoneMin = 0.80m;

    /// <summary>Decision U2-03 A / 06/13 Cross Doji §1: extreme low zone (<c>Pos(L_t, t, 20) &lt;= 0.20</c>).</summary>
    public const decimal CandleDojiLowZoneMax = 0.20m;

    /// <summary>06/18 Three White Soldiers §1 (T6): long lookback bars for bottom-zone alternative (60 bars).</summary>
    public const int CandleLongZoneBars = 60;

    /// <summary>06/18 Three White Soldiers §1 (T6): bottom-zone threshold over 60 bars (&lt;= 0.25).</summary>
    public const decimal CandleLongZoneLowMax = 0.25m;

    /// <summary>Common §3: minimum range ratio vs ATR_ctx for <c>IsMarubozu</c> (<c>B_i &gt;= max(..., 0.80 * ATR_ctx)</c>).</summary>
    public const decimal CandleMarubozuAtrRatio = 0.80m;

    /// <summary>Common §3: minimum body-to-range ratio for <c>IsMarubozu</c> (<c>B_i / R_i &gt;= 0.90</c>).</summary>
    public const decimal CandleMarubozuBodyRangeMin = 0.90m;

    /// <summary>Common §3: minimum range-to-ATR ratio for normal standalone <c>IsDoji</c> (<c>R_i &gt;= 0.50 * ATR_ctx</c>).</summary>
    public const decimal CandleDojiNormalMinRangeAtr = 0.50m;

    // ---------------------------------------------------------------- V2-8 Part 1 Pattern-Specific Constants

    /// <summary>06/09–12 Umbrella §§3: maximum body-to-range ratio (<c>B_t &lt;= 0.40 * R_t</c>).</summary>
    public const decimal CandleUmbrellaMaxBodyRangeRatio = 0.40m;

    /// <summary>06/09–12 Umbrella §§3: minimum bar range vs ATR_ctx (<c>R_t &gt;= 0.80 * ATR_ctx</c>).</summary>
    public const decimal CandleUmbrellaMinRangeAtr = 0.80m;

    /// <summary>06/09–12 Umbrella §§3: maximum opposite-shadow-to-range ratio (<c>&lt;= 0.10 * R_t</c>).</summary>
    public const decimal CandleUmbrellaMaxOppShadowRangeRatio = 0.10m;

    /// <summary>06/13 Cross Doji §3: strict minimum upper and lower shadow ratios (<c>U_t &gt; 0.15 * R_t</c> and <c>D_t &gt; 0.15 * R_t</c>).</summary>
    public const decimal CandleCrossDojiMinShadowRatio = 0.15m;

    /// <summary>06/14–15 Dragonfly / Gravestone Doji §3: minimum range vs ATR_ctx (<c>R_t &gt;= 0.80 * ATR_ctx</c>).</summary>
    public const decimal CandleShadowDojiMinRangeAtr = 0.80m;

    /// <summary>06/14–15 Dragonfly / Gravestone Doji §3: minimum long shadow ratio (<c>&gt;= 0.70 * R_t</c>).</summary>
    public const decimal CandleShadowDojiMinLongShadowRatio = 0.70m;

    /// <summary>06/03–04 Engulfing §3: minimum c1 noise-filter body ratio (<c>B_1 &gt;= 0.30 * AvgBody_ctx</c>).</summary>
    public const decimal CandleEngulfingFirstMinBodyAvgRatio = 0.30m;

    /// <summary>06/03–04 Engulfing §3: minimum c2 body ratio (<c>B_2 &gt;= 1.00 * AvgBody_ctx</c>).</summary>
    public const decimal CandleEngulfingSecondMinBodyAvgRatio = 1.00m;

    /// <summary>06/03–04 Engulfing §4: optional volume confirmation vs prior bar (<c>V_2 &gt;= 1.20 * V_1</c>).</summary>
    public const decimal CandleEngulfingVolumeRatio = 1.20m;

    /// <summary>06/03 Bullish Engulfing §4: default volume multiplier vs AvgVol(20) (<c>1.10m</c>).</summary>
    public const decimal CandleBullishEngulfingAvgVolumeMultiplier = 1.10m;

    /// <summary>06/09, 16, 17 Hammer / Piercing / Dark Cloud §4: default volume multiplier vs AvgVol(20) (<c>1.15m</c>).</summary>
    public const decimal CandleHammerPiercingAvgVolumeMultiplier = 1.15m;

    /// <summary>06/05–06 Harami §3: maximum c2/c1 body ratio (<c>B_2 &lt;= 0.50 * B_1</c>).</summary>
    public const decimal CandleHaramiMaxInnerBodyRatio = 0.50m;

    /// <summary>06/40–41 Kicking §3: minimum wick-to-wick gap avg-body term (<c>0.20 * AvgBody_ctx</c>).</summary>
    public const decimal CandleKickingMinGapAvgRatio = 0.20m;

    /// <summary>06/40–41 Kicking §3: minimum wick-to-wick gap ATR term (<c>0.50 * ATR_ctx</c>).</summary>
    public const decimal CandleKickingMinGapAtrRatio = 0.50m;

    /// <summary>06/40–41 Kicking §4: default volume multiplier vs AvgVol(20) (<c>1.30m</c>).</summary>
    public const decimal CandleKickingAvgVolumeMultiplier = 1.30m;

    // ---------------------------------------------------------------- V2-9 Part 2 (3-Bar) Pattern-Specific Constants

    /// <summary>06/07–08 Morning / Evening Star §3: minimum third-bar body vs AvgBody_ctx (<c>1.20 * AvgBody_ctx</c>).</summary>
    public const decimal CandleStarThirdMinBodyAvgRatio = 1.20m;

    /// <summary>06/18–19 Three White Soldiers / Three Black Crows / Deliberation §3: minimum bar body vs AvgBody_ctx (<c>0.70 * AvgBody_ctx</c>).</summary>
    public const decimal CandleThreeSoldiersMinBodyAvgRatio = 0.70m;

    /// <summary>06/18–19 Three White Soldiers / Three Black Crows §3: minimum body-to-range ratio (<c>B_i / R_i &gt;= 0.55</c>).</summary>
    public const decimal CandleThreeSoldiersMinBodyRange = 0.55m;

    /// <summary>06/23–24, 34–37 Side-by-Side / Tasuki Gap / Gap Three Methods §3: minimum first-bar body vs AvgBody_ctx (<c>0.80 * AvgBody_ctx</c>).</summary>
    public const decimal CandleGapPatternFirstMinBodyAvgRatio = 0.80m;

    /// <summary>06/23–24 Side-by-Side White Lines §3: maximum open difference ratio vs max(AvgBody_ctx, B_2) (<c>0.30</c>).</summary>
    public const decimal CandleSideBySideOpenTolRatio = 0.30m;

    /// <summary>06/23–24 Side-by-Side White Lines §3: maximum body difference ratio vs B_2 (<c>0.30</c>).</summary>
    public const decimal CandleSideBySideBodyTolRatio = 0.30m;

    /// <summary>06/27 Bullish Abandoned Baby §4: default volume multiplier vs AvgVol(20) (<c>1.25m</c>).</summary>
    public const decimal CandleAbandonedBabyAvgVolumeMultiplier = 1.25m;

    /// <summary>06/29 Advance Block §3: maximum c_3/c_1 body ratio (<c>B_3 &lt;= 0.85 * B_1</c>).</summary>
    public const decimal CandleAdvanceBlockMaxThirdBodyRatio = 0.85m;

    /// <summary>06/29 Advance Block §3: minimum upper shadow ratio of c_2 or c_3 (<c>0.50 * B_i</c>).</summary>
    public const decimal CandleAdvanceBlockMinUpperShadowBodyRatio = 0.50m;

    /// <summary>06/30 Deliberation §3: maximum c_3/c_2 body ratio (<c>0.40 * B_2</c>).</summary>
    public const decimal CandleDeliberationMaxThirdBodyRatio = 0.40m;

    /// <summary>06/30 Deliberation §3: c_3 open slack below c_2 close (<c>0.15 * AvgBody_ctx</c>).</summary>
    public const decimal CandleDeliberationThirdOpenSlackAvgRatio = 0.15m;

    /// <summary>06/31 Stick Sandwich §3: c_3 open slack below c_2 close (<c>0.10 * AvgBody_ctx</c>).</summary>
    public const decimal CandleStickSandwichThirdOpenSlackAvgRatio = 0.10m;

    /// <summary>06/31 Stick Sandwich &amp; 06/43 Identical Three Crows §3: equal-price tolerance AvgBody term (<c>0.05 * AvgBody_ctx</c>).</summary>
    public const decimal CandleEqualPriceAvgBodyRatio = 0.05m;

    /// <summary>06/31 Stick Sandwich &amp; 06/43 Identical Three Crows §3: equal-price tolerance price fraction (<c>0.002 * price</c>).</summary>
    public const decimal CandleEqualPriceFraction = 0.002m;

    /// <summary>06/34–37 Tasuki Gap &amp; Gap Three Methods §3: c_3 open slack vs c_2 close (<c>0.20 * AvgBody_ctx</c>).</summary>
    public const decimal CandleGapReentryOpenSlackAvgRatio = 0.20m;

    /// <summary>06/43 Identical Three Crows §3: maximum lower shadow ratio (<c>D_i &lt;= 0.15 * B_i</c>).</summary>
    public const decimal CandleIdenticalCrowsMaxLowerShadowBodyRatio = 0.15m;

    // ---------------------------------------------------------------- V2-10 Part 3 (4/5-Bar) Pattern-Specific Constants

    /// <summary>06/20–21, 22, 32 Rising/Falling Three Methods, Mat Hold &amp; Ladder Bottom §3: fifth-bar minimum body vs AvgBody_ctx (<c>1.00 * AvgBody_ctx</c>).</summary>
    public const decimal CandleThreeMethodsFifthMinBodyAvgRatio = 1.00m;

    /// <summary>06/22 Mat Hold §3: second-bar open upward gap above c_1 close (<c>0.10 * AvgBody_ctx</c>).</summary>
    public const decimal CandleMatHoldSecondOpenGapAvgRatio = 0.10m;

    /// <summary>06/25–26 Three-Line Strike §3: fourth-bar open slack into c_3 close (<c>0.10 * AvgBody_ctx</c>).</summary>
    public const decimal CandleThreeLineStrikeOpenSlackAvgRatio = 0.10m;

    /// <summary>06/25–26 Three-Line Strike §3: minimum fourth-bar body vs AvgBody_ctx (<c>1.50 * AvgBody_ctx</c>).</summary>
    public const decimal CandleThreeLineStrikeFourthMinBodyAvgRatio = 1.50m;

    /// <summary>06/38–39 Breakaway §3: fifth-bar minimum body vs AvgBody_ctx (<c>1.20 * AvgBody_ctx</c>).</summary>
    public const decimal CandleBreakawayFifthMinBodyAvgRatio = 1.20m;

    /// <summary>06/42 Concealing Baby Swallow §3: third-bar minimum body vs AvgBody_ctx (<c>0.20 * AvgBody_ctx</c>).</summary>
    public const decimal CandleConcealingBabyThirdMinBodyAvgRatio = 0.20m;

    /// <summary>06/42 Concealing Baby Swallow §3: third-bar maximum lower shadow vs range (<c>D_3 &lt;= 0.10 * R_3</c>).</summary>
    public const decimal CandleConcealingBabyThirdMaxLowerShadowRangeRatio = 0.10m;

    /// <summary>06/42 Concealing Baby Swallow §3: third-bar minimum upper shadow vs body (<c>U_3 &gt;= 0.80 * B_3</c>).</summary>
    public const decimal CandleConcealingBabyThirdMinUpperShadowBodyRatio = 0.80m;
}
