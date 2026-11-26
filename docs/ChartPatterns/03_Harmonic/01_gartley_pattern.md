# Gartley Pattern (Gartley 222) — Strict Specification (v2.0)

- **Category**: `Harmonic`
- **Indicator Identifier (planned `IndicatorType`)**: `HarmonicGartley`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 40 bars (40-bar price zone range for point X + ATR14)
- **Theoretical Basis**: H.M. Gartley / Scott Carney, *Harmonic Trading Vol. 1*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A 5-point (`X-A-B-C-D`) harmonic reversal pattern where point B retraces 0.618 of the initial impulse leg XA and point D converges at the 0.786 XA Potential Reversal Zone (PRZ) without breaching point X.

## 2. Price Zone and Prior Trend Definition
- **Initial Leg XA Positioning**:
  - Bullish Gartley: Point X lies in the trailing 40-bar low zone ($\text{Pos}(P_X, i_X, 40) \le 0.25$), and $X \to A$ move is $\ge 3.0\% \;\land\; |P_A - P_X| \ge 2.5 \cdot \text{ATR}_{\text{ctx}}(i_X-1, 14)$.
  - Bearish Gartley: Point X lies in the trailing 40-bar high zone ($\text{Pos}(P_X, i_X, 40) \ge 0.75$), with downward impulse $X \to A$.
- **Swing Detection Criteria**: Causally confirmed local extrema across window widths $w \in \{2, 3, 5, 8\}$ (`ConfirmedAtIndex = i + w <= t`). Evaluated in an **independent Gartley-only candidate pool** (isolated from the legacy 18-pattern shared pool).

## 3. Period (Time Axis & Bar Count) Definition
- **Total Formation Duration ($i_D - i_X$)**: $[12, 120]$ bars.
- **Per-Leg Duration**: Each leg $d_k \in [2, 45]$ bars; overall time symmetry $\dfrac{\max(d_k)}{\max(3.0, \min(d_k))} \le 6.0$; AB/CD symmetry $0.33 \le \dfrac{i_D - i_C}{i_B - i_A} \le 3.0$.

## 4. Geometric Ratios and Price Tolerances
Let $XA = |P_A - P_X|,\; AB = |P_B - P_A|,\; BC = |P_C - P_B|,\; CD = |P_D - P_C|,\; AD = |P_D - P_A|$.
- **Minimum Leg Amplitude**: All 4 legs must satisfy $\ge 2.0\% \;\land\; \ge 0.50 \cdot \text{ATR}_{\text{leg}}$ ($\text{ATR}_{\text{leg}} = \text{ATR}_{\text{ctx}}(i_{\text{start}}-1, 14)$).
- **Strict Structural Ordering and Intermediate Cleanliness**:
  - Bullish: $P_X < P_D < P_B < P_C < P_A$ (D must not breach X; C must not exceed A), with $H_i \le P_A$ for all $i \in (i_A, i_D)$ and $L_i \ge P_X$ for all $i \in (i_X, i_D)$.
  - Bearish: $P_X > P_D > P_B > P_C > P_A$, with $L_i \ge P_A$ for $i \in (i_A, i_D)$ and $H_i \le P_X$ for $i \in (i_X, i_D)$.
- **Fibonacci Ratios & Trapezoidal Confidence Score (Common Spec §6, composite $\text{Score} \ge 0.50$; any zero component rejects)**:
  - $AB / XA$: Ideal `0.618`, Tolerance `[0.55, 0.68]` (Weight `0.30`)
  - $BC / AB$: Ideal `[0.382, 0.886]`, Tolerance `[0.33, 0.93]` (Weight `0.15`)
  - $CD / BC$: Ideal `[1.272, 1.618]`, Tolerance `[1.15, 1.75]` (Weight `0.15`)
  - $AD / XA$: Ideal `0.786`, Tolerance `[0.74, 0.84]` (Weight `0.40`, strictly $< 1.00$)

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: After $(X, A, B, C)$ are confirmed, price enters the PRZ ($P_A \mp 0.786 \cdot XA \pm 0.02 \cdot XA$) (`Main = 0m`).
- **`Confirmed`**:
  - Point D is causally confirmed ($i_D + w \le t$). Under the **multi-scale deduplication rule**, when the same 5-vertex tuple $(i_X, i_A, i_B, i_C, i_D)$ confirms across multiple window widths $w$, **only the smallest window $w_{\min}$ confirmation bar $t_{\text{conf}} = i_D + w_{\min}$** is registered as `Consumed`, with active window $0 \le t - t_{\text{conf}} \le 5$ bars.
  - Optional reversal candle confirmation (default `false`): Bullish $C_t > O_t \land C_t > P_D$; Bearish $C_t < O_t \land C_t < P_D$.
- **`Invalidated`**: After entering PRZ or during persistence, closing price breaches point X (Bullish: $C_t < P_X$; Bearish: $C_t > P_X$).

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**: False negatives from 18-pattern shared pool contention, excessive fixed ratio tolerance (`±0.15`) causing overlap with Bat/Butterfly, missing intermediate cleanliness checks, and unguarded multi-scale duplicate firing.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
