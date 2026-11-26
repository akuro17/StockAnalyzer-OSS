# StockAnalyzer Chart Patterns — Common Mathematical Definitions & Detection Foundation Specification (v2.0 Revised)

This document defines the Single Source of Truth (SSoT) for the strict mathematical definitions referenced across all 57 individual indicator specifications in StockAnalyzer's "Chart Patterns" suite across 6 categories (`PatternFlagIndicatorCatalog` SSoT: 43 Candlestick Patterns, 3 Pattern Recognition formations, 4 Market Structure indicators, 3 Harmonic patterns, 2 Elliott Wave indicators, and 2 Trading Rules; the legacy Screener `Criteria` compatibility map `LegacyCriteriaNameMap` covers 56 types). It specifies the OHLCV data contract, common candlestick predicates, EMA/SMA/ATR calculations, prior trend, price zone position, causal swing/ZigZag extraction, Fibonacci confidence scoring, and lifecycle state transitions (`Forming` / `Confirmed` / `Invalidated` / `Consumed`).

## 1. Notation, OHLCV Integrity, and Output Domain Contract

### 1.1 Base Data and Integrity Guards
- Let the time-series bar sequence be $C_0, C_1, \dots, C_t$ (where $t$ is the latest bar under evaluation, and all price and ratio arithmetic uses the `decimal` type).
- Each bar $C_i = (O_i, H_i, L_i, C_i, V_i)$ must satisfy the following **OHLCV integrity condition**:
  $$H_i \ge \max(O_i, C_i) \;\land\; L_i \le \min(O_i, C_i) \;\land\; H_i \ge L_i \;\land\; O_i > 0 \;\land\; H_i > 0 \;\land\; L_i > 0 \;\land\; C_i > 0 \;\land\; V_i \ge 0$$
  If any bar within the evaluation window violates integrity, no exception shall be thrown; the evaluation result for that bar shall be `0m` (not triggered).
- **Strict Causality (No Look-Ahead)**: The output value $y_t$ at bar $t$ must depend solely on $C_0, \dots, C_t$. Referencing future bars $C_{t+1}$ onward or retroactively rewriting past bar outputs (repainting) is strictly prohibited.
- **Candlestick Primitives**:
  - Real body length: $B_i = |C_i - O_i|$; Total range: $R_i = H_i - L_i$
  - Upper shadow length: $U_i = H_i - \max(O_i, C_i)$; Lower shadow length: $D_i = \min(O_i, C_i) - L_i$
  - Bullish candle: $C_i > O_i$; Bearish candle: $C_i < O_i$

### 1.2 Output Domain and Multi-Candidate Tie-Breaker Contract (Unified Across All 57 Indicators)
To maintain complete alignment with StockAnalyzer Screener conditions (`Main > 0`) and fixed sub-window Y-axes (`[0m, 1.2m]`), all 57 indicators adhere to the following dual-series output contract:
- **Primary Series `Main` (all 57 indicators)**: $\text{Main}_t \in \{\text{null},\; 0\text{m},\; 1\text{m}\}$
  - `null`: Insufficient history ($t + 1 < N_{\min}$) or required reference values (SMA/ATR, etc.) cannot be computed.
  - `1m`: Pattern/signal is established (`Confirmed`) at bar $t$.
  - `0m`: Not established (`None` / `Forming` / `Invalidated`).
- **Secondary Series `Direction` (10 bidirectional indicators only; hidden on chart via `IndicatorChartSuppression`)**: $\text{Direction}_t \in \{\text{null},\; -1\text{m},\; 0\text{m},\; +1\text{m}\}$
  - `Main_t == null` $\iff$ `Direction_t == null`
  - `Main_t == 0m` $\implies$ `Direction_t == 0m`
  - `Main_t == 1m` $\implies$ `Direction_t ∈ {-1m (Bearish), +1m (Bullish)}`
