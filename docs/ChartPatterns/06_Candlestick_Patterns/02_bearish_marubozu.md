# Bearish Marubozu — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Basic)
- **Indicator Identifier (planned `IndicatorType`)**: `CandleBearishMarubozu`
- **Bar Count**: `1` bar (pattern interval $[s, t] = [t - 0, t]$, context end $e = t - 1$, minimum required history $N_{\min} = 21 bars$)
- **Output Domain**: Unidirectional (Bearish: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## Visual Reference

![Bearish Marubozu](../../ChartPatterns_Images/06_Candlestick_Patterns/02_bearish_marubozu.png)

> [!NOTE]
> This chart image is a conceptual visual illustration for reference only. Actual detection results depend on the strict mathematical rules, parameters, and market data, and may not match this illustration.

## 1. Price Zone and Prior Trend Definition (Role: Top Reversal or Downtrend Acceleration)
- **Prior Trend**: Not strictly required; in practice, recommended at top-zone reversals ($\text{Pos}(H_{\text{pat}}, t, 20) \ge 0.70$) or downward range breakdowns ($C_t < \min_{i \in [e-19, e]} L_i$).
- **Volatility Context**: Referenced against the 14 bars ending at $e = t - k$ ($\text{ATR}_{\text{ctx}}(e, 14)$), strictly excluding the pattern bars themselves.

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **1 consecutive bar** (interval $[t - 0,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 1$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
- Bearish candle satisfying the common Marubozu predicate: $C_t < O_t \;\land\; \text{IsMarubozu}(c_t)$.

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: `Main = 1m` upon close of bar $t$. Optional volume confirmation (default `true`): $V_t \ge 1.20 \cdot \text{AvgVol}(t-1, 20)$.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = O_t$ (invalidated if a subsequent close exceeds $O_t$).

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation lacks context separation, volume verification, and suffers from exclusive shadowing.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 21$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
