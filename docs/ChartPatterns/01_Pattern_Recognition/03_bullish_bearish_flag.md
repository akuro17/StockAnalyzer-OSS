# Bullish / Bearish Flag — Strict Specification (v2.0)

- **Category**: `Pattern Recognition`
- **Indicator Identifier (planned `IndicatorType`)**: `PatternFlag`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Bearish), 0m, +1m (Bullish)}`)
- **Minimum Required History $N_{\min}$**: 35 bars (pre-pole ATR14 + pole + flag body)
- **Theoretical Basis**: Edwards & Magee / Thomas Bulkowski (High-Tight Flag & Standard Flag criteria)
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A trend continuation pattern consisting of a sharp, nearly straight price impulse (flagpole) followed by a narrow, gently counter-sloping (or horizontal) parallel consolidation channel (flag body), completed by a closing breakout through the channel boundary in the direction of the pole.

## 2. Price Zone and Prior Trend Definition
- **Flagpole (Sharp Prior Impulse)**:
  - Over pole interval $[i_{\text{pole0}}, i_{\text{pole1}}]$ (length $L_{\text{pole}} \in [3, 15]$ bars):
  - **Bullish Flag**: Gain $\dfrac{H_{\text{pole1}} - L_{\text{pole0}}}{L_{\text{pole0}}} \ge 0.08 \;\land\; (H_{\text{pole1}} - L_{\text{pole0}}) \ge 3.5 \cdot \text{ATR}_{\text{ctx}}(i_{\text{pole0}}-1, 14)$, with bullish candle ratio $\ge 0.60$.
  - **Bearish Flag**: Decline $\dfrac{H_{\text{pole0}} - L_{\text{pole1}}}{H_{\text{pole0}}} \ge 0.08 \;\land\; (H_{\text{pole0}} - L_{\text{pole1}}) \ge 3.5 \cdot \text{ATR}_{\text{ctx}}(i_{\text{pole0}}-1, 14)$, with bearish candle ratio $\ge 0.60$.
  - **Price Zone**: Pole endpoint $i_{\text{pole1}}$ lies in the extreme of the trailing $N_{\text{zone}} = 20$-bar range (Bullish: $\text{Pos} \ge 0.90$; Bearish: $\text{Pos} \le 0.10$).

## 3. Period (Time Axis & Bar Count) Definition
- **Flagpole Duration**: $[3, 15]$ bars.
- **Flag Channel Consolidation Duration**: **Min $5$ bars, Max $25$ bars**.
- **Total Formation Duration (Pole Origin to Breakout Bar)**: $[8, 40]$ bars.
- **Time Ratio**: $L_{\text{flag}} \le 3.0 \cdot L_{\text{pole}} \;\land\; L_{\text{flag}} \le 25$.

## 4. Geometric Ratios and Price Tolerances
Over the flag interval $I = [i_{\text{pole1}}, i_{\text{flagEnd}}]$ (length $n = |I|$, time unit = 1 bar), fit upper boundary $U(i) = a_U i + b_U$ to highs $\{H_i\}$ and lower boundary $L(i) = a_L i + b_L$ to lows $\{L_i\}$ via Theil-Sen regression.
- **Theil-Sen Estimator (computed using mean-centered $y'_i = y_i - \bar{y}$ to prevent `decimal` overflow)**:
  $$a_U = \text{median}\left\{\frac{H_j - H_i}{j - i} \;\middle|\; i, j \in I,\; i < j\right\}, \qquad b_U = \text{median}\left\{H_i - a_U \cdot i \;\middle|\; i \in I\right\}$$
- **ATR-Normalized Floor $R^2_{\text{norm}}$ (Prevents Divergence on Horizontal Flags)**:
  $$SS_{\text{res},U} = \sum_{i \in I} \left(H_i - U(i)\right)^2, \qquad SS_{\text{floor}} = n \cdot \left(0.50 \cdot \text{ATR}_{\text{ctx}}(i_{\text{pole1}}-1, 14)\right)^2$$
  $$R^2_{\text{norm},U} = \max\left(0,\; 1 - \frac{SS_{\text{res},U}}{\max(SS_{\text{tot},U},\; SS_{\text{floor}})}\right), \qquad \bar{R}^2 = \frac{R^2_{\text{norm},U} + R^2_{\text{norm},L}}{2} \ge 0.70$$
- **Parallelism and Independent Touch Count**:
  - Normalized per-bar slope difference: $\dfrac{|a_U - a_L|}{\text{ATR}_{\text{ctx}}} \le 0.15$.
  - **Independent touches**: At least 2 touches each on upper and lower boundaries (within tolerance $|H_i - U(i)| \le \max(0.01 \cdot U(i),\; 0.35 \cdot \text{ATR}_{\text{ctx}})$, counting a new touch only if spacing from prior touch $i - i_{\text{prev}} \ge 2$).
- **Channel Slope Direction and Retracement Depth**:
  - Bullish Flag: $a_U / \text{ATR}_{\text{ctx}} \le +0.05$ (downward or horizontal). Bearish Flag: $a_L / \text{ATR}_{\text{ctx}} \ge -0.05$.
  - Maximum counter-trend retracement depth: $\dfrac{D_{\text{flag}}}{W_{\text{pole}}} \in [0.10,\; 0.50]$ (>50% invalidates); average channel width $\overline{U(i) - L(i)} \le 0.35 \cdot W_{\text{pole}}$.

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: Pole, parallel channel, and retracement $\le 50\%$ conditions are satisfied, awaiting breakout (`Main = 0m`).
- **`Confirmed`**:
  - **Bullish Flag (`Direction = +1m`)**: Immediately after flag interval at bar $i$, $C_i > U(i) + \max(0.003 \cdot U(i),\; 0.20 \cdot \text{ATR}_{\text{ctx}}(i-1, 14)) \;\land\; C_i > O_i$.
  - **Bearish Flag (`Direction = -1m`)**: $C_i < L(i) - \max(0.003 \cdot L(i),\; 0.20 \cdot \text{ATR}_{\text{ctx}}(i-1, 14)) \;\land\; C_i < O_i$.
  - **Volume Confirmation (default `RequireVolumeConfirmation = true`)**: $\bar{V}_{\text{flag}} < 0.85 \cdot \bar{V}_{\text{pole}} \;\land\; V_i \ge 1.30 \cdot \bar{V}_{\text{flag}}$.
- **`Invalidated`**: Flag retracement depth exceeds 50% of pole height, or flag consolidation exceeds 25 bars.

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation**:
  1. The current Screener hardcodes `promoteFlagsAndPennants = false` and resolves only `BullishFlag`, resulting in permanent `0m` output (complete non-detection).
  2. Maximum flag bar count (25 bars), 50% retracement cap, independent touch spacing, and volume contraction/breakout expansion are unimplemented.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
