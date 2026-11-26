# Three White Soldiers — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Basic)
- **Indicator Identifier (planned `IndicatorType`)**: `CandleThreeWhiteSoldiers`
- **Bar Count**: `3` bars (pattern interval $[s, t] = [t - 2, t]$, context end $e = t - 3$, minimum required history $N_{\min} = 63 bars (under default `PriceZoneLookbackBars = 60`, computed as $3 + \max(\text{AtrPeriod}+1, L_{\text{st}}+1, \text{PriceZoneLookbackBars}, 20)$)$)
- **Output Domain**: Unidirectional (Bullish: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Price Zone and Prior Trend Definition (Role: Downtrend Bottom Zone or Base-Building Range (Bullish Breakout))
- **Prior Downtrend or Bottom-Zone Range (v2.0 Improvement)**: At bar $e = t - k$, satisfies either (a) short-term prior downtrend ($C_{e-10} - C_e \ge 1.5 \cdot \text{ATR}_{\text{ctx}}(e, 14) \land C_e < \text{EMA}_{10}(e)$) **or** (b) bottom zone of the trailing `PriceZoneLookbackBars` (default $60$ bars) range ($\text{Pos}(L_{\text{pat}}, t, \text{PriceZoneLookbackBars}) \le \text{PriceZoneThreshold}$, default $0.25$), preventing missed signals on Three White Soldiers emerging from a base.

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **3 consecutive bars** (interval $[t - 2,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 3$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range 60 bars (default `PriceZoneLookbackBars = 60`, dynamically linked); average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
3-bar structure $(c_1, c_2, c_3)$:
- Three consecutive strong bullish candles: $C_i > O_i \;\land\; B_i \ge 0.70 \cdot \text{AvgBody}_{\text{ctx}} \;\land\; B_i / R_i \ge 0.55$.
- Strictly rising closes and highs: $C_1 < C_2 < C_3 \;\land\; H_1 < H_2 < H_3$.
- Opening inside prior real body: $O_1 \le O_2 \le C_1 \;\land\; O_2 \le O_3 \le C_2$.
- Controlled upper shadows: $U_i \le 0.30 \cdot B_i$ for each candle.

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: `Main = 1m` upon close of $c_3$.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = O_1$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation omits upper-shadow caps, body-to-range ratio, and bottom-zone context verification.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < $N_{\min}$ (default $63$)$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
