# Granville's Law Sell Signal (Rules S1–S4) — Strict Specification (v2.0)

- **Category**: `Trading Rules`
- **Indicator Identifier (planned `IndicatorType`)**: `GranvilleSellSignal`
- **Output Domain**: Unidirectional (`Main ∈ {null, 0m, 1m}`, hidden secondary series `RuleCode ∈ {null, 0m, -1m, -2m, -3m, -4m}`)
- **Minimum Required History $N_{\min}$**: 206 bars (`MaPeriod(200) + SlopePeriod(5) + 1`)
- **Theoretical Basis**: Joseph E. Granville, *A Strategy of Daily Stock Market Timing for Maximum Profit*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## Visual Reference

![Granville's Law Sell Signal (Rules S1–S4)](../../ChartPatterns_Images/04_Trading_Rules/02_granvilles_law_sell_signal.png)

> [!NOTE]
> This chart image is a conceptual visual illustration for reference only. Actual detection results depend on the strict mathematical rules, parameters, and market data, and may not match this illustration.

## 1. Pattern Overview
A rule-based trading indicator strictly evaluating Granville's 4 Sell Rules (moving average slope, breakdown crossover, resistance rejection, and deep upward deviation) against a long-term Simple Moving Average (default 200-bar SMA), completely independent of the Buy indicator.

## 2. Price Zone and Prior Trend Definition
- **Moving Average Slope State**: Exact mirror of Buy Signal ($P_{\text{ma}} = 200,\; P_{\text{slope}} = 5,\; \theta_{\text{flat}} = 0.05\%$).
- **Prior Position Preconditions per Rule**:
  - **S1**: Downward breakdown from above a flat or falling MA.
  - **S2**: Temporary upward pop above a falling MA.
  - **S3**: Rally toward and rejection off a falling MA while remaining below it ($C_{t-1} < \text{SMA}_{t-1}$).
  - **S4**: Deep upward deviation above a rising MA (overbought zone).

## 3. Period (Time Axis & Bar Count) Definition
- **Minimum Warmup Bars**: $N_{\min} = 206$ bars (`null` for $t < 205$).
- **S4 Consecutive-Bar Suppression Option**: `S4TriggerMode` (`EveryBar` legacy-compatible default / `FirstEntryOrReversal` firing only on first entry or bearish reversal bar $C_t < O_t$).

## 4. Geometric Ratios and Price Tolerances
- **Deviation Rate**: $\text{dev}_t > +10.0\%$ (S4 threshold).
- **Resistance Proximity (S3)**: $\text{proxHigh}_t = \dfrac{|H_t - \text{SMA}_t|}{\text{SMA}_t} \times 100 \le 0.5\%$.

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
Evaluates the 4 Sell Rules **completely independently of the Buy indicator** (legacy compatibility mode optionally allows `Buy == None` gating) in priority order (S4 → S1 → S2 → S3), outputting `Main = 1m` and `RuleCode ∈ {-1..-4}` when met:
1. **S4 (`-4`)**: `isMaRising && dev_t > +10.0m`
2. **S1 (`-1`)**: `(isMaFlat || isMaFalling) && crossedBelow_t`
3. **S2 (`-2`)**: `isMaFalling && crossedAbove_t`
4. **S3 (`-3`)**: `isMaFalling && C_{t-1} < SMA_{t-1} && C_t < SMA_t && proxHigh_t <= 0.5m && C_t < H_t`
- **Invalidation / Anomaly**: Returns `null` if `SMA_t <= 0` or `SMA_{t-1} <= 0`.

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**: Current logic unconditionally suppresses Sell signals on any bar where Buy triggers (e.g., simultaneous B1 and S4). Decoupling into independent indicators ensures symmetric evaluation.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
