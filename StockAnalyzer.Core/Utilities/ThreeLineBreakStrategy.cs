using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// Interface for Three Line Break rules.
/// </summary>
public interface IThreeLineBreakStrategy
{
    bool TryDetermineInitialTrend(IReadOnlyList<CoreCandleData> candles, out int startIndex, out bool isUp);
    bool ShouldContinue(decimal currentClose, ThreeLineBreakBlock lastBlock, decimal tolerance);
    bool ShouldReverse(decimal currentClose, IList<ThreeLineBreakBlock> blocks, bool currentTrendIsUp, int lineCount, decimal tolerance, out decimal threshold);
}

/// <summary>
/// Standard Nison Three Line Break Strategy.
/// </summary>
public class StandardThreeLineBreakStrategy : IThreeLineBreakStrategy
{
    public bool TryDetermineInitialTrend(IReadOnlyList<CoreCandleData> candles, out int startIndex, out bool isUp)
    {
        startIndex = -1;
        isUp = false;

        if (candles.Count < 2) return false;

        decimal firstPrice = candles[0].Close;
        decimal tolerance = 0.0001m;
        
        for (int i = 1; i < candles.Count; i++)
        {
            decimal diff = candles[i].Close - firstPrice;
            if (System.Math.Abs(diff) > tolerance)
            {
                startIndex = i;
                isUp = diff > 0;
                return true;
            }
        }

        return false;
    }

    public bool ShouldContinue(decimal currentClose, ThreeLineBreakBlock lastBlock, decimal tolerance)
    {
        if (lastBlock.IsUp)
            return currentClose > (lastBlock.ClosePrice + tolerance);
        else
            return currentClose < (lastBlock.ClosePrice - tolerance);
    }

    public bool ShouldReverse(decimal currentClose, IList<ThreeLineBreakBlock> blocks, bool currentTrendIsUp, int lineCount, decimal tolerance, out decimal threshold)
    {
        int count = blocks.Count;
        int lookBack = System.Math.Min(count, lineCount);
        
        threshold = 0;
        if (count == 0) return false;

        if (currentTrendIsUp)
        {
            decimal minLow = decimal.MaxValue;
            for (int i = 0; i < lookBack; i++)
            {
                var block = blocks[count - 1 - i];
                if (block.Low < minLow) minLow = block.Low;
            }
            threshold = minLow;
            return currentClose < (threshold - tolerance);
        }
        else
        {
            decimal maxHigh = decimal.MinValue;
            for (int i = 0; i < lookBack; i++)
            {
                var block = blocks[count - 1 - i];
                if (block.High > maxHigh) maxHigh = block.High;
            }
            threshold = maxHigh;
            return currentClose > (threshold + tolerance);
        }
    }
}
