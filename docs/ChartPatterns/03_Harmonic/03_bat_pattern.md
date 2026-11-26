# Bat Pattern — Strict Specification (v2.0)

- **Category**: `Harmonic`
- **Indicator Identifier (planned `IndicatorType`)**: `HarmonicBat`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 40 bars (40-bar price zone range for point X + ATR14)
- **Theoretical Basis**: Scott Carney, *Harmonic Trading Vol. 1* (Bat 0.886 PRZ definition)
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A 5-point harmonic reversal pattern characterized by a shallow B-point retracement (0.382–0.500 XA) and a deep 0.886 XA retracement at point D (just shy of point X) for high-precision reversals.

## 2. Price Zone and Prior Trend Definition
- **Initial Leg XA Positioning**:
  - Bullish Bat: Point X lies in the trailing 40-bar low zone ($\text{Pos}(P_X, i_X, 40) \le 0.25$), and $X \to A$ move $\ge 3.0\% \;\land\; \ge 2.5 \cdot \text{ATR}_{\text{ctx}}(i_X-1, 14)$.
  - Bearish Bat: Point X lies in the trailing 40-bar high zone ($\text{Pos}(P_X, i_X, 40) \ge 0.75$).
- **Swing Detection Criteria**: Causally confirmed local extrema ($w \in \{2, 3, 5, 8\}$), evaluated in an **independent Bat-only pool**.

## 3. Period (Time Axis & Bar Count) Definition
- **Total Formation Duration ($i_D - i_X$)**: $[12, 120]$ bars. Each leg $[2, 45]$ bars; time symmetry $\le 6.0$.

## 4. Geometric Ratios and Price Tolerances
- **Minimum Leg Amplitude**: All 4 legs $\ge 2.0\% \;\land\; \ge 0.50 \cdot \text{ATR}_{\text{leg}}$.
- **Strict Structural Ordering and Cleanliness**:
  - Bullish: $P_X < P_D < P_B < P_C < P_A$ (**D must not breach X**), with $H_i \le P_A$ on $(i_A, i_D)$ and $L_i \ge P_X$ on $(i_X, i_D)$.
  - Bearish: $P_X > P_D > P_B > P_C > P_A$.
- **Fibonacci Ratios & Trapezoidal Confidence Score (composite $\text{Score} \ge 0.50$)**:
  - $AB / XA$: Ideal `[0.382, 0.500]`, Tolerance `[0.33, 0.55]` (Weight `0.30`)
  - $BC / AB$: Ideal `[0.382, 0.886]`, Tolerance `[0.33, 0.93]` (Weight `0.15`)
  - $CD / BC$: Ideal `[1.618, 2.618]`, Tolerance `[1.50, 2.75]` (Weight `0.15`)
  - $AD / XA$: Ideal `0.886`, Tolerance `[0.84, 0.95]` (Weight `0.40`, **strictly $< 1.00$**)

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: After $(X, A, B, C)$ are confirmed, price enters the 0.886 XA PRZ (`Main = 0m`).
- **`Confirmed`**: Within $0 \le t - t_{\text{conf}} \le 5$ bars of minimum-window confirmation bar $t_{\text{conf}} = i_D + w_{\min}$ (5-vertex tuple registered as `Consumed`).
- **`Invalidated`**: Closing price breaches point X (Bullish: $C_t < P_X$; Bearish: $C_t > P_X$).

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**: Current `AD/XA` upper bound of `1.036` erroneously permits X-point breaches; 18-pattern shared pool contention drops; unguarded multi-scale duplicate triggers.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
