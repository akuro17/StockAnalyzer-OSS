# Change of Character (CHoCH) — Strict Specification (v2.0)

- **Category**: `Market Structure`
- **Indicator Identifier (`IndicatorType`)**: `MarketStructureChoch`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 20 bars (full-history 1-pass causal scan sharing state machine with BOS)
- **Theoretical Basis**: Smart Money Concepts (SMC) / Dow Theory (trend reversal definition)
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
An early trend-reversal signal triggered when price closes beyond the Protected Swing (Protected High / Protected Low) that originated the most recent BOS of the prevailing trend, flipping market structure direction.

## 2. Price Zone and Prior Trend Definition
- **Full-History 1-Pass Causal State Machine (Shared with BOS)**: Scans in a single pass from bar $i = 0$ to bar $t$, sharing exact `TrendState` and `ProtectedSwing` state with BOS.
- **Unambiguous Definition of Protected Swing**:
  - Uptrend (`TrendState == +1`) Protected Low $P_{\text{protLow}}$: **The confirmed swing low that originated the impulse wave producing the most recent Bullish BOS (or initial bullish confirmation)** (minor internal pullbacks do not qualify).
  - Downtrend (`TrendState == -1`) Protected High $P_{\text{protHigh}}$: **The confirmed swing high that originated the impulse wave producing the most recent Bearish BOS (or initial bearish confirmation)**.

## 3. Period (Time Axis & Bar Count) Definition
- **Protected Swing Confirmation to Reversal Break Interval**: Min $1$ bar ($i_{\text{brk}} > i_{\text{conf}}$), Max $T_{\max} = 100$ bars.
- **Signal Persistence**: Default `SignalPersistenceBars = 0` (`1m` on reversal confirmation bar only; configurable range `[0, 10]`).

## 4. Geometric Ratios and Price Tolerances
- **Mandatory Closing Break**: Excludes wick-only liquidity sweeps by requiring a closing breach.
- **Margin and Structural Width**: $m_{\text{choch}} = \max(0.001 \cdot P_{\text{prot}},\; 0.10 \cdot \text{ATR}_{\text{ctx}}(t-1, 14))$ (strict inequality when configured to `0m`). The structural distance between the Protected Swing and the prior extremum must be $\ge 2.0 \cdot \text{ATR}_{\text{ctx}}$.

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: Price approaches or wick-tests the Protected Swing without closing beyond it (`Main = 0m`).
- **`Confirmed`**:
  - **Bullish CHoCH (`Direction = +1m`)**: `TrendState == -1` and for unconsumed $P_{\text{protHigh}}$, $C_t > P_{\text{protHigh}} + m_{\text{choch}}$. Immediately transitions `TrendState = +1` and marks $P_{\text{protHigh}}$ as `Consumed`.
  - **Bearish CHoCH (`Direction = -1m`)**: `TrendState == +1` and for unconsumed $P_{\text{protLow}}$, $C_t < P_{\text{protLow}} - m_{\text{choch}}$. Immediately transitions `TrendState = -1` and marks $P_{\text{protLow}}$ as `Consumed`.
  - **Volume Confirmation (optional, default `false`)**: When enabled, $V_t \ge 1.25 \cdot \text{AvgVol}(t-1, 20)$.
- **`Invalidated`**: If price breaks in the prevailing trend direction before breaching the Protected Swing, a BOS fires and the Protected Swing advances to the new origin.

## 6. Implementation Alignment and Anomaly/Boundary Contract
- **v2.0 Implementation Status**: Registered as `CoreMarketStructureChochIndicator` (`StructureShiftEngine`) under `IndicatorType.MarketStructureChoch`, applying default `BreakoutVolumeMultiplier = 1.25m` via `CoreStructureShiftParameter.CreateFor(IndicatorType.MarketStructureChoch)` (preserving JSON persistence discriminator `CoreStructureShiftParameter` and isolating the frozen legacy Screener detector).
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
