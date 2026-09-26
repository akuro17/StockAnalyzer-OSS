using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Tests.ZeroAllocation
{
    public class ZeroAllocKagiAdapterTests
    {
        [Fact]
        public void Calculate_EmptySource_ShouldThrowArgumentException()
        {
            var source = ReadOnlyMemory<ZeroAllocCandleData>.Empty;
            var parameters = new KagiParameters { ReversalAmount = 10m };

            Assert.Throws<ArgumentException>(() => KagiCalculator.Calculate(source, parameters));
        }

        [Fact]
        public void Calculate_WithValidSource_ShouldProduceValidKagiSegments()
        {
            // Create some sample data that moves enough to trigger a trend and a reversal
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var source = new[]
            {
                new ZeroAllocCandleData(baseTime, 100m, 100m, 100m, 100m, 1000),
                new ZeroAllocCandleData(baseTime.AddMinutes(1), 100m, 100m, 100m, 115m, 1500), // Up trend (>10m)
                new ZeroAllocCandleData(baseTime.AddMinutes(2), 115m, 115m, 115m, 102m, 1200), // Down trend/reversal (>10m)
                new ZeroAllocCandleData(baseTime.AddMinutes(3), 102m, 102m, 102m, 118m, 1100), // Up trend/reversal (>10m, should trigger split at LastHigh=115m)
            };

            var parameters = new KagiParameters { ReversalAmount = 10m };
            var adapter = KagiCalculator.Calculate(source, parameters);

            Assert.Equal(ZeroAllocChartDataType.Kagi, adapter.DataType);
            Assert.True(adapter.Count > 0);

            // Verify that timestamps, opens, highs, lows, closes, volumes are all correctly sized
            Assert.Equal(adapter.Count, adapter.Timestamps.Length);
            Assert.Equal(adapter.Count, adapter.Opens.Length);
            Assert.Equal(adapter.Count, adapter.Highs.Length);
            Assert.Equal(adapter.Count, adapter.Lows.Length);
            Assert.Equal(adapter.Count, adapter.Closes.Length);
            Assert.Equal(adapter.Count, adapter.Volumes.Length);
            Assert.Equal(adapter.Count, adapter.OriginalRanges.Length);

            // Verify that segments represent real price ranges (High >= Low)
            var highs = adapter.Highs.Span;
            var lows = adapter.Lows.Span;
            for (int i = 0; i < adapter.Count; i++)
            {
                Assert.True(highs[i] >= lows[i]);
            }
        }

        [Fact]
        public void Calculate_ShouldCorrectlyEncodeVolumeYangYin()
        {
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var source = new[]
            {
                new ZeroAllocCandleData(baseTime, 100m, 100m, 100m, 100m, 100),
                new ZeroAllocCandleData(baseTime.AddMinutes(1), 100m, 100m, 100m, 120m, 100), // Up trend
                new ZeroAllocCandleData(baseTime.AddMinutes(2), 120m, 120m, 120m, 100m, 100), // Down trend -> sets LastLow when reversing
                new ZeroAllocCandleData(baseTime.AddMinutes(3), 100m, 100m, 100m, 120m, 100), // Up trend
                new ZeroAllocCandleData(baseTime.AddMinutes(4), 120m, 120m, 120m, 90m, 100),  // Down trend breaks LastLow (100) -> Splits and flips
            };

            var parameters = new KagiParameters { ReversalAmount = 10m };
            var adapter = KagiCalculator.Calculate(source, parameters);

            // Check that volume parity is encoded correctly
            var volumes = adapter.Volumes.Span;

            // In the restructured array:
            // Segment 0: startup flat anchor close price (100) -> Yin/bearish (0L)
            // Segment 1: first Trend Up segment (100 -> 120) -> Yang/bullish (1L)
            Assert.True(adapter.Count >= 4);
            Assert.Equal(0L, volumes[0]); // Anchor flat
            Assert.Equal(1L, volumes[1]); // First trend up segment (Yang)
            
            // Check that there is a Yin segment at the end
            Assert.Equal(0L, volumes[adapter.Count - 1]); // Yin (bearish, after trend flips)
        }
    }
}