- **Same-Bar Multi-Candidate Selection Rule (Tie-Breaker)**:
  1. For indicators with a confidence score (Harmonic / Elliott / Flag), prefer the candidate with the highest score.
  2. If tied on score (or for unscored indicators), prefer the candidate with the latest confirmed structural point (largest `ConfirmedAtIndex`).
  3. If still tied and Bullish (`+1m`) conflicts with Bearish (`-1m`), deterministically prefer Bullish (`+1m`) by explicit convention.

### 1.3 Unified Notation for Pattern Start and Context End
When evaluating a $k$-bar candlestick pattern at bar $t$:
- **Pattern formation interval**: $[s,\; t]$ where $s = t - k + 1$ ($k$ bars total).
- **Context end bar (bar immediately preceding the pattern)**: $e = s - 1 = t - k$.

## 2. Strict Definitions of Moving Averages (SMA/EMA), Volatility (ATR), and Volume

### 2.1 SMA and EMA Initialization and Recurrence
- **SMA (period $N$)**: Undefined (`null`) for $i < N - 1$; for $i \ge N - 1$, $\text{SMA}_N(i) = \dfrac{1}{N} \sum_{j=i-N+1}^{i} C_j$.
- **EMA (period $N$)**:
  - Smoothing factor: $\alpha = \dfrac{2\text{m}}{N + 1}$
  - Initial value ($i = N - 1$): $\text{EMA}_N(N - 1) = \text{SMA}_N(N - 1)$
  - Recurrence ($i \ge N$): $\text{EMA}_N(i) = \alpha \cdot C_i + (1\text{m} - \alpha) \cdot \text{EMA}_N(i - 1)$

### 2.2 Context-Separated ATR and Average Real Body (Unified Simple Average)
To guarantee reproducibility, zero-allocation simplicity, and consistency with the existing `CandlePatternDetector`, the reference $\text{ATR}$ across all indicators is unified as the **Simple Moving Average of True Range (default $L_{\text{atr}} = 14$)**:

$$\text{TR}_i = \max\left(H_i - L_i,\; |H_i - C_{i-1}|,\; |L_i - C_{i-1}|\right) \quad (i \ge 1,\quad \text{TR}_0 = H_0 - L_0)$$

$$\text{ATR}_{\text{ctx}}(e, L_{\text{atr}}) = \frac{1}{L_{\text{atr}}} \sum_{i=e-L_{\text{atr}}+1}^{e} \text{TR}_i, \qquad \text{AvgBody}_{\text{ctx}}(e, L_{\text{atr}}) = \frac{1}{L_{\text{atr}}} \sum_{i=e-L_{\text{atr}}+1}^{e} B_i$$

*Notes:*
- To guarantee 14 strict True Range bars with prior closes, $e \ge L_{\text{atr}}$ (index 14 or greater) is required (`PatternMath.TryAtrCtx`: returns `false` and treats the pattern as not triggered if $[e - L_{\text{atr}}, e]$ is out of range or contains an OHLCV-inconsistent bar).
- For Harmonic patterns, the per-leg volatility baseline $\text{ATR}_{\text{leg}}$ is defined as $\text{ATR}_{\text{ctx}}(e, 14)$ at the bar immediately preceding the leg start, $e = i_{\text{start}} - 1$.
- **Mathematical Equivalence with $O(N)$ Lazy Prefix-Sum Cache**: `PatternSeriesContext` (`TryGetContextStats` / `BuildContextPrefixSums`) computes $\text{ATR}_{\text{ctx}}$ and $\text{AvgBody}_{\text{ctx}}$ in $O(1)$ using $O(N)$ prefix sums only when all valid bars satisfy $H_i \le 1{,}000{,}000\text{m}$ and `.Scale <= 18` for all OHLC values (zero-allocation check); for series exceeding these bounds, it falls back to direct summation in `CandlePatternDetector.ComputeContext` to preserve bit-exact `decimal` equivalence.

### 2.3 Average Volume and Missing-Volume Handling
$$\text{AvgVol}(e, L_{\text{vol}}) = \frac{1}{L_{\text{vol}}} \sum_{i=e-L_{\text{vol}}+1}^{e} V_i \qquad (\text{default } L_{\text{vol}} = 20)$$

