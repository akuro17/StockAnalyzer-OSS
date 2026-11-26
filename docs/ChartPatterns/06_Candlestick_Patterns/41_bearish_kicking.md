# Bearish Kicking — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Special)
- **Indicator Identifier (planned `IndicatorType`)**: `CandleBearishKicking`
- **Bar Count**: `2` bars (pattern interval $[s, t] = [t - 1, t]$, context end $e = t - 2$, minimum required history $N_{\min} = 22 bars$)
- **Output Domain**: Unidirectional (Bearish: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Price Zone and Prior Trend Definition (Role: Top Reversal or Downtrend Acceleration)
- **Prior Trend**: Not strictly required; in practice, recommended at top-zone reversals ($\text{Pos}(H_{\text{pat}}, t, 20) \ge 0.70$) or downward range breakdowns ($C_t < \min_{i \in [e-19, e]} L_i$).
- **Volatility Context**: Referenced against the 14 bars ending at $e = t - k$ ($\text{ATR}_{\text{ctx}}(e, 14)$), strictly excluding the pattern bars themselves.

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **2 consecutive bars** (interval $[t - 1,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 2$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
2-bar structure $(c_1, c_2)$ (context baseline $e = t - 2$, revised in v2.0):
- $c_1$: Bullish Marubozu ($C_1 > O_1 \;\land\; \text{IsMarubozu}(c_1)$).
- $c_2$: Bearish Marubozu ($C_2 < O_2 \;\land\; \text{IsMarubozu}(c_2)$).
- Full wick-to-wick downward gap: $L_1 - H_2 \ge \max(0.20 \cdot \text{AvgBody}_{\text{ctx}},\; 0.50 \cdot \text{ATR}_{\text{ctx}})$.

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: `Main = 1m` upon close of $c_2$. Optional volume confirmation: $V_2 \ge 1.30 \cdot \text{AvgVol}(t-2, 20)$.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = L_1$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation lacks context separation and volume verification.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 22$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
