# Mat Hold — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Continuation)
- **Indicator Identifier (`IndicatorType`)**: `CandleMatHold`
- **Bar Count**: `5` bars (pattern interval $[s, t] = [t - 4, t]$, context end $e = t - 5$, minimum required history $N_{\min} = 25 bars$)
- **Output Domain**: Unidirectional (Bullish Continuation: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Price Zone and Prior Trend Definition (Role: Within Uptrend (Bullish Continuation))
- **Prior Uptrend (Required)**: At bar $e = t - k$, $C_e - C_{e-10} \ge 1.0 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e \ge \text{EMA}_{20}(e) \;\land\; \left(C_e > \text{EMA}_{10}(e) \lor \text{EMA}_{10}(e) > \text{EMA}_{20}(e)\right)$.
- **Price Zone (High Zone to Mid-Pullback)**: Pattern first-bar close $C_s$ lies within the top 40% of the trailing $N_{\text{zone}} = 20$-bar range ($\text{Pos}(C_s, s, 20) \ge 1 - \text{PriceZoneThreshold}$, evaluating to $\ge 0.60$ under `CoreCandlePatternParameter.CreateFor` default `PriceZoneThreshold = 0.40m`, `CandlestickPredicates.T3Holds`).

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **5 consecutive bars** (interval $[t - 4,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 5$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
5-bar structure $(c_1 \dots c_5)$:
- $c_1$: Large bullish candle ($C_1 > O_1 \;\land\; \text{IsLarge}(c_1)$).
- $c_2$: $\text{IsSmall}(c_2)$ with upward real-body gap $O_2 > C_1 + 0.10 \cdot \text{AvgBody}_{\text{ctx}}$.
- $c_3, c_4$: $\text{IsSmall}$; at least 1 bearish candle among $c_2..c_4$, all lows $\ge O_1$, and all closes $> \dfrac{O_1 + C_1}{2}$.
- $c_5$: Strong bullish breakout ($C_5 > O_5 \;\land\; (B_5 \ge 1.0 \cdot \text{AvgBody}_{\text{ctx}} \lor \text{IsLarge}(c_5)) \;\land\; O_5 \ge C_4 \;\land\; C_5 > \max(H_1..H_4)$).

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: Confirmed when $c_5$ closes above $\max(H_1..H_4)$.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = \dfrac{O_1 + C_1}{2}$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation omits prior uptrend verification.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 25$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
