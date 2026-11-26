# Break of Structure (BOS) — Strict Specification (v2.0)

- **Category**: `Market Structure`
- **Indicator Identifier (`IndicatorType`)**: `MarketStructureBos`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 20 bars (ATR14 + minimal structural formation; full-history 1-pass causal scan)
- **Theoretical Basis**: Smart Money Concepts (SMC) / Dow Theory (trend continuation definition)
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A trend continuation signal triggered when price decisively breaks and closes beyond the most recent confirmed swing high (in an established uptrend) or confirmed swing low (in an established downtrend) in the direction of the prevailing trend.

## 2. Price Zone and Prior Trend Definition
- **Full-History 1-Pass Causal State Machine Principle (Model P)**:
  - Re-slicing a fixed 100-bar window on every bar resets initial `TrendState` and causes BOS and CHoCH to invert. Therefore, a **single causal state machine scans in one $O(N)$ pass from bar $i = 0$ to the latest bar $t$**.
  - From initial neutral state `TrendState = 0`, the first confirmed swing break establishes trend direction (`+1` / `-1`) only and does not fire a BOS signal.
- **Prior Trend Precondition**: Bullish BOS requires `TrendState == +1`; Bearish BOS requires `TrendState == -1`.
- **Swing Detection Criteria**: Confirmed pivots only (`ConfirmedAtIndex < t`, ZigZag $\delta = 5.0\%$ and minimum amplitude $\ge 1.5 \cdot \text{ATR}_{\text{ctx}}$).

## 3. Period (Time Axis & Bar Count) Definition
- **Reference Swing Confirmation to Break Interval**: Min $1$ bar ($i_{\text{brk}} > i_{\text{conf}}$), Max $T_{\max} = 100$ bars.
- **Signal Persistence**: Default `SignalPersistenceBars = 0` (`1m` on confirmation bar only; configurable range `[0, 10]`).

## 4. Geometric Ratios and Price Tolerances
- **Structural Soundness**: Reference Swing High $P_{\text{SH}}$ (or Swing Low $P_{\text{SL}}$) must be a confirmed swing following a reversal of at least $1.5 \cdot \text{ATR}_{\text{ctx}}$ from the prior opposite confirmed extremum.
- **Closing Breakout Margin**: Excludes wick-only sweeps by requiring a close beyond $m_{\text{bos}} = \max(0.001 \cdot P_{\text{level}},\; 0.10 \cdot \text{ATR}_{\text{ctx}}(t-1, 14))$ (strict inequality when configured to `0m`).

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: Price approaches an unconsumed reference swing without closing beyond it (`Main = 0m`).
- **`Confirmed`**:
  - **Bullish BOS (`Direction = +1m`)**: `TrendState == +1` and for the latest unconsumed confirmed swing high $P_{\text{SH}}$ (`ConfirmedAtIndex < t`), $C_t > P_{\text{SH}} + m_{\text{bos}}$. Upon firing, mark $P_{\text{SH}}$ as `Consumed` and update the confirmed trough that originated this impulse wave as the new Protected Low.
  - **Bearish BOS (`Direction = -1m`)**: `TrendState == -1` and for the latest unconsumed confirmed swing low $P_{\text{SL}}$, $C_t < P_{\text{SL}} - m_{\text{bos}}$. Upon firing, mark $P_{\text{SL}}$ as `Consumed` and update the confirmed peak that originated this impulse leg as the new Protected High.
  - **Volume Confirmation (optional, default `false`)**: When enabled, $V_t \ge 1.15 \cdot \text{AvgVol}(t-1, 20)$.
- **`Invalidated`**: If price closes beyond the Protected Low/High prior to breaking $P_{\text{SH}}$/$P_{\text{SL}}$, the state transitions via CHoCH and flips `TrendState`.

## 6. Implementation Alignment and Anomaly/Boundary Contract
- **v2.0 Implementation Status**: Registered as `CoreMarketStructureBosIndicator` (`StructureShiftEngine`) under `IndicatorType.MarketStructureBos`, with default `BreakoutVolumeMultiplier = 1.15m` via `CoreStructureShiftParameter.CreateFor(IndicatorType.MarketStructureBos)` (preserving JSON persistence discriminator `CoreStructureShiftParameter` and isolating the frozen legacy Screener detector).
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
