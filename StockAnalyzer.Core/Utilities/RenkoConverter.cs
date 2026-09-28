using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

public static class RenkoConverter
{
    public static List<CoreCandleData> Convert(IReadOnlyList<CoreCandleData> candles, decimal brickSize, ChartRoundingMode roundingMode = ChartRoundingMode.None)
    {
        var result = new List<CoreCandleData>();
        Convert(candles, brickSize, result, roundingMode);
        return result;
    }

    public static void Convert(IReadOnlyList<CoreCandleData> candles, decimal brickSize, List<CoreCandleData> outputBuffer, ChartRoundingMode roundingMode = ChartRoundingMode.None)
    {
        outputBuffer.Clear();
        if (candles == null) return;
        if (brickSize <= 0) return;
        
        bool isFirst = true;
        
        // Track the current brick's range
        decimal currentHigh = 0m;
        decimal currentLow = 0m;
        
        // 0 = Unknown, 1 = Up, -1 = Down
        int direction = 0; 

        foreach (var candle in candles)
        {
            if (isFirst)
            {
                // Align first brick to grid
                decimal close = candle.Close;
                
                // Use custom quantization if mode is set, otherwise fallback to standard Floor behavior for backward compatibility.
                decimal snapped = roundingMode == ChartRoundingMode.None
                    ? Math.Floor(close / brickSize) * brickSize
                    : ChartMath.Quantize(close, brickSize, roundingMode);
                
                // For the very first brick, we assume a neutral start or just set the level.
                // We won't generate a brick until we move from this level.
                currentHigh = snapped + brickSize;
                currentLow = snapped;
                
                // We'll treat the first movement as establishing direction.
                isFirst = false;
                continue;
            }

            decimal closePrice = candle.Close;
            
            // Loop to handle large moves (multiple bricks in one candle)
            int loopGuard = 0;
            while (true)
            {
                if (++loopGuard > 1000)
                {
                    break;
                }
                // Check for Up move
                // New brick if price >= currentHigh + brickSize
                // Standard Renko: strictly >, or >=? usually >= or if (price - high) >= brickSize.
                // If we are neutral (direction == 0), we can go either way.
                
                if (direction == 0)
                {
                    // Neutral state: can go Up or Down
                    if (closePrice >= currentHigh + brickSize)
                    {
                        // Start Up
                        direction = 1;
                        decimal brickStart = currentHigh;
                        decimal brickEnd = currentHigh + brickSize;
                        
                        outputBuffer.Add(new CoreCandleData(candle.Timestamp, brickStart, brickEnd, brickStart, brickEnd, 0));
                        
                        currentHigh = brickEnd;
                        currentLow = brickStart;
                        continue;
                    }
                    else if (closePrice <= currentLow - brickSize)
                    {
                        // Start Down
                        direction = -1;
                        decimal brickStart = currentLow;
                        decimal brickEnd = currentLow - brickSize;
                        
                        outputBuffer.Add(new CoreCandleData(candle.Timestamp, brickStart, brickStart, brickEnd, brickEnd, 0));
                        
                        currentHigh = brickStart;
                        currentLow = brickEnd;
                        continue;
                    }
                }
                else if (direction == 1)
                {
                    // Currently Going Up
                    // 1. Check Continuation (More Up)
                    if (closePrice >= currentHigh + brickSize)
                    {
                        decimal brickStart = currentHigh;
                        decimal brickEnd = currentHigh + brickSize;
                        
                        outputBuffer.Add(new CoreCandleData(candle.Timestamp, brickStart, brickEnd, brickStart, brickEnd, 0));
                        
                        currentHigh = brickEnd;
                        currentLow = brickStart; // Bottom of new brick
                        continue;
                    }
                    // 2. Check Reversal (Down)
                    // Must drop below the current brick's low by at least one brick size
                    else if (closePrice <= currentLow - brickSize)
                    {
                        direction = -1;
                        // Reversal Brick: Starts at currentLow, goes down
                        decimal brickStart = currentLow;
                        decimal brickEnd = currentLow - brickSize;
                        
                        outputBuffer.Add(new CoreCandleData(candle.Timestamp, brickStart, brickStart, brickEnd, brickEnd, 0));
                        
                        currentHigh = brickStart; // Top of new brick (was bottom of prev)
                        currentLow = brickEnd;
                        continue;
                    }
                }
                else if (direction == -1)
                {
                    // Currently Going Down
                    // 1. Check Continuation (More Down)
                    if (closePrice <= currentLow - brickSize)
                    {
                        decimal brickStart = currentLow;
                        decimal brickEnd = currentLow - brickSize;
                        
                        outputBuffer.Add(new CoreCandleData(candle.Timestamp, brickStart, brickStart, brickEnd, brickEnd, 0));
                        
                        currentHigh = brickStart; // Top of new brick
                        currentLow = brickEnd;
                        continue;
                    }
                    // 2. Check Reversal (Up)
                    // Must rise above the current brick's high by at least one brick size
                    else if (closePrice >= currentHigh + brickSize)
                    {
                        direction = 1;
                        // Reversal Brick: Starts at currentHigh, goes up
                        decimal brickStart = currentHigh;
                        decimal brickEnd = currentHigh + brickSize;
                        
                        outputBuffer.Add(new CoreCandleData(candle.Timestamp, brickStart, brickEnd, brickStart, brickEnd, 0));
                        
                        currentHigh = brickEnd;
                        currentLow = brickStart; // Bottom of new brick (was top of prev)
                        continue;
                    }
                }

                // If no condition met, stop processing this candle
                break;
            }
        }
    }

}
