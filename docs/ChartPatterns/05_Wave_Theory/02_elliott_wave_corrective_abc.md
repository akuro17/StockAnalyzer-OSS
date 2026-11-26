# Elliott Wave Corrective ABC — Strict Specification (v2.0)

- **Category**: `Wave Theory`
- **Indicator Identifier (planned `IndicatorType`)**: `ElliottWaveCorrective`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 40 bars (40-bar origin price zone range + minimum corrective formation)
- **Theoretical Basis**: Ralph Nelson Elliott / Frost & Prechter, *Elliott Wave Principle*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## Visual Reference

![Elliott Wave Corrective ABC](../../ChartPatterns_Images/05_Wave_Theory/02_elliott_wave_corrective_abc.png)

> [!NOTE]
> This chart image is a conceptual visual illustration for reference only. Actual detection results depend on the strict mathematical rules, parameters, and market data, and may not match this illustration.

## 1. Pattern Overview
A wave-theory indicator detecting 3-wave counter-trend corrective structures (Wave A -> Wave B retracement -> Wave C completion across 4 confirmed pivots) following an impulsive move.

## 2. Price Zone and Prior Trend Definition
- **Prior Trend (Existence of a Trend to Correct)**:
  - Bearish Corrective ABC (downward correction): Origin $p_0$ lies in the trailing 40-bar high zone ($\text{Pos}(p_0, i_0, 40) \ge 0.75$).
  - Bullish Corrective ABC (upward correction): Origin $p_0$ lies in the trailing 40-bar low zone ($\text{Pos}(p_0, i_0, 40) \le 0.25$).
- **Swing Detection Criteria**: **4** consecutive causally confirmed ZigZag pivots (`ExtractConfirmedPivots`, $\delta = 5.0\%$), $(p_0, p_1, p_2, p_3)$. Evaluated in a **Corrective-only pool** independent of Impulse.

## 3. Period (Time Axis & Bar Count) Definition
- **Total Formation Duration ($i_3 - i_0$)**: $[9, 120]$ bars. Each wave $d_k \in [2, 50]$ bars; time symmetry $\le 6.0$.

## 4. Geometric Ratios and Price Tolerances
Let $w_A = |p_1 - p_0|,\; w_B = |p_2 - p_1|,\; w_C = |p_3 - p_2|$.
- **Structural Rules (Explicit Separation of Zigzag vs. Flat)**:
  - **Wave B Retracement Cap**: Bullish $p_2 > p_0$ ($w_B / w_A < 1.0$); Bearish $p_2 < p_0$.
  - **Wave C Extension Beyond Wave A (Zigzag Default)**: Default mode requires Bullish $p_3 > p_1$, Bearish $p_3 < p_1$. Set `RequireWaveCExceedWaveA = false` to include Flat corrections ($w_B / w_A \in [0.80, 1.00]$ with truncated C allowed).
- **Fibonacci Ratios (Common Spec §6 trapezoidal score, tolerance $\pm 0.20$, both component scores $> 0$ required, composite $\ge 0.35$)**:
  - $w_B / w_A$: Ideal `[0.382, 0.786]` (Tolerance `[0.182, 0.986]`, Weight `0.40`)
  - $w_C / w_A$: Ideal `[0.618, 1.618]` (Tolerance `[0.418, 1.818]`, Weight `0.60`)

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: $(p_0, p_1, p_2)$ confirmed with Wave C in progress (`Main = 0m`).
- **`Confirmed`**: $p_3$ is causally confirmed as a pivot and $0 \le t - p_3.\text{ConfirmedAtIndex} \le 5$ bars (4-pivot tuple registered as `Consumed`).
- **`Invalidated`**: Wave B breaches origin $p_0$.

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**: Early guard `pivots.Count < 6` at the start of `Detect` blocks detection on 4–5 pivot intervals; $p_3$ validation is missing; non-causal pivots, missing recency checks, and Impulse pool contention drops.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
