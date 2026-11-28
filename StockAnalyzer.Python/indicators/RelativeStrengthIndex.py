
# =============================================================================
# Logic Breakdown (Relative Strength Index)
# =============================================================================
#
# This document provides a "Deep Dive" into the internal logic of the
# Relative Strength Index (RSI).
#
# [Core Concepts]
# 1. Internal Strength
#    - RSI compares the magnitude of recent Gains vs recent Losses.
#    - If Gains > Losses -> Market is Strong (RSI > 50).
#    - If Losses > Gains -> Market is Weak (RSI < 50).
#
# 2. Wilder's Smoothing (The "Gotcha")
#    - Most people assume RSI uses a simple average (SMA).
#    - It actually uses Wilder's Moving Average (SMMA).
#    - This creates "Infinite Memory". The values from 100 days ago still
#      have a microscopic effect on today's RSI.
#    - Formula: NewAvg = (PrevAvg * (N-1) + Current) / N.
#
# 3. Normalization
#    - The raw Ratio (RS) stretches from 0 to Inf.
#    - RSI maps this to 0-100 for readability.
#    - RSI = 100 - (100 / (1 + RS)).
#
# =============================================================================

import math


def _valid_close(candle):
    if not isinstance(candle, dict):
        return None
    raw = candle.get('close')
    if raw is None or isinstance(raw, bool):
        return None
    try:
        val = float(raw)
    except (TypeError, ValueError):
        return None
    return val if math.isfinite(val) else None


# -------------------------------------------------------------
# Type-Level Explanation: RelativeStrengthIndex (class)
#
# [Why]  The industry standard for momentum.
# [What] Oscillator (0-100).
# [How]  Uses Wilder's Smoothing to calculate the average of gains and losses over N periods, then formats this ratio as an oscillator between 0 and 100.
# [Who]  J. Welles Wilder Jr. (1978).
# -------------------------------------------------------------
class RelativeStrengthIndex:
    
    # Constant to prevent Division By Zero
    NEAR_ZERO = 1e-10

    # -------------------------------------------------------------
    # Constructor: __init__
    # -------------------------------------------------------------
    def __init__(self, period=14):
        if period <= 0:
            raise ValueError("period must be > 0")
        self.period = period
        self.values = []

    # =============================================================================
    # IPO: calculate
    # =============================================================================
    #
    # [Input]
    # - candles: List[dict]
    #
    # [Process]
    # 1. Compute Change = Close - PrevClose (skipping None / non-finite closes).
    # 2. Separate into U (Gain) and D (Loss).
    # 3. First N valid changes: Calculate Simple Average of Gains and Losses.
    # 4. Subsequent periods: Apply Wilder's Smoothing.
    # 5. Compute RS = AvgGain / AvgLoss.
    # 6. Compute RSI.
    #
    # [Output]
    # - None.
    # =============================================================================
    def calculate(self, candles):
        
        # -------------------------------------------------------------
        # Logic Step 1: Reset
        # -------------------------------------------------------------
        self.values.clear()
        if candles is None or len(candles) == 0:
            return
            
        # The first bar has no "change", so RSI is undefined
        self.values.append(None)
        
        if len(candles) < 2:
            return

        # State Variables for Smoothing (matching C# CoreRsiIndicator.CalculateSegment)
        avg_gain = 0.0
        avg_loss = 0.0
        valid_changes = 0
        is_initialized = False
        
        # -------------------------------------------------------------
        # Logic Step 2: Main Loop
        # -------------------------------------------------------------
        for i in range(1, len(candles)):
            curr = _valid_close(candles[i])
            prev = _valid_close(candles[i - 1])
            if curr is None or prev is None:
                if not is_initialized:
                    avg_gain = 0.0
                    avg_loss = 0.0
                    valid_changes = 0
                self.values.append(None)
                continue

            # Logic Step 2.1: Calculate Raw Change
            change = curr - prev
            
            # Logic Step 2.2: Separate Up/Down
            gain = change if change > 0.0 else 0.0
            loss = -change if change < 0.0 else 0.0

            if not is_initialized:
                avg_gain += gain
                avg_loss += loss
                valid_changes += 1

                if valid_changes == self.period:
                    avg_gain /= self.period
                    avg_loss /= self.period
                    is_initialized = True
                    self.values.append(self._compute_rsi_value(avg_gain, avg_loss))
                else:
                    self.values.append(None)
            else:
                avg_gain = (avg_gain * (self.period - 1) + gain) / self.period
                avg_loss = (avg_loss * (self.period - 1) + loss) / self.period
                self.values.append(self._compute_rsi_value(avg_gain, avg_loss))


    # =============================================================================
    # IPO: _compute_rsi_value (Helper)
    # =============================================================================
    # [Input] AvgGain, AvgLoss.
    # [Process] RS = Gain/Loss. RSI = 100 - 100/(1+RS).
    # [Output] RSI Value (0-100).
    # =============================================================================
    def _compute_rsi_value(self, avg_gain, avg_loss):
        # [Fail Safe] Division by Zero
        if abs(avg_loss) < self.NEAR_ZERO:
            if abs(avg_gain) < self.NEAR_ZERO:
                return 50.0 # Flat line
            else:
                return 100.0 # Vertical Up line
        
        rs = avg_gain / avg_loss
        
        # [Visual]
        # Gain=10, Loss=2. RS=5.
        # RSI = 100 - (100 / 6) = 100 - 16.6 = 83.3.
        rsi = 100.0 - (100.0 / (1.0 + rs))
        
        # Clamp (floating point safety)
        return max(0.0, min(100.0, rsi))

# =============================================================================
# Static Wrapper Function
# =============================================================================
#
# [Why] Functional API.
# [Usage] rsi_values = calculate(candles, period=14)
# =============================================================================
def calculate(candles, period=14):
    instance = RelativeStrengthIndex(period)
    instance.calculate(candles)
    return instance.values