- If `RequireVolumeConfirmation == false`, or if $\text{AvgVol}(e, 20) = 0$ across the instrument (e.g., FX or indices without volume data), the volume condition is bypassed.
- If `RequireVolumeConfirmation == true` and $\text{AvgVol}(e, 20) > 0$, a current bar with $V_t \le 0$ fails the volume condition (`false`).

## 3. Common Candlestick Predicate Macros (Single Source of Truth)

Using $\text{AvgBody}_{\text{ctx}} = \text{AvgBody}_{\text{ctx}}(e, 14)$ and $\text{ATR}_{\text{ctx}} = \text{ATR}_{\text{ctx}}(e, 14)$ at context end bar $e = t - k$, the following predicates are defined. If $\text{AvgBody}_{\text{ctx}} \le 0 \land \text{ATR}_{\text{ctx}} \le 0$, all predicates evaluate to `false`.

- **Large Body `IsLarge(c_i)`**:
  $$R_i > 0 \;\land\; B_i \ge \max\left(1.50 \cdot \text{AvgBody}_{\text{ctx}},\; 0.60 \cdot \text{ATR}_{\text{ctx}}\right) \;\land\; \frac{B_i}{R_i} \ge 0.60$$
- **Small Body `IsSmall(c_i)`**:
  $$R_i > 0 \;\land\; B_i \le 0.80 \cdot \text{AvgBody}_{\text{ctx}} \;\land\; \frac{B_i}{R_i} \le 0.50$$
- **Marubozu `IsMarubozu(c_i)`**:
  $$R_i > 0 \;\land\; B_i \ge \max\left(1.50 \cdot \text{AvgBody}_{\text{ctx}},\; 0.80 \cdot \text{ATR}_{\text{ctx}}\right) \;\land\; \frac{B_i}{R_i} \ge 0.90 \;\land\; U_i \le 0.05 \cdot R_i \;\land\; D_i \le 0.05 \cdot R_i$$
- **Doji `IsDoji(c_i)`**:
  - Normal mode (standalone Doji): $R_i \ge 0.50 \cdot \text{ATR}_{\text{ctx}} \;\land\; B_i \le 0.05 \cdot R_i$
  - Isolated star mode (exclusively for star candle $c_2$ in `Morning/Evening Star` and `Abandoned Baby`): $R_i = 0 \;\lor\; B_i \le 0.05 \cdot R_i$
- **Gap Definitions**:
  - **Real-body gap up**: $O_{i+1} > C_i + 0.10 \cdot \text{AvgBody}_{\text{ctx}}$ (comparing upper/lower body edges according to candle polarity)
  - **Real-body gap down**: $O_{i+1} < C_i - 0.10 \cdot \text{AvgBody}_{\text{ctx}}$
  - **Full wick-to-wick gap**: Upward $L_{i+1} > H_i$; Downward $H_{i+1} < L_i$

### 3.1 Minimum Required History $N_{\min}$ for Candlestick Indicators
Taking the maximum of context-separated ATR ($L_{\text{atr}} + 1 = 15$), short-term prior trend ($L_{\text{st}} + 1 = 11$), price zone range ($N_{\text{zone}} = 20$), average volume ($L_{\text{vol}} = 20$), and $\text{EMA}_{20}(e)$ ($20$), the minimum required history for a $k$-bar candlestick indicator is unified as:

$$N_{\min}(k) = 20 + k \text{ bars} \qquad (k=1 \Rightarrow 21,\; k=2 \Rightarrow 22,\; k=3 \Rightarrow 23,\; k=4 \Rightarrow 24,\; k=5 \Rightarrow 25)$$

## 4. Prior Trend and Price Zone Position

