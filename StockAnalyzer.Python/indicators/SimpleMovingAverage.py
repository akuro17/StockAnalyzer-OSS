
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
# Type-Level Explanation: SimpleMovingAverage (class)
#
# [Why]  The fundamental building block of Technical Analysis.
# [What] Arithmetic Mean over time.
# [How]  Calculates the average of the last N closing prices.
# -------------------------------------------------------------
class SimpleMovingAverage:

    RESYNC_INTERVAL = 1000

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
    # 1. Initialize Running Sum and null count.
    # 2. Iterate through candles.
    # 3. Add valid current Close to Sum (or increment null count).
    # 4. If window is full, subtract the (i-period)th valid Close (or decrement null count).
    # 5. Periodically resynchronize running_sum via math.fsum to prevent float drift.
    # 6. Divide Sum by Period when window contains no missing/non-finite closes.
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
        if candles is None or len(candles) == 0:
            return

        # -------------------------------------------------------------
        # Logic Step 2: Main Loop (Optimized Running Sum with Null/Drift Safety)
        # [Why]  O(1) approach with periodic math.fsum resync and C# null parity.
        # [How]  Add incoming value, subtract outgoing value, track null count.
        # -------------------------------------------------------------
        running_sum = 0.0
        null_count = 0
        bars_since_resync = 0

        for i in range(len(candles)):

            # Logic Step 2.1: Add Newest Value
            curr = _valid_close(candles[i])
            if curr is not None:
                running_sum += curr
            else:
                null_count += 1

            # Logic Step 2.2: Remove Oldest Value (if Exceeds Period)
            if i >= self.period:
                prev = _valid_close(candles[i - self.period])
                if prev is not None:
                    running_sum -= prev
                else:
                    null_count -= 1

            # Logic Step 2.3: Compute Average
            if i < self.period - 1:
                # Not enough data yet (e.g. Day 2 of Period 10)
                self.values.append(None)
                continue

            bars_since_resync += 1
            if bars_since_resync >= self.RESYNC_INTERVAL and null_count == 0:
                running_sum = math.fsum(
                    _valid_close(candles[j]) for j in range(i - self.period + 1, i + 1)
                )
                bars_since_resync = 0

            if null_count > 0:
                self.values.append(None)
            else:
                self.values.append(running_sum / self.period)

# =============================================================================
# Static Wrapper Function
# =============================================================================
# [Usage] sma_values = calculate(candles, 20)
# =============================================================================
def calculate(candles, period):
    sma = SimpleMovingAverage(period)
    sma.calculate(candles)
    return sma.values
