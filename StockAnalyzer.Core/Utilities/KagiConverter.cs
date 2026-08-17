using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// Converts time-series CandleData into non-linear Kagi segments.
/// </summary>
public static class KagiConverter
{
    public enum ReversalType { Fixed, Percent, ATR }

    private class KagiState
    {
        public decimal CurrentPrice { get; set; }
        public int Direction { get; set; } // 1 = Up, -1 = Down
        public bool IsYang { get; set; }
        public decimal LastHigh { get; set; }
        public decimal LastLow { get; set; }
        public DateTime CurrentTime { get; set; }
    }

    public static List<KagiCandleData> Convert(
        IReadOnlyList<CoreCandleData> candles, 
        decimal reversalAmount, 
        ReversalType type = ReversalType.Fixed, 
        int atrPeriod = 14,
        decimal atrMultiplier = 1.0m,
        ChartRoundingMode roundingMode = ChartRoundingMode.None,
        AutoFallbackMode fallbackMode = AutoFallbackMode.Percentage)
    {
        var result = new List<KagiCandleData>();
        Convert(candles, reversalAmount, type, atrPeriod, atrMultiplier, roundingMode, fallbackMode, result);
        return result;
    }

    public static void Convert(
        IReadOnlyList<CoreCandleData> candles, 
        decimal reversalAmount, 
        ReversalType type, 
        int atrPeriod, 
        decimal atrMultiplier,
        ChartRoundingMode roundingMode,
        AutoFallbackMode fallbackMode,
        List<KagiCandleData> outputBuffer)
    {
        outputBuffer.Clear();
        if (candles == null || candles.Count == 0) return;

        // Use the centralized AutoBoxSizeCalculator to ensure consistent rounding and fallback behavior
        ChartSizingMode sizingMode = type switch
        {
            ReversalType.Percent => ChartSizingMode.Percentage,
            ReversalType.ATR => ChartSizingMode.AutoAtr,
            _ => ChartSizingMode.Fixed
        };

        decimal threshold = AutoBoxSizeCalculator.Calculate(
            sizingMode,
            candles,
            reversalAmount,
            atrPeriod,
            atrMultiplier,
            roundingMode,
            fallbackMode,
            candles[candles.Count - 1].Close,
            reversalAmount); // For Percent mode

        var startPrice = candles[0].Close;
        var state = new KagiState
        {
            CurrentPrice = startPrice,
            Direction = 0,
            IsYang = false,
            LastHigh = decimal.MinValue,
            LastLow = decimal.MaxValue,
            CurrentTime = candles[0].Timestamp
        };

        // Standard: Always insert the starting close price at index 0 as a horizontal anchor block
        outputBuffer.Add(KagiCandleData.Create(candles[0].Timestamp, startPrice, startPrice, false, startPrice, 0));

        for (int i = 1; i < candles.Count; i++)
        {
            var c = candles[i];
            var price = c.Close;

            if (state.Direction == 0)
            {
                if (Math.Abs(price - state.CurrentPrice) >= threshold)
                {
                    state.Direction = price > state.CurrentPrice ? 1 : -1;
                    state.IsYang = state.Direction == 1;
                    
                    // Add the first trend segment at index 1, originating from startPrice and ending at price
                    outputBuffer.Add(KagiCandleData.Create(c.Timestamp, startPrice, price, state.IsYang, startPrice, state.IsYang ? 1 : 0));

                    state.CurrentPrice = price;
                    state.CurrentTime = c.Timestamp;
                }
            }
            else
            {
                if ((state.Direction == 1 && price > state.CurrentPrice) ||
                    (state.Direction == -1 && price < state.CurrentPrice))
                {
                    // Continuation: Update active trend's unconfirmed high/low
                    HandleContinuationWithPotentialSplit(outputBuffer, state, price, c.Timestamp, c.Volume);
                }
                else if ((state.Direction == 1 && price <= state.CurrentPrice - threshold) ||
                         (state.Direction == -1 && price >= state.CurrentPrice + threshold))
                {
                    // Reversal confirmed!
                    // 1. Record the exact peak/trough extreme reached (state.CurrentPrice) as LastHigh/LastLow
                    var extremePrice = state.CurrentPrice;
                    if (state.Direction == 1)
                        state.LastHigh = extremePrice;
                    else if (state.Direction == -1)
                        state.LastLow = extremePrice;

                    // 2. Start the new segment EXACTLY from the confirmed extremePrice (the shoulder/waist vertex) to the new trigger price
                    state.Direction = -state.Direction;
                    state.CurrentPrice = extremePrice;

                    // Route through the unified HandleContinuationWithPotentialSplit logic to handle the initial reversal segment.
                    // This ensures any Yang/Yin transitions crossing LastHigh/LastLow bounds are correctly split at the boundary.
                    // To ensure it ALWAYS starts a new segment rather than extending the prior trend, we bypass extension.
                    bool forceNewSegment = true;
                    HandleContinuationWithPotentialSplit(outputBuffer, state, price, c.Timestamp, c.Volume, forceNewSegment);
                }
            }
        }
    }

