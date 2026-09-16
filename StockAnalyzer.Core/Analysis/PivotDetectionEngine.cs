using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

public static class PivotDetectionEngine
{
    /// <summary>
    /// Extracts Swing Highs and Swing Lows from a given list of candles without using LINQ, adhering to ZeroAllocation where possible.
    /// </summary>
    /// <param name="candles">The list of historical candle data.</param>
    /// <param name="leftStrength">Number of candles to the left that must be strictly lower/higher.</param>
    /// <param name="rightStrength">Number of candles to the right that must be strictly lower/higher.</param>
    /// <returns>A list of extracted fractal pivot points.</returns>
    public static void ExtractPivots(
        IReadOnlyList<CandleData> candles, 
        int leftStrength, 
        int rightStrength,
        List<FractalPivot> outputBuffer)
    {
        outputBuffer.Clear();
        
        if (candles == null || candles.Count < leftStrength + rightStrength + 1)
        {
            return;
        }

        for (int i = leftStrength; i < candles.Count - rightStrength; i++)
        {
            var current = candles[i];
            bool isSwingHigh = true;
            bool isSwingLow = true;

            // Check Left
            for (int j = 1; j <= leftStrength; j++)
            {
                var leftCandle = candles[i - j];
                if (leftCandle.High >= current.High) isSwingHigh = false;
                if (leftCandle.Low <= current.Low) isSwingLow = false;
                
                if (!isSwingHigh && !isSwingLow) break;
            }

            // Check Right
            if (isSwingHigh || isSwingLow)
            {
                for (int j = 1; j <= rightStrength; j++)
                {
                    var rightCandle = candles[i + j];
                    if (rightCandle.High >= current.High) isSwingHigh = false;
                    if (rightCandle.Low <= current.Low) isSwingLow = false;
                    
                    if (!isSwingHigh && !isSwingLow) break;
                }
            }

            if (isSwingHigh)
            {
                outputBuffer.Add(new FractalPivot
                {
                    Type = FractalPivotType.High,
                    Index = i,
                    Price = current.High,
                    Timestamp = current.Timestamp
                });
            }
            else if (isSwingLow)
            {
                outputBuffer.Add(new FractalPivot
                {
                    Type = FractalPivotType.Low,
                    Index = i,
                    Price = current.Low,
                    Timestamp = current.Timestamp
                });
            }
        }
    }
}
