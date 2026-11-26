# Granville's Law Buy Signal (Rules B1–B4) — Strict Specification (v2.0)

- **Category**: `Trading Rules`
- **Indicator Identifier (planned `IndicatorType`)**: `GranvilleBuySignal`
- **Output Domain**: Unidirectional (`Main ∈ {null, 0m, 1m}`, hidden secondary series `RuleCode ∈ {null, 0m, +1m, +2m, +3m, +4m}`)
- **Minimum Required History $N_{\min}$**: 206 bars (`MaPeriod(200) + SlopePeriod(5) + 1`)
- **Theoretical Basis**: Joseph E. Granville, *A Strategy of Daily Stock Market Timing for Maximum Profit*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A rule-based trading indicator strictly evaluating Granville's 4 Buy Rules (moving average slope, golden crossover, support bounce, and deep downward deviation) against a long-term Simple Moving Average (default 200-bar SMA), completely independent of the Sell indicator.

## 2. Price Zone and Prior Trend Definition
- **Moving Average Slope State ($P_{\text{ma}} = 200,\; P_{\text{slope}} = 5,\; \theta_{\text{flat}} = 0.05\%$)**:
  $$\text{slope}_t = \text{SMA}_{200}(t) - \text{SMA}_{200}(t - 5), \qquad \text{slopePct}_t = \frac{|\text{slope}_t|}{\text{SMA}_{200}(t)} \times 100$$
  - Rising: $\text{slope}_t > 0 \land \text{slopePct}_t > 0.05\%$; Falling: $\text{slope}_t < 0 \land \text{slopePct}_t > 0.05\%$; Flat: otherwise.
- **Prior Position Preconditions per Rule**:
  - **B1**: Upward breakout from below a flat or rising MA ($C_{t-1} \le \text{SMA}_{t-1}$).
  - **B2**: Temporary downward dip below a rising MA.
  - **B3**: Pullback toward and bounce off a rising MA while remaining above it ($C_{t-1} > \text{SMA}_{t-1}$).
  - **B4**: Deep downward deviation below a falling MA (oversold zone).

## 3. Period (Time Axis & Bar Count) Definition
- **Minimum Warmup Bars**: $N_{\min} = P_{\text{ma}} + P_{\text{slope}} + 1 = 206$ bars (`null` for $t < 205$).
- **B4 Consecutive-Bar Suppression Option**: `B4TriggerMode` (`EveryBar` legacy-compatible default / `FirstEntryOrReversal` firing only on first entry or bullish reversal bar $C_t > O_t$).

## 4. Geometric Ratios and Price Tolerances
- **Deviation Rate**: $\text{dev}_t = \dfrac{C_t - \text{SMA}_t}{\text{SMA}_t} \times 100$ (B4 threshold: $< -10.0\%$).
- **Support Proximity (B3)**: $\text{proxLow}_t = \dfrac{|L_t - \text{SMA}_t|}{\text{SMA}_t} \times 100 \le 0.5\%$.
- **Crossover Predicates**: $\text{crossedAbove}_t \iff (C_{t-1} \le \text{SMA}_{t-1} \land C_t > \text{SMA}_t)$; $\text{crossedBelow}_t \iff (C_{t-1} \ge \text{SMA}_{t-1} \land C_t < \text{SMA}_t)$.

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
Evaluates the 4 Buy Rules **completely independently of the Sell indicator** in priority order (B4 → B1 → B2 → B3), outputting `Main = 1m` and `RuleCode ∈ {+1..+4}` when met:
1. **B4 (`+4`)**: `isMaFalling && dev_t < -10.0m`
2. **B1 (`+1`)**: `(isMaFlat || isMaRising) && crossedAbove_t`
3. **B2 (`+2`)**: `isMaRising && crossedBelow_t`
4. **B3 (`+3`)**: `isMaRising && C_{t-1} > SMA_{t-1} && C_t > SMA_t && proxLow_t <= 0.5m && C_t > L_t`
- **Volume Confirmation (optional, default `false`)**: When enabled for B1, $V_t \ge 1.20 \cdot \text{AvgVol}(t-1, 20)$.
- **Invalidation / Anomaly**: Returns `null` if `SMA_t <= 0` or `SMA_{t-1} <= 0`.

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**: Current B3 lacks the prior-bar above-MA check ($C_{t-1} > \text{SMA}_{t-1}$), and B4 fires unconditionally on every consecutive bar during a decline.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
