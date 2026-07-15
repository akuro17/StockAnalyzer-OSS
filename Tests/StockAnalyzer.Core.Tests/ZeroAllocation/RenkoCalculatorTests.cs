using System;
using Xunit;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Core.Tests.ZeroAllocation
{
    public class RenkoCalculatorTests
    {
        [Fact]
        public void Calculate_With2BrickReversal_SkipsPreviousLevel_WhenReversingDown()
        {
            // Set up candles where price goes up 2 bricks, then down 3 bricks (which is enough for a 2-brick reversal)
            // Brick size: 10
            var candles = new ZeroAllocCandleData[]
            {
                new ZeroAllocCandleData(DateTime.Today, 100, 100, 100, 100, 0), // Start (aligns to 100, currentHigh=110, currentLow=100)
                new ZeroAllocCandleData(DateTime.Today.AddDays(1), 100, 120, 100, 120, 0), // Up to 120 (generates 100-110, 110-120)
                new ZeroAllocCandleData(DateTime.Today.AddDays(2), 120, 120, 90, 90, 0), // Down to 90
            };

            var parameters = new RenkoParameters(BlockSize: 10m, ReversalBricks: 2);
            var adapter = RenkoCalculator.Calculate(candles, parameters);

            // Let's see the expected bricks
            // Initialization sets up at 100.
            // 1st close=100. Does nothing because 100 is not >= 110 or <= 90.
            // 2nd close=120. Goes up.
            //   - Brick 0: 100 to 110 (Up)
            //   - Brick 1: 110 to 120 (Up). currentHigh=120, currentLow=110.
            // 3rd close=90. Direction is Up. Checks reversal.
            //   - Reversal requires dropping below currentLow - (ReversalBricks * BlockSize) + BlockSize
            //   - For 2 bricks: drop below 110 - (2*10) + 10 = 100? Wait, the logic in calculator:
            //   "closePrice <= currentLow - (reversalBricks * blockSize) + blockSize"
            //   reversalBricks = 2, blockSize = 10 => 110 - 20 + 10 = 100.
            //   closePrice = 90. 90 <= 100. Reversal triggers.
            //   - Reversal Brick 2: Starts at currentLow (110) downwards to (110 - 10) = 100.
            //     - Visually leaves the 110-120 vertical space empty, rendering 110-100 adjacent to 110-120's bottom edge.
            //   - Continuation Brick 3: currentHigh=110, currentLow=100. closePrice=90 is <= 100-10=90.
            //     - Brick 3: Starts at currentLow 100 downwards to 90.
            
            Assert.Equal(3, adapter.Count);
            
            var opens = adapter.Opens.Span;
            var closes = adapter.Closes.Span;

            // Brick 0: 110 -> 120 (Up) skips neutral zone 100-110
            Assert.Equal(110m, opens[0]);
            Assert.Equal(120m, closes[0]);

            // Brick 1: 110 -> 100 (Reversal Down). Open is higher than Close.
            Assert.Equal(110m, opens[1]);
            Assert.Equal(100m, closes[1]);

            // Brick 2: 100 -> 90 (Continuation Down)
            Assert.Equal(100m, opens[2]);
            Assert.Equal(90m, closes[2]);
        }
    }
}
