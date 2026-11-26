# Bearish Abandoned Baby — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Advanced Reversal)
- **Indicator Identifier (planned `IndicatorType`)**: `CandleBearishAbandonedBaby`
- **Bar Count**: `3` bars (pattern interval $[s, t] = [t - 2, t]$, context end $e = t - 3$, minimum required history $N_{\min} = 23 bars$)
- **Output Domain**: Unidirectional (Bearish: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Price Zone and Prior Trend Definition (Role: Uptrend Top Zone (Bearish Reversal))
- **Prior Uptrend (Required)**: At the immediately preceding bar $e = t - k$, 10-bar price change ($L_{\text{st}} = 10$) satisfies $C_e - C_{e-10} \ge 1.5 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e > \text{EMA}_{10}(e) \;\land\; \text{EMA}_{10}(e) > \text{EMA}_{10}(e-3)$.
- **Price Zone (Top Zone)**: Pattern highest high $H_{\text{pat}} = \max_{i \in [s, t]} H_i$ lies within the top 25% of the trailing $N_{\text{zone}} = 20$-bar range ($\text{Pos}(H_{\text{pat}}, t, 20) \ge 0.75$).

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **3 consecutive bars** (interval $[t - 2,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 3$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
3-bar structure $(c_1, c_2, c_3)$:
- $c_1$: Large bullish candle ($C_1 > O_1 \;\land\; \text{IsLarge}(c_1)$).
- $c_2$: Isolated Doji ($\text{IsDoji}_{\text{star}}(c_2)$) with **full wick-to-wick upward gap** $L_2 > H_1$.
- $c_3$: Large bearish candle ($C_3 < O_3 \;\land\; \text{IsLarge}(c_3)$) with **full wick-to-wick downward gap** $H_3 < L_2$ and $C_3 < \dfrac{O_1 + C_1}{2}$.

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: `Main = 1m` upon close of $c_3$. Optional volume confirmation: $V_3 \ge 1.25 \cdot \text{AvgVol}(t-3, 20)$ (R-8 bypass when volume is unavailable or disabled).
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = H_2$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation omits prior uptrend and top-zone checks.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 23$); return `0m` on OHLC integrity violations or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$. *Exception: A four-price Doji ($R_2 = 0$) is permitted exclusively for the isolated star $c_2$; $c_1$ and $c_3$ strictly require $R_i > 0$.*
