# Double Top / Double Bottom — Strict Specification (v2.0)

- **Category**: `Pattern Recognition`
- **Indicator Identifier (planned `IndicatorType`)**: `PatternDoubleTopBottom`
- **Output Domain**: Bidirectional (`Main ∈ {null, 0m, 1m}`, `Direction ∈ {null, -1m (Top), 0m, +1m (Bottom)}`)
- **Minimum Required History $N_{\min}$**: 60 bars (for medium-term trend and 60-bar price zone range)
- **Theoretical Basis**: Thomas Bulkowski, *Encyclopedia of Chart Patterns* (Eve & Adam statistical criteria)
- **Common Mathematical Definitions**: [00_COMMON_MATHEMATICAL_DEFINITIONS.md](../00_COMMON_MATHEMATICAL_DEFINITIONS.md)

## 1. Pattern Overview
A two-point reversal pattern formed at the end of a clear prior trend (high zone / low zone), consisting of two comparable peaks (M-Top) or troughs (W-Bottom) separated by an intermediate neckline of sufficient depth, completed upon a closing breakout through the neckline.

## 2. Price Zone and Prior Trend Definition
- **Double Top (Top / Bearish)**:
  - Prior uptrend ($[i_{P1} - 30,\, i_{P1} - 1]$): Prior advance $\dfrac{P_1 - L_{\text{pre}}}{L_{\text{pre}}} \ge 0.10$ or $P_1 - L_{\text{pre}} \ge 3.0 \cdot \text{ATR}_{\text{ctx}}(i_{P1}-1, 14)$, and $\text{SMA}_{20}(i_{P1}-1) > \text{SMA}_{20}(i_{P1}-11)$.
  - Price zone (high zone): $\text{Pos}(P_1, i_{P1}, 60) \ge 0.85 \;\land\; \text{Pos}(P_2, i_{P2}, 60) \ge 0.80$.
- **Double Bottom (Bottom / Bullish)**:
  - Prior downtrend ($[i_{P1} - 30,\, i_{P1} - 1]$): Prior decline $\dfrac{H_{\text{pre}} - P_1}{H_{\text{pre}}} \ge 0.10$ or $H_{\text{pre}} - P_1 \ge 3.0 \cdot \text{ATR}_{\text{ctx}}(i_{P1}-1, 14)$, and $\text{SMA}_{20}(i_{P1}-1) < \text{SMA}_{20}(i_{P1}-11)$.
  - Price zone (low zone): $\text{Pos}(P_1, i_{P1}, 60) \le 0.15 \;\land\; \text{Pos}(P_2, i_{P2}, 60) \le 0.20$.
- **Swing Detection Criteria**: Confirmed ZigZag pivots ($\delta = 3.0\%$, minimum swing amplitude $\ge 1.5 \cdot \text{ATR}_{\text{ctx}}$) co-required with left/right $(k_L = 3, k_R = 3)$ local fractal extrema.

## 3. Period (Time Axis & Bar Count) Definition
- **Total Formation Duration ($i_{P1}$ to Breakout Bar $i_{\text{brk}}$)**: Min $12$ bars / Max $90$ bars.
- **Inter-Pivot Bar Constraints**:
  - Peak-to-peak / bottom-to-bottom spacing ($i_{P2} - i_{P1}$): **Min $10$ bars, Max $65$ bars**.
  - First extremum to neckline valley/peak ($i_V - i_{P1}$): $[3, 40]$ bars; valley/peak to second extremum ($i_{P2} - i_V$): $[3, 40]$ bars.
  - Second extremum to breakout ($i_{\text{brk}} - i_{P2}$): $[1, 25]$ bars.

## 4. Geometric Ratios and Price Tolerances
Let the 3 confirmed pivots be $(P_1, V, P_2)$.
- **Two-Peak / Two-Bottom Price Tolerance (v2.0 unified single formula)**:
  $$|P_1 - P_2| \le \max\left(0.03 \cdot \max(P_1, P_2),\; 0.50 \cdot \text{ATR}_{\text{ctx}}(i_{P2}-1, 14)\right)$$
  (Default percentage tolerance $3.0\%$, with $0.50 \cdot \text{ATR}_{\text{ctx}}$ floor for low-volatility instruments; relaxed upper limit $5.0\%$).
- **Neckline (Intermediate Valley/Peak) Retracement Depth (Unified with H&S)**:
  - For pullback depth $D_{\text{neck}} = |\min(P_1, P_2) - P_V|$ (Top) / $|P_V - \max(P_1, P_2)|$ (Bottom):
  - Minimum depth: $\dfrac{D_{\text{neck}}}{P_{\text{ref}}} \ge 0.06 \;\lor\; \left(D_{\text{neck}} \ge 2.5 \cdot \text{ATR}_{\text{ctx}}(i_V-1, 14) \;\land\; \dfrac{D_{\text{neck}}}{P_{\text{ref}}} \ge 0.02\right)$.
  - Maximum depth: Retracement relative to the prior trend leg must be $\le 0.65$ (65%) and must not breach the prior trend origin ($L_{\text{pre}}$ / $H_{\text{pre}}$).
- **Intermediate Cleanliness**: No bar $i \in (i_{P1}, i_{P2})$ may close above $\max(P_1, P_2)$ (Top) or below $\min(P_1, P_2)$ (Bottom).

## 5. Signal Confirmation Triggers (`Forming` vs. `Confirmed` & `Invalidated`)
- **`Forming`**: Confirmed pivots $(P_1, V, P_2)$ satisfy all geometric/period rules, while neckline $P_V$ has not yet been broken (`Main = 0m`).
- **`Confirmed`**:
  - **Top (`Direction = -1m`)**: Within $1 \le i - i_{P2} \le 25$, closing price $C_i < P_V - \max(0.005 \cdot P_V,\; 0.25 \cdot \text{ATR}_{\text{ctx}}(i-1, 14))$.
  - **Bottom (`Direction = +1m`)**: Closing price $C_i > P_V + \max(0.005 \cdot P_V,\; 0.25 \cdot \text{ATR}_{\text{ctx}}(i-1, 14))$.
  - **Volume Confirmation (default `RequireVolumeConfirmation = true`)**: $V_i \ge 1.20 \cdot \text{AvgVol}(i-1, 20)$.
  - **`Consumed` Tracking**: Register confirmed tuple $(i_{P1}, i_V, i_{P2})$ as `Consumed`.
- **`Invalidated`**: Before breakout or during persistence, $C_i > \max(P_1, P_2) \cdot 1.01$ (Top), $C_i < \min(P_1, P_2) \cdot 0.99$ (Bottom), or $i - i_{P2} > 25$ bars elapse.

## 6. Current Implementation Gaps and Anomaly/Boundary Contract
- **Gaps vs. Current Implementation (`MarketStructureDetector.IsDouble{Top|Bottom}Core`)**:
  1. Price zone, prior trend, peak spacing $[10, 65]$ bars, neckline depth, and closing breakout are all unimplemented—the direct cause of user-reported false positives (triggers at mid-range positions or excessive bar spacing).
- **Common Anomaly Contract**: Return `null` when history is insufficient; return `0m` without throwing exceptions on OHLC integrity violations, non-positive denominators ($\le 0$), or non-positive prices ($P \le 0$). All price comparisons use `decimal`.