    private static void HandleContinuationWithPotentialSplit(
        List<KagiCandleData> result, KagiState state, decimal newPrice, DateTime time, long vol, bool forceNewSegment = false)
    {
        decimal start = state.CurrentPrice;
        decimal end = newPrice;

        bool willTurnYin = state.IsYang && state.LastLow != decimal.MaxValue && end < state.LastLow;
        bool willTurnYang = !state.IsYang && state.LastHigh != decimal.MinValue && end > state.LastHigh;

        if (willTurnYin)
        {
            // Split at LastLow
            decimal boundary = state.LastLow;
            
            // 1. Process first half: start -> boundary (with original IsYang = true)
            AddOrExtendSegment(result, start, boundary, true, time, vol, forceNewSegment);
            
            // 2. Process second half: boundary -> end (with new IsYang = false)
            state.IsYang = false;
            // Force a new segment for the second half to guarantee split structure
            AddOrExtendSegment(result, boundary, end, false, time, 0, true);
        }
        else if (willTurnYang)
        {
            // Split at LastHigh
            decimal boundary = state.LastHigh;
            
            // 1. Process first half: start -> boundary (with original IsYang = false)
            AddOrExtendSegment(result, start, boundary, false, time, vol, forceNewSegment);
            
            // 2. Process second half: boundary -> end (with new IsYang = true)
            state.IsYang = true;
            // Force a new segment for the second half to guarantee split structure
            AddOrExtendSegment(result, boundary, end, true, time, 0, true);
        }
        else
        {
            // No split: normal extension or addition
            AddOrExtendSegment(result, start, end, state.IsYang, time, vol, forceNewSegment);
        }

        state.CurrentPrice = end;
    }

    private static void AddOrExtendSegment(
        List<KagiCandleData> result, decimal start, decimal end, bool isYang, DateTime time, long vol, bool forceNewSegment)
    {
        bool isExtension = false;
        if (!forceNewSegment && result.Count > 0)
        {
            var last = result[result.Count - 1];
            bool lastDirUp = last.Close >= last.Open;
            bool currDirUp = end >= start;
            
            if (last.Close == start && last.IsYang == isYang && lastDirUp == currDirUp)
            {
                var extended = last with 
                { 
                    Close = end, 
                    High = Math.Max(last.Open, end), 
                    Low = Math.Min(last.Open, end),
                    Timestamp = time,
                    Volume = last.Volume + vol
                };
                result[result.Count - 1] = extended;
                isExtension = true;
            }
        }
        
        if (!isExtension)
        {
             result.Add(KagiCandleData.Create(time, start, end, isYang, start, isYang ? 1 : 0));
        }
    }
}
