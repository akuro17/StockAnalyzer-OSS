# Bullish Three-Line Strike — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Continuation)
- **Indicator Identifier (`IndicatorType`)**: `CandleBullishThreeLineStrike`
- **Bar Count**: `4` bars (pattern interval $[s, t] = [t - 3, t]$, context end $e = t - 4$, minimum required history $N_{\min} = 24 bars$)
- **Output Domain**: Unidirectional (Bullish Continuation: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## Visual Reference

![Bullish Three-Line Strike](../../ChartPatterns_Images/06_Candlestick_Patterns/25_bullish_three_line_strike.png)

> [!NOTE]
> This chart image is a conceptual visual illustration for reference only. Actual detection results depend on the strict mathematical rules, parameters, and market data, and may not match this illustration.

## 1. Price Zone and Prior Trend Definition (Role: Within Uptrend (Bullish Continuation))
- **Prior Uptrend (Required)**: At bar $e = t - k$, $C_e - C_{e-10} \ge 1.0 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e \ge \text{EMA}_{20}(e) \;\land\; \left(C_e > \text{EMA}_{10}(e) \lor \text{EMA}_{10}(e) > \text{EMA}_{20}(e)\right)$.
- **Price Zone (High Zone to Mid-Pullback)**: Pattern first-bar close $C_s$ lies within the top 40% of the trailing $N_{\text{zone}} = 20$-bar range ($\text{Pos}(C_s, s, 20) \ge 1 - \text{PriceZoneThreshold}$, evaluating to $\ge 0.60$ under `CoreCandlePatternParameter.CreateFor` default `PriceZoneThreshold = 0.40m`, `CandlestickPredicates.T3Holds`).

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **4 consecutive bars** (interval $[t - 3,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 4$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
4-bar structure $(c_1, c_2, c_3, c_4)$ (v2.0 revised to total-range dominance):
- $c_1..c_3$: Three consecutive bullish candles with rising closes $C_1 < C_2 < C_3$, opening inside prior body ($O_1 \le O_2 \le C_1,\; O_2 \le O_3 \le C_2$), each $B_i \ge 0.50 \cdot \text{AvgBody}_{\text{ctx}}$.
- $c_4$: Large bearish strike candle with $O_4 \ge C_3 - 0.10 \cdot \text{AvgBody}_{\text{ctx}} \;\land\; C_4 < O_1$ and total-span dominance **$B_4 \ge (C_3 - O_1) \;\land\; B_4 \ge 1.50 \cdot \text{AvgBody}_{\text{ctx}}$** (eliminating over-constraint from overlapping body sums).

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: `Main = 1m` upon close of $c_4$.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = L_4$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation requires $0.85 \cdot (B_1 + B_2 + B_3)$, causing false negatives due to overlapping bodies, and omits prior uptrend verification.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 24$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
