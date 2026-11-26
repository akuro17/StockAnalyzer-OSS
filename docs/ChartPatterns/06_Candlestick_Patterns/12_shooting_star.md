# Shooting Star — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Basic)
- **Indicator Identifier (planned `IndicatorType`)**: `CandleShootingStar`
- **Bar Count**: `1` bar (pattern interval $[s, t] = [t - 0, t]$, context end $e = t - 1$, minimum required history $N_{\min} = 21 bars$)
- **Output Domain**: Unidirectional (Bearish: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## Visual Reference

![Shooting Star](../../ChartPatterns_Images/06_Candlestick_Patterns/12_shooting_star.png)

> [!NOTE]
> This chart image is a conceptual visual illustration for reference only. Actual detection results depend on the strict mathematical rules, parameters, and market data, and may not match this illustration.

## 1. Price Zone and Prior Trend Definition (Role: Uptrend Top Zone (Bearish Reversal))
- **Prior Uptrend (Required)**: At the immediately preceding bar $e = t - k$, 10-bar price change ($L_{\text{st}} = 10$) satisfies $C_e - C_{e-10} \ge 1.5 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e > \text{EMA}_{10}(e) \;\land\; \text{EMA}_{10}(e) > \text{EMA}_{10}(e-3)$.
- **Price Zone (Top Zone)**: Pattern highest high $H_{\text{pat}} = \max_{i \in [s, t]} H_i$ lies within the top 25% of the trailing $N_{\text{zone}} = 20$-bar range ($\text{Pos}(H_{\text{pat}}, t, 20) \ge 0.75$).

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **1 consecutive bar** (interval $[t - 0,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 1$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
1-bar structure $(c_t)$ (must appear in an uptrend top zone):
- Small non-Doji real body: $0.05 \cdot R_t < B_t \le \min(0.40 \cdot R_t,\; 0.80 \cdot \text{AvgBody}_{\text{ctx}})$.
- Long upper shadow: $U_t \ge 2.0 \cdot B_t \;\land\; R_t \ge 0.80 \cdot \text{ATR}_{\text{ctx}}$, with minimal lower shadow $D_t \le \min(0.10 \cdot R_t,\; 0.50 \cdot B_t)$.

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: Confirmed upon bar $t$ close meeting uptrend and top-zone criteria.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = H_t$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current Screener uses `requireTrendContext = false` and does not verify the prior uptrend.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 21$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
