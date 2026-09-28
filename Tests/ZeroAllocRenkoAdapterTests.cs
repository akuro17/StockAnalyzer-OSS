using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Tests.ZeroAllocation
{
    public class ZeroAllocRenkoAdapterTests
    {
        private class MockRenkoDataAdapter : IZeroAllocChartDataAdapter
        {
            public int Count { get; set; }
            public ReadOnlyMemory<decimal> Opens { get; set; }
            public ReadOnlyMemory<decimal> Highs { get; set; }
            public ReadOnlyMemory<decimal> Lows { get; set; }
            public ReadOnlyMemory<decimal> Closes { get; set; }
            public ReadOnlyMemory<long> Volumes { get; set; }
            public ReadOnlyMemory<DateTime> Timestamps { get; set; }
            public ReadOnlyMemory<OriginalIndexRange> OriginalRanges { get; set; }

            public ZeroAllocChartDataType DataType => ZeroAllocChartDataType.Renko;

            public (decimal max, decimal min) GetPriceRange(int startIndex = 0, int? endIndex = null)
            {
                return (100m, 0m);
            }
        }

        [Fact]
        public void RenkoIndicatorDataAdapter_GetClosePrices_ShouldReturnCorrectValues()
        {
            // Arrange
            var closes = new decimal[] { 100m, 105m, 110m };
            var mockAdapter = new MockRenkoDataAdapter
            {
                Count = 3,
                Closes = new ReadOnlyMemory<decimal>(closes)
            };

            // Act
            using var adapter = new RenkoIndicatorDataAdapter(mockAdapter);
            using var pooledCloses = adapter.GetClosePrices();

            // Assert
            Assert.Equal(3, pooledCloses.Length);
            Assert.Equal(100m, pooledCloses.Span[0]);
            Assert.Equal(105m, pooledCloses.Span[1]);
            Assert.Equal(110m, pooledCloses.Span[2]);
        }

        [Fact]
        public void RenkoIndicatorDataAdapter_GetCandleData_ShouldReturnCorrectOHLCV()
        {
            // Arrange
            int count = 2;
            var timestamps = new DateTime[] { DateTime.Now, DateTime.Now.AddMinutes(1) };
            var opens = new decimal[] { 100m, 110m };
            var highs = new decimal[] { 105m, 115m };
            var lows = new decimal[] { 95m, 105m };
            var closes = new decimal[] { 102m, 112m };
            var volumes = new long[] { 1000, 2000 };

            var mockAdapter = new MockRenkoDataAdapter
            {
                Count = count,
                Timestamps = new ReadOnlyMemory<DateTime>(timestamps),
                Opens = new ReadOnlyMemory<decimal>(opens),
                Highs = new ReadOnlyMemory<decimal>(highs),
                Lows = new ReadOnlyMemory<decimal>(lows),
                Closes = new ReadOnlyMemory<decimal>(closes),
                Volumes = new ReadOnlyMemory<long>(volumes),
                OriginalRanges = new OriginalIndexRange[count]
            };

            // Act
            using var adapter = new RenkoIndicatorDataAdapter(mockAdapter);
            var candleData = adapter.GetCandleData();

            // Assert
            Assert.Equal(count, candleData.Length);
            var span = candleData.Span;
            
            Assert.Equal(timestamps[0], span[0].Timestamp);
            Assert.Equal(opens[0], span[0].Open);
            Assert.Equal(closes[1], span[1].Close);
            Assert.Equal(volumes[1], span[1].Volume);
        }

        [Fact]
        public void RenkoIndicatorDataAdapter_Dispose_ShouldReleaseResources()
        {
            // Arrange
            var mockAdapter = new MockRenkoDataAdapter
            {
                Count = 1,
                Closes = new decimal[] { 100m },
                Opens = new decimal[] { 100m },
                Highs = new decimal[] { 100m },
                Lows = new decimal[] { 100m },
                Volumes = new long[] { 0 },
                Timestamps = new DateTime[] { DateTime.Now },
                OriginalRanges = new OriginalIndexRange[1]
            };

            var adapter = new RenkoIndicatorDataAdapter(mockAdapter);
            
            // Access data to trigger pooling
            var data = adapter.GetCandleData();
            Assert.False(data.IsEmpty);

            // Act
            adapter.Dispose();

            // Assert
            Assert.Throws<ObjectDisposedException>(() => adapter.GetCandleData());
            Assert.Throws<ObjectDisposedException>(() => adapter.GetClosePrices());
        }
    }
}