### 4.1 Short-Term Prior Trend (for Candlestick Patterns & Flags, $e = s - 1$, default $L_{\text{st}} = 10$)
- **Uptrend (`PriorTrend == +1`)**: $C_e - C_{e - 10} \ge 1.5 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e > \text{EMA}_{10}(e) \;\land\; \text{EMA}_{10}(e) > \text{EMA}_{10}(e - 3)$
- **Downtrend (`PriorTrend == -1`)**: $C_{e - 10} - C_e \ge 1.5 \cdot \text{ATR}_{\text{ctx}}(e, 14) \;\land\; C_e < \text{EMA}_{10}(e) \;\land\; \text{EMA}_{10}(e) < \text{EMA}_{10}(e - 3)$
- **No Trend (`PriorTrend == 0`)**: Otherwise.

### 4.2 Medium-Term Prior Trend (for Chart Formations, closed interval $[s - L_{\text{mt}}, s - 1]$, default $L_{\text{mt}} = 30$)
To prevent contaminating the trend check with the pattern's own starting price $s$, evaluation is strictly restricted to the **closed interval $[s - L_{\text{mt}}, s - 1]$ (excluding $s$)**:
- **Prior Uptrend**: For $L_{\text{pre}} = \min_{i \in [s - L_{\text{mt}},\, s - 1]} L_i$, $\dfrac{P_{\text{peak1}} - L_{\text{pre}}}{L_{\text{pre}}} \ge R_{\text{pre}}$ (default $0.10$, or $\ge 3.0 \cdot \text{ATR}_{\text{ctx}}(s-1, 14)$) and $\left(\text{SMA}_{20}(s-1) > \text{SMA}_{50}(s-1) \;\lor\; \text{SMA}_{20}(s-1) > \text{SMA}_{20}(s-11)\right)$.
- **Prior Downtrend**: For $H_{\text{pre}} = \max_{i \in [s - L_{\text{mt}},\, s - 1]} H_i$, $\dfrac{H_{\text{pre}} - P_{\text{trough1}}}{H_{\text{pre}}} \ge R_{\text{pre}}$ (default $0.10$, or $\ge 3.0 \cdot \text{ATR}_{\text{ctx}}(s-1, 14)$) and $\left(\text{SMA}_{20}(s-1) < \text{SMA}_{50}(s-1) \;\lor\; \text{SMA}_{20}(s-1) < \text{SMA}_{20}(s-11)\right)$.

### 4.3 Price Zone Percentile (`Pos`)
Over the trailing $N_{\text{zone}}$ bars ending at reference bar $r$ ($20$ bars for reversal candlesticks, $60$ bars for major chart formations):

$$H_{\max} = \max_{i \in [r - N_{\text{zone}} + 1,\, r]} H_i, \qquad L_{\min} = \min_{i \in [r - N_{\text{zone}} + 1,\, r]} L_i$$

$$\text{Pos}(P, r, N_{\text{zone}}) = \begin{cases} \text{clamp}\left(\dfrac{P - L_{\min}}{H_{\max} - L_{\min}},\; 0,\; 1\right) & (H_{\max} > L_{\min}) \\ \text{Undefined (condition fails)} & (H_{\max} = L_{\min}) \end{cases}$$

## 5. Causal Swing Extraction and ZigZag Algorithm

### 5.1 Fractal Swing (Left $k_L$, Right $k_R$, default $3, 3$)
- When consecutive equal highs or equal lows occur, the **last bar** is selected as the representative extremum (left uses $\le$, right uses strict $<$ for uniqueness).
- Confirmation index for pivot bar $i$ is $i_{\text{fractal\_conf}} = i + k_R \le t$.

