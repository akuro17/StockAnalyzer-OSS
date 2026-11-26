# Higher High / Higher Low (Bullish Market Structure) — Strict Specification (v2.0)

- **Category**: `Market Structure`
- **Indicator Identifier (`IndicatorType`)**: `MarketStructureHhHl`
- **Output Domain**: Unidirectional (`Main ∈ {null, 0m, 1m}`)
- **Minimum Required History $N_{\min}$**: 20 bars (ATR14 + at least 2 confirmed swing highs and 2 confirmed swing lows)
- **Theoretical Basis**: Dow Theory (fundamental uptrend definition: HH + HL)
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)
- **Symmetric Mirror Indicator (Bearish)**: [04_lower_low_lower_high.md](04_lower_low_lower_high.md) (`MarketStructureLlLh`, sharing `HigherHighHigherLowEngine` via `CoreStructureTrendIndicatorBase`)

## 1. Pattern Overview
A state indicator identifying a healthy bullish market structure where both the latest two confirmed swing highs and latest two confirmed swing lows step strictly higher, and the current closing price remains above the most recent confirmed swing low.

## 2. Price Zone and Prior Trend Definition
- **Current-Price Structural Maintenance Constraint**: Current close $C_t > L_2$ (eliminates false positives after the latest confirmed swing low is broken).
- **Swing Detection Criteria**: Confirmed causal ZigZag pivots (`ExtractConfirmedPivots`, default $\delta = 3.0\%$, minimum swing amplitude $\ge 1.5 \cdot \text{ATR}_{\text{ctx}}$).

## 3. Period (Time Axis & Bar Count) Definition
- **Structural Swing Effective Span**: Distance from oldest to newest of the 4 alternating pivots $(H_1, L_1, H_2, L_2)$ must lie in $[\text{MinSpan}, \text{MaxSpan}]$ bars, where $\text{MaxSpan} = 2 \cdot \text{MaxPivotIntervalBars}$, $\text{geomMaxSpan} = \max(0, \text{MaxSpan} - 1)$, and $\text{MinSpan} = \min\!\big(10,\; \min(2 \cdot \text{MinPivotIntervalBars} + 2,\; \text{geomMaxSpan})\big)$. The lower span bound dynamically adapts within default cap `10` and the geometric maximum span `2 * MaxPivotIntervalBars - 1` (with defaults `Min=4, Max=60`, span is $[10, 120]$ bars; `Validate()` allows `Min >= 2`, `Max >= 15`, `Min <= Max`).
- **Consecutive High Spacing ($i_{H2} - i_{H1}$) and Low Spacing ($i_{L2} - i_{L1}$)**: Each in $[\text{MinPivotIntervalBars}, \text{MaxPivotIntervalBars}]$ (default $[4, 60]$ bars).
- **Recency Limit**: Bars elapsed since the latest confirmed pivot: $t - \max(i_{H2,\text{conf}}, i_{L2,\text{conf}}) \le \text{MaxLastPivotAgeBars}$ (default `40` bars).
- **Required Warmup Bars**: $\max\!\big(500,\; 2 \cdot \text{MaxPivotIntervalBars} + \text{MaxLastPivotAgeBars} + 20\big)$ (`StructureTrendWarmupMarginBars = 20`).

## 4. Geometric Ratios and Price Tolerances
- **Higher High (HH)**: $H_1 > 0 \;\land\; \Delta_H > 0 \;\land\; \dfrac{\Delta_H}{H_1} \ge \dfrac{\text{MinStepUpPercent}}{100} \;\land\; i_{H2} \ge 15 \;\land\; \Delta_H \ge 0.50 \cdot \text{ATR}_{\text{ctx}}(i_{H2}-1, 14)$ ($\Delta_H = H_2 - H_1$, default `MinStepUpPercent = 0.5`).
- **Higher Low (HL)**: $L_1 > 0 \;\land\; \Delta_L > 0 \;\land\; \dfrac{\Delta_L}{L_1} \ge \dfrac{\text{MinStepUpPercent}}{100} \;\land\; i_{L2} \ge 15 \;\land\; \Delta_L \ge 0.50 \cdot \text{ATR}_{\text{ctx}}(i_{L2}-1, 14)$ ($\Delta_L = L_2 - L_1$).
  - When `MinStepUpPercent = 0m`, the percentage check is trivially true and only the ATR condition ($\ge 0.50 \cdot \text{ATR}_{\text{ctx}}$) and $\Delta > 0$ apply. If $i_{\text{later}} < 15$ (ATR unavailable, R-16) or origin price $\le 0$, evaluates to `0m`.
- **Alternation**: Must be the trailing two pairs of strictly alternating confirmed pivots with $H_2 > L_2$.

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: $H_2 > H_1$ holds, but pullback low $L_2$ is not yet confirmed (`Main = 0m`).
- **`Confirmed`**: State indicator outputting `Main = 1m` on every bar satisfying HH & HL conditions and $C_t > L_2$.
- **`Invalidated`**: Transitions to `0m` immediately when $C_t \le L_2$ or a newly confirmed swing high satisfies $H_{\text{new}} \le H_2$.

## 6. Implementation Alignment and Anomaly/Boundary Contract
- **Implementation Structure (v2 Completed)**: Implemented via `CoreMarketStructureHhHlIndicator` (`CoreStructureTrendIndicatorBase`, `TrendSide => +1`) and `HigherHighHigherLowEngine.Run(ctx, p, side: +1)` with full causal enforcement of `CausalZigZag`, $C_t > L_2$, dynamic span $[\text{MinSpan}, \text{MaxSpan}]$, AND step thresholds, and recency cap. Legacy `MarketStructureDetector` (for old `Criteria`) remains frozen and isolated.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
