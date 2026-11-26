# Elliott Wave Impulse 1-5 — Strict Specification (v2.0)

- **Category**: `Wave Theory`
- **Indicator Identifier (planned `IndicatorType`)**: `ElliottWaveImpulse`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 40 bars (40-bar origin price zone range + minimum impulse formation)
- **Theoretical Basis**: Ralph Nelson Elliott / Frost & Prechter, *Elliott Wave Principle*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A wave-theory indicator detecting 5-wave impulsive structures (6 confirmed pivots) conforming to Elliott's 3 cardinal rules (Wave 2 never retraces beyond Wave 1 origin; Wave 3 is never the shortest impulse wave; Wave 4 never overlaps Wave 1 territory) and Fibonacci proportionality guidelines.

## 2. Price Zone and Prior Trend Definition
- **Origin $p_0$ Price Zone**:
  - Bullish Impulse: Origin $p_0$ lies in the trailing 40-bar low zone ($\text{Pos}(p_0, i_0, 40) \le 0.25$).
  - Bearish Impulse: Origin $p_0$ lies in the trailing 40-bar high zone ($\text{Pos}(p_0, i_0, 40) \ge 0.75$).
- **Swing Detection Criteria**: 6 consecutive causally confirmed ZigZag pivots (`ExtractConfirmedPivots`, $\delta = 5.0\%$, minimum amplitude $\ge 1.5 \cdot \text{ATR}_{\text{ctx}}$), $(p_0 \dots p_5)$. Evaluated in an **Impulse-only pool** independent of Corrective ABC.

## 3. Period (Time Axis & Bar Count) Definition
- **Total Formation Duration ($i_5 - i_0$)**: $[15, 200]$ bars.
- **Per-Wave Duration**: Each wave $d_k = i_k - i_{k-1} \in [2, 60]$ bars; time symmetry $\dfrac{\max(d_k)}{\max(3.0, \min(d_k))} \le 6.0$.

## 4. Geometric Ratios and Price Tolerances
Let $w_k = |p_k - p_{k-1}|$ for $k = 1..5$.
- **Three Cardinal Rules (any violation immediately rejects)**:
  1. **Rule 1**: Bullish $p_2 > p_0$; Bearish $p_2 < p_0$.
  2. **Rule 2**: $\neg(w_3 \le w_1 \land w_3 \le w_5)$, and Bullish $p_3 > p_1$, Bearish $p_3 < p_1$.
  3. **Rule 3**: Strict theory requires Bullish $p_4 > p_1$, Bearish $p_4 < p_1$ (optional real-market noise tolerance `OverlapTolerance`, default `0.05`: $(p_1 - p_4) / w_1 \le 0.05$).
- **Wave 5 Endpoint Requirement (explicit in v2.0)**: By default, Wave 5 must exceed Wave 3's endpoint (Bullish: $p_5 > p_3$; Bearish: $p_5 < p_3$). Truncated fifths (failures) are permitted only when `AllowTruncation = true`.
- **Fibonacci Ratios (Common Spec §6 trapezoidal score, tolerance $\pm 0.20$, composite $\ge 0.35$, at most 1 zero component allowed)**:
  - $w_2 / w_1$: Ideal `[0.382, 0.618]` (Weight `0.20`) / $w_3 / w_1$: Ideal `[1.272, 1.618]` (`0.35`)
  - $w_4 / w_3$: Ideal `[0.236, 0.500]` (Weight `0.20`) / $w_5 / w_1$: Ideal `[0.618, 1.000]` (`0.25`)

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: Confirmed through $p_4$ with Wave 5 in progress (`Main = 0m`).
- **`Confirmed`**: $p_5$ is causally confirmed as a pivot and $0 \le t - p_5.\text{ConfirmedAtIndex} \le 5$ bars (6-pivot tuple registered as `Consumed`).
- **Wave 5 Momentum Divergence (Auxiliary Attribute with Zero-Division Guard)**: For $i \ge 5 \land C_{i-5} > 0$, define $\text{ROC}(i) = \dfrac{C_i - C_{i-5}}{C_{i-5}}$ (otherwise divergence = `false`): Bullish $p_5 > p_3 \land \text{ROC}(i_5) < \text{ROC}(i_3)$; Bearish $p_5 < p_3 \land \text{ROC}(i_5) > \text{ROC}(i_3)$.
- **`Invalidated`**: Any violation of the 3 cardinal rules during formation.

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**: Uses non-causal `ExtractPivots`, lacks recency constraints, suffers from Corrective pool contention drops, and omits origin price zone checks.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
