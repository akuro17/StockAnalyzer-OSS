# Butterfly Pattern — Strict Specification (v2.0)

- **Category**: `Harmonic`
- **Indicator Identifier (planned `IndicatorType`)**: `HarmonicButterfly`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 40 bars (40-bar extremum range for point D + ATR14)
- **Theoretical Basis**: Bryce Gilmore / Scott Carney, *Harmonic Trading Vol. 1*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
An extension-type 5-point harmonic reversal pattern where point B forms a deep 0.786 retracement of XA and point D extends strictly beyond point X to the 1.272–1.618 XA extension zone to trigger a reversal at new price extremes.

## 2. Price Zone and Prior Trend Definition
- **Initial Leg XA and Point D Extremum Zone**:
  - Bullish Butterfly: $X \to A$ move $\ge 3.0\% \;\land\; \ge 2.5 \cdot \text{ATR}_{\text{ctx}}(i_X-1, 14)$; point D makes a new low beyond X within the trailing 40-bar bottom zone ($\text{Pos}(P_D, i_D, 40) \le 0.15$).
  - Bearish Butterfly: Downward impulse $X \to A$; point D makes a new high beyond X within the trailing 40-bar top zone ($\text{Pos}(P_D, i_D, 40) \ge 0.85$).
- **Swing Detection Criteria**: Causally confirmed local extrema ($w \in \{2, 3, 5, 8\}$), evaluated in an **independent Butterfly-only pool**.

## 3. Period (Time Axis & Bar Count) Definition
- **Total Formation Duration ($i_D - i_X$)**: $[12, 120]$ bars. Each leg $[2, 45]$ bars; time symmetry $\le 6.0$.

## 4. Geometric Ratios and Price Tolerances
- **Minimum Leg Amplitude**: All 4 legs $\ge 2.0\% \;\land\; \ge 0.50 \cdot \text{ATR}_{\text{leg}}$.
- **Strict Structural Ordering and Cleanliness**:
  - Bullish: $P_D < P_X < P_B < P_C < P_A$ (**D must strictly breach below X**), with $H_i \le P_A$ on $(i_A, i_C)$ and $L_i \ge P_X$ on $(i_X, i_B)$.
  - Bearish: $P_D > P_X > P_B > P_C > P_A$ (**D must strictly exceed above X**).
- **Fibonacci Ratios & Trapezoidal Confidence Score (composite $\text{Score} \ge 0.50$)**:
  - $AB / XA$: Ideal `0.786`, Tolerance `[0.72, 0.85]` (Weight `0.30`)
  - $BC / AB$: Ideal `[0.382, 0.886]`, Tolerance `[0.33, 0.93]` (Weight `0.15`)
  - $CD / BC$: Ideal `[1.618, 2.618]`, Tolerance `[1.50, 2.75]` (Weight `0.15`)
  - $AD / XA$: Ideal `[1.272, 1.618]`, Tolerance `[1.20, 1.68]` (Weight `0.40`, strictly $> 1.00$)

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: After $(X, A, B, C)$ are confirmed, price breaks beyond X and enters PRZ ($1.272 \sim 1.618 \cdot XA$) (`Main = 0m`).
- **`Confirmed`**: Within $0 \le t - t_{\text{conf}} \le 5$ bars of minimum-window confirmation bar $t_{\text{conf}} = i_D + w_{\min}$ (5-vertex tuple registered as `Consumed`).
- **`Invalidated`**: $AD / XA > 1.75$ (over-extension into Crab pattern territory).

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**: 18-pattern shared pool contention drops, overly wide `AB/XA` tolerance (`±0.15`), and unguarded multi-scale duplicate triggers.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
