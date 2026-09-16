
# =============================================================================
# Logic Breakdown (Simple Moving Average)
# =============================================================================
#
# This document provides a "Deep Dive" into the internal logic of the
# Simple Moving Average (SMA).
#
# [Core Concepts]
# 1. The "Moving" Part
#    - We look at a fixed window of time (e.g., 20 days).
#    - As time moves forward by 1 day, the window shifts.
#    - One Oldest Value drops out. One Newest Value comes in.
#
# 2. Efficiency (Running Sum)
#    - Naive approach: Sum the entire window of 200 items every single step.
#      Operations: 200 * N.
#    - Optimized approach: Total = Total + New - Old.
#      Operations: 2 * N.
#    - This is O(1) per step vs O(Period).
#
# =============================================================================

# -------------------------------------------------------------
# Type-Level Explanation: SimpleMovingAverage (class)
#
# [Why]  The fundamental building block of Technical Analysis.
# [What] Arithmetic Mean over time.
# [How]  Calculates the average of the last N closing prices.
# -------------------------------------------------------------
class SimpleMovingAverage:
    
    # -------------------------------------------------------------
    # Constructor: __init__
    # -------------------------------------------------------------
    def __init__(self, period):
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
    # 1. Initialize Running Sum.
    # 2. Iterate through candles.
    # 3. Add current Close to Sum.
    # 4. If window is full, subtract the (i-period)th Close from Sum.
    # 5. Divide Sum by Period.
    #
    # [Output]
    # - None.
    # =============================================================================
    def calculate(self, candles):
        
        # -------------------------------------------------------------
        # Logic Step 1: Reset
        # [Why]  Clean state for new calculation.
        # -------------------------------------------------------------
        self.values.clear()
        if len(candles) == 0:
            return

        # -------------------------------------------------------------
        # Logic Step 2: Main Loop (Optimized Running Sum)
        # [Why]  O(1) approach is faster than sum(window) which is O(N).
        # [How]  Add incoming value, subtract outgoing value.
        # -------------------------------------------------------------
        running_sum = 0.0
        
        for i in range(len(candles)):
            
            # Logic Step 2.1: Add Newest Value
            # [Visual]
            # Window: [A, B, C]. Sum = A+B+C.
            # Next: D comes in. Sum = A+B+C+D.
            new_val = candles[i]['close']
            running_sum += new_val
            
            # Logic Step 2.2: Remove Oldest Value (if Exceeds Period)
            # [Visual]
            # Window size 3. Current count 4 (A,B,C,D).
            # Remove A. Sum = B+C+D.
            if i >= self.period:
                old_val = candles[i - self.period]['close']
                running_sum -= old_val
                
            # Logic Step 2.3: Compute Average
            if i < self.period - 1:
                # Not enough data yet (e.g. Day 2 of Period 10)
                self.values.append(None)
            else:
                # Full window available
                avg = running_sum / self.period
                self.values.append(avg)

# =============================================================================
# Static Wrapper Function
# =============================================================================
# [Usage] sma_values = calculate(candles, 20)
# =============================================================================
def calculate(candles, period):
    sma = SimpleMovingAverage(period)
    sma.calculate(candles)
    return sma.values
