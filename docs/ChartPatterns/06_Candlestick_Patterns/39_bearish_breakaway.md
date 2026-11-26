# Bearish Breakaway — Strict Specification (v2.0)

- **Category**: `Candlestick Patterns` (Subcategory: Gap)
- **Indicator Identifier (planned `IndicatorType`)**: `CandleBearishBreakaway`
- **Bar Count**: `5` bars (pattern interval $[s, t] = [t - 4, t]$, context end $e = t - 5$, minimum required history $N_{\min} = 25 bars$)
- **Output Domain**: Unidirectional (Bearish: `1m`) (`Main ∈ {null, 0m, 1m}`)
- **Theoretical Basis**: Sakata Five Methods / Steve Nison, *Japanese Candlestick Charting Techniques* / Thomas Bulkowski, *Encyclopedia of Candlestick Charts*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Price Zone and Prior Trend Definition (Role: Uptrend Top Zone (Bearish Reversal))
- **Prior Uptrend (Required)**: At the immediately preceding bar $e = t - k$, 10-bar price change ($L_{\text{st}} = 10$) satisfies $C_e - C_{e-10} \ge 1.5 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e > \text{EMA}_{10}(e) \;\land\; \text{EMA}_{10}(e) > \text{EMA}_{10}(e-3)$.
- **Price Zone (Top Zone)**: Pattern highest high $H_{\text{pat}} = \max_{i \in [s, t]} H_i$ lies within the top 25% of the trailing $N_{\text{zone}} = 20$-bar range ($\text{Pos}(H_{\text{pat}}, t, 20) \ge 0.75$).

## 2. Period (Time Axis & Bar Count) Definition
- **Pattern Formation Bars**: Strictly **5 consecutive bars** (interval $[t - 4,\; t]$). Missing bars within the pattern are not permitted.
- **Context Reference Window**: $\text{AvgBody}_{\text{ctx}}$ and $\text{ATR}_{\text{ctx}}$ are computed over the 14 consecutive bars ending at $e = t - 5$ ($[e - 13,\; e]$) (context separation).
- **Prior Trend, Price Zone, and Volume Windows**: Prior trend $L_{\text{st}} = 10$ bars; price zone range $N_{\text{zone}} = 20$ bars; average volume $L_{\text{vol}} = 20$ bars.
- **Signal Active Duration**: `1m` on completion bar $t$ only (single-bar event; retroactive repainting of past bars is prohibited).

## 3. Geometric Ratios and Price Tolerances
5-bar structure $(c_1 \dots c_5)$:
- $c_1$: Large bullish candle ($C_1 > O_1 \;\land\; \text{IsLarge}(c_1)$).
- $c_2$: Gap-up bullish candle $O_2 > C_1 + 0.10 \cdot \text{AvgBody}_{\text{ctx}}$.
- $c_3, c_4$: $\text{IsSmall}$, no early gap intrusion ($\min(C_3, C_4) > O_2$), excluding sustained pullback ($\neg(H_4 < H_3 \land C_4 < C_2)$).
- $c_5$: Large bearish breakdown ($B_5 \ge 1.20 \cdot \text{AvgBody}_{\text{ctx}} \lor \text{IsLarge}(c_5)$), closing below intermediate bodies ($C_5 < \min_{i \in \{2,3,4\}} \min(O_i, C_i)$; strict wick gap: $C_5 < \min(L_2, L_3, L_4)$) and inside the gap ($C_1 < C_5 < O_2$).

## 4. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **Confirmation Bar ($t$)**: `Main = 1m` upon close of $c_5$.
- **Post-Signal Invalidation Level (`InvalidationLevel`; does not retroactively alter past `Main` at bar $t$)**:
  - Post-signal stop level $P_{\text{stop}} = \max(H_2, H_3, H_4)$.

## 5. Current Implementation Gaps and Anomaly/Boundary Contract
- Current implementation omits prior uptrend and top-zone checks.
- **No Exclusive Shadowing**: Evaluated independently using only this pattern's predicates (equivalent to `stopOnFirst: false`).
- **Common Anomaly Contract**: Return `null` when history is insufficient ($t + 1 < 25$); return `0m` without throwing exceptions on OHLC integrity violations, $R_i \le 0$, or $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$.