### 5.2 Deterministic Causal ZigZag (`ExtractConfirmedPivots`)
1. **Bootstrap (Initial Direction)**: At $i = 0$, initialize provisional extrema $H_{\text{ext}} = H_0, L_{\text{ext}} = L_0$. Scan $i = 1, 2, \dots$ and determine the initial direction on the first bar that satisfies either the downward reversal condition (confirming a peak -> downtrend leg) or upward reversal condition (confirming a trough -> uptrend leg).
2. **Symmetric Reversal Conditions (with Integrated Minimum ATR Amplitude)**:
   - Peak confirmation (up -> down): $H_{\text{last}} > 0 \;\land\; \dfrac{H_{\text{last}} - L_i}{H_{\text{last}}} \ge \delta \;\land\; (H_{\text{last}} - L_i) \ge \theta_{\text{atr\_piv}} \cdot \text{ATR}_{\text{ctx}}(i-1, 14)$
   - Trough confirmation (down -> up): $L_{\text{last}} > 0 \;\land\; \dfrac{H_i - L_{\text{last}}}{L_{\text{last}}} \ge \delta \;\land\; (H_i - L_{\text{last}}) \ge \theta_{\text{atr\_piv}} \cdot \text{ATR}_{\text{ctx}}(i-1, 14)$
   - *Integrating the minimum ATR amplitude (default $\theta_{\text{atr\_piv}} = 1.5$) directly into the reversal condition prevents violation of strict peak/trough alternation.*
3. **Same-Bar Collision Rule**: If during an upward leg bar $i$ makes a new high $H_i > H_{\text{last}}$ and its low $L_i$ simultaneously meets the downward reversal condition, intra-bar path ordering is unknown; therefore, **simultaneous confirmation on the same bar is prohibited** and deferred to subsequent bars (symmetric for downward legs).
4. **Confirmation Index with Fractal Co-Requirement**: $i_{\text{conf}} = \max(i_{\text{zigzag\_conf}},\; i_{\text{piv}} + k_R)$. Once confirmed, pivots are immutable.

## 6. Fibonacci Confidence Score Function (Shared by Harmonic & Elliott)

For a measured ratio $r$, ideal interval $[I_{\min}, I_{\max}]$ (single target: $I_{\min} = I_{\max}$), and tolerance interval $[A_{\min}, A_{\max}]$, the trapezoidal membership function $S(r) \in [0, 1]$ is defined as:

$$S(r) = \begin{cases} 1.0 & (I_{\min} \le r \le I_{\max}) \\ \dfrac{r - A_{\min}}{I_{\min} - A_{\min}} & (A_{\min} < r < I_{\min},\; I_{\min} > A_{\min}) \\ \dfrac{A_{\max} - r}{A_{\max} - I_{\max}} & (I_{\max} < r < A_{\max},\; A_{\max} > I_{\max}) \\ 0.0 & (\text{otherwise}) \end{cases}$$

The composite score is $\text{Score} = \sum_k w_k \cdot S_k(r_k)$ with weights $w_k$ satisfying $\sum w_k = 1$.

## 7. Pattern Lifecycle State Transitions and `Consumed` Contract

```text
None ──▶ Forming ──┬──▶ Confirmed (confirmation trigger met; pivot tuple registered as Consumed)
                   └──▶ Invalidated (structural breakdown; terminal state until next candidate cycle)
```

1. **`Consumed` Contract**: Once a primary pivot tuple (e.g., $(i_{P1}, i_V, i_{P2})$ or $(i_X, i_A, i_B, i_C, i_D)$) reaches `Confirmed`, it is registered in the `Consumed` set to prevent re-triggering after $T_{\text{persist}}$ and to suppress multi-scale window duplicates.
2. **`Invalidated` Precedence During `Confirmed` Persistence**: Even within the persistence window $0 \le t - t_{\text{brk}} \le T_{\text{persist}}$ (default $5$ bars; $0$ means confirmation bar only), if an invalidation condition is met at bar $t$, the state immediately transitions to `Invalidated` and outputs `Main_t = 0m` (without retroactively modifying prior bars $t_{\text{brk}} \dots t-1$).
3. **Role of Invalidation Levels for Candlestick Indicators**: Candlestick indicators are single-bar events returning `1m` only on the completion bar $t$. The "Invalidation Level" (`InvalidationLevel`) in each candlestick specification does not erase past `Main` values at bar $t$; it defines the **trade-management stop-loss level and post-signal termination level when persistence mode is enabled**.
