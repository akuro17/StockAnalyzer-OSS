# Head and Shoulders — Strict Specification (v2.0)

- **Category**: `Pattern Recognition`
- **Indicator Identifier (planned `IndicatorType`)**: `PatternHeadAndShoulders`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Top), 0m, +1m (Inverse)}`)
- **Minimum Required History $N_{\min}$**: 60 bars (for medium-term trend, SMA50, and 60-bar price zone range)
- **Theoretical Basis**: Edwards & Magee / Thomas Bulkowski, *Encyclopedia of Chart Patterns*
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A major top reversal pattern formed in the upper zone of an uptrend, where a central peak (Head) rises higher than the flanking shoulders (Left/Right Shoulders) and completes upon a closing breakout below the neckline connecting the two intermediate troughs (Inverse Head and Shoulders is the exact bullish mirror at a bottom).

## 2. Price Zone and Prior Trend Definition
- **Head and Shoulders Top (Bearish)**:
  - Prior uptrend ($[i_{LS} - 30,\, i_{LS} - 1]$): Prior advance $\dfrac{P_H - L_{\text{pre}}}{L_{\text{pre}}} \ge 0.15$ or $P_H - L_{\text{pre}} \ge 4.0 \cdot \text{ATR}_{\text{ctx}}(i_{LS}-1, 14)$, and $\text{SMA}_{20}(i_{LS}-1) > \text{SMA}_{50}(i_{LS}-1)$.
  - Price zone (high zone): Head peak $P_H$ lies within the top 15% of the trailing $N_{\text{zone}} = 60$-bar range ($\text{Pos}(P_H, i_H, 60) \ge 0.85$).
- **Inverse Head and Shoulders (Bottom / Bullish)**:
  - Prior downtrend ($[i_{LS} - 30,\, i_{LS} - 1]$): Prior decline $\dfrac{H_{\text{pre}} - P_H}{H_{\text{pre}}} \ge 0.15$ or $H_{\text{pre}} - P_H \ge 4.0 \cdot \text{ATR}_{\text{ctx}}(i_{LS}-1, 14)$, and $\text{SMA}_{20}(i_{LS}-1) < \text{SMA}_{50}(i_{LS}-1)$.
  - Price zone (low zone): $\text{Pos}(P_H, i_H, 60) \le 0.15$.
- **Swing Detection Criteria**: Confirmed ZigZag pivots ($\delta = 3.0\%$, minimum swing amplitude $\ge 1.5 \cdot \text{ATR}_{\text{ctx}}$) co-required with left/right $(k_L = 3, k_R = 3)$ local fractal extrema (confirmation bar $i_{\text{conf}} = \max(i_{\text{zz}}, i_{\text{piv}} + 3)$).

## 3. Period (Time Axis & Bar Count) Definition
- **Total Formation Duration (Left Shoulder $i_{LS}$ to Breakout Bar $i_{\text{brk}}$)**: Min $20$ bars / Max $180$ bars.
- **Inter-Pivot Bar Constraints**:
  - Left Shoulder to Head ($i_H - i_{LS}$): $[5, 60]$ bars; Head to Right Shoulder ($i_{RS} - i_H$): $[5, 60]$ bars.
  - Time symmetry: $0.40 \le \dfrac{i_{RS} - i_H}{i_H - i_{LS}} \le 2.50$.
  - Right Shoulder to Breakout ($i_{\text{brk}} - i_{RS}$): $[1, 30]$ bars.

## 4. Geometric Ratios and Price Tolerances
Let the 5 confirmed pivots be $(LS, V_1, H, V_2, RS)$ (High-Low-High-Low-High for Top; symmetric for Inverse).
- **Head Prominence Requirement**:
  - Theoretical mandatory condition: $P_H > \max(P_{LS}, P_{RS})$ (Inverse: $P_H < \min(P_{LS}, P_{RS})$).
  - Tunable prominence parameter (default $\ge 0.15$): $\dfrac{P_H - \max(P_{LS}, P_{RS})}{P_H - \text{Neckline}(i_H)} \ge 0.15$.
- **Shoulder Price Tolerance**:
  - $|P_{LS} - P_{RS}| \le \max\left(0.05 \cdot \max(P_{LS}, P_{RS}),\; 0.50 \cdot \text{ATR}_{\text{ctx}}(i_{RS}-1, 14)\right)$ (default 5%, relaxed upper limit 10%).
  - Both shoulders must lie above the neckline (below the neckline for Inverse).
- **Neckline Depth (Unified with Double Top) and Slope Limit**:
  - Neckline equation: $NL(i) = P_{V1} + \dfrac{P_{V2} - P_{V1}}{i_{V2} - i_{V1}}(i - i_{V1})$; pattern height $H_{\text{pat}} = |P_H - NL(i_H)|$.
  - Minimum depth: $\dfrac{H_{\text{pat}}}{P_H} \ge 0.05 \;\lor\; \left(H_{\text{pat}} \ge 2.5 \cdot \text{ATR}_{\text{ctx}}(i_H-1, 14) \;\land\; \dfrac{H_{\text{pat}}}{P_H} \ge 0.02\right)$ (prevents false negatives on low-volatility large-cap stocks).
  - Neckline slope limit: $\dfrac{|P_{V2} - P_{V1}|}{H_{\text{pat}}} \le 0.35$.

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: All 5 pivots are causally confirmed and satisfy all geometric and duration constraints, but the neckline has not yet been broken (`Main = 0m`).
- **`Confirmed`**:
  - **Top (`Direction = -1m`)**: Within $1 \le i - i_{RS} \le 30$, closing price $C_i < NL(i) - m_{\text{brk}}$, where $m_{\text{brk}} = \max(0.005 \cdot NL(i),\; 0.25 \cdot \text{ATR}_{\text{ctx}}(i-1, 14))$.
  - **Inverse (`Direction = +1m`)**: Closing price $C_i > NL(i) + m_{\text{brk}}$.
  - **Volume Confirmation (default `RequireVolumeConfirmation = true`)**: Breakout bar volume $V_i \ge 1.25 \cdot \text{AvgVol}(i-1, 20)$ (bypassed for instruments with $\text{AvgVol} = 0$).
  - **`Consumed` Tracking**: Register confirmed tuple $(i_{LS}, i_{V1}, i_H, i_{V2}, i_{RS})$ as `Consumed` to prohibit re-triggering after persistence period $T_{\text{persist}}$ (default 5 bars).
- **`Invalidated`**: Before breakout or during the persistence window, closing price exceeds the Head (Top: $C_i > P_H$; Inverse: $C_i < P_H$), or $i - i_{RS} > 30$ bars elapse without breakout.

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation (`MarketStructureDetector.IsHeadAndShoulders{Top|Bottom}Core`)**:
  1. Prior trend, high/low price zone check, inter-peak bar constraints, neckline depth/slope, closing breakout, and `Consumed` tracking are all unimplemented.
  2. Shoulder tolerance is overly loose at `15%`, and a division-by-zero guard for `Math.Max(h1, h3) == 0` is missing.
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
