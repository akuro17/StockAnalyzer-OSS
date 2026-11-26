# Bullish Engulfing — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Basic)
- **Indicator Identifier (planned `IndicatorType`)**: `CandleBullishEngulfing`
- **Bar Count**: `2` bars (pattern interval $[s, t] = [t - 1, t]$, context end $e = t - 2$, minimum required history $N_{\min} = 22 bars$)
- **Output Domain**: Unidirectional (Bullish: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## Visual Reference

![Bullish Engulfing](../../ChartPatterns_Images/06_Candlestick_Patterns/03_bullish_engulfing.png)

> [!NOTE]
> This chart image is a conceptual visual illustration for reference only. Actual detection results depend on the strict mathematical rules, parameters, and market data, and may not match this illustration.

## 1. Price Zone and Prior Trend Definition (Role: Downtrend Bottom Zone (Bullish Reversal))
- **Prior Downtrend (Required)**: At the immediately preceding bar $e = t - k$, 10-bar price change ($L_{\text{st}} = 10$) satisfies $C_{e-10} - C_e \ge 1.5 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e < \text{EMA}_{10}(e) \;\land\; \text{EMA}_{10}(e) < \text{EMA}_{10}(e-3)$.
- **Price Zone (Bottom Zone)**: Pattern lowest low $L_{\text{pat}} = \min_{i \in [s, t]} L_i$ lies within the bottom 25% of the trailing $N_{\text{zone}} = 20$-bar range ($\text{Pos}(L_{\text{pat}}, t, 20) \le 0.25$).

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **2 consecutive bars** (interval $[t - 1,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 2$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
2-bar structure $(c_1 = C_{t-1},\; c_2 = C_t)$:
- $c_1$: Bearish candle ($C_1 < O_1$) with noise floor $B_1 \ge 0.30 \cdot \text{AvgBody}_{\text{ctx}}$.
- $c_2$: Bullish candle ($C_2 > O_2$) with $B_2 \ge 1.00 \cdot \text{AvgBody}_{\text{ctx}}$.
- Complete real-body engulfing: $O_2 \le C_1 < O_1 \le C_2 \;\land\; B_2 > B_1$ (strict mode: $B_2 \ge 1.25 \cdot B_1$).

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: `Main = 1m` upon close of $c_2$. Optional volume confirmation: $V_2 \ge 1.20 \cdot V_1 \land V_2 \ge 1.10 \cdot \text{AvgVol}(t-2, 20)$.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = \min(L_1, L_2)$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation omits prior downtrend, bottom-zone check, $c_1$ minimum body filter, and volume expansion.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 22$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
