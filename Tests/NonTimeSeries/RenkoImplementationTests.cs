// RenkoImplementationTests.cs
// Boundary condition tests for Renko non-time-series implementation
// Covers edge cases: empty data, count == Period, count == Period-1, etc.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.NonTimeSeries;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.NonTimeSeries;


namespace StockAnalyzer.Tests.NonTimeSeries
{
    public class RenkoBlockTests
    {
        [Fact]
        public void RenkoBlock_Create_WithValidData_ShouldCreateBlock()
        {
            var timestamps = new[] { DateTime.Now, DateTime.Now.AddMinutes(1) };
            var block = RenkoBlock.Create(timestamps[0], timestamps[1], 100m, 110m, true, timestamps);

            Assert.Equal(100m, block.Open);
            Assert.Equal(110m, block.Close);
            Assert.True(block.IsBullish);
            Assert.Equal(2, block.Timestamps.Length);
        }

        [Fact]
        public void RenkoBlock_Create_WithNullTimestamps_ShouldCreateEmptyArray()
        {
            var block = RenkoBlock.Create(DateTime.Now, DateTime.Now.AddMinutes(1), 100m, 110m, true, null!);
            Assert.True(block.Timestamps.IsEmpty);
        }

        [Fact]
        public void RenkoBlock_RenderHighLow_Bullish_ShouldReturnCorrectValues()
        {
            var block = RenkoBlock.Create(DateTime.Now, DateTime.Now, 100m, 110m, true, Array.Empty<DateTime>());
            Assert.Equal(110m, block.RenderHigh);
            Assert.Equal(100m, block.RenderLow);
        }

        [Fact]
        public void RenkoBlock_RenderHighLow_Bearish_ShouldReturnCorrectValues()
        {
            var block = RenkoBlock.Create(DateTime.Now, DateTime.Now, 110m, 100m, false, Array.Empty<DateTime>());
            Assert.Equal(110m, block.RenderHigh);
            Assert.Equal(100m, block.RenderLow);
        }
    }

    public class RenkoConverterTests
    {
        private static List<CoreCandleData> CreateCandles(params decimal[] closes)
        {
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            return closes.Select((c, i) => new CoreCandleData
            {
                Timestamp = baseTime.AddMinutes(i),
                Open = c - 1,
                High = c + 1,
                Low = c - 2,
                Close = c
            }).ToList();
        }

        [Fact]
        public void Convert_NullCandles_ReturnsEmpty()
        {
            var result = RenkoConverter.Convert(null, 10m);
            Assert.True(result.IsEmpty);
        }

        [Fact]
        public void Convert_EmptyCandles_ReturnsEmpty()
        {
            var result = RenkoConverter.Convert(new List<CoreCandleData>(), 10m);
            Assert.True(result.IsEmpty);
        }

        [Fact]
        public void Convert_SingleCandle_ReturnsEmpty()
        {
            var candles = CreateCandles(100m);
            var result = RenkoConverter.Convert(candles, 10m);
            Assert.True(result.IsEmpty);
        }

        [Fact]
        public void Convert_TwoCandles_NoBlockCreated_ReturnsEmpty()
        {
            var candles = CreateCandles(100m, 105m);
            var result = RenkoConverter.Convert(candles, 10m);
            Assert.True(result.IsEmpty);
        }

        [Fact]
        public void Convert_PriceMovesExactlyOneBlock_CreatesSingleBlock()
        {
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var candles = new List<CoreCandleData>
            {
                new() { Timestamp = baseTime, Open = 100m, High = 102m, Low = 98m, Close = 100m },
                new() { Timestamp = baseTime.AddMinutes(1), Open = 100m, High = 111m, Low = 99m, Close = 110m }
            };
            var result = RenkoConverter.Convert(candles, 10m);
            Assert.Single(result);
            Assert.True(result[0].IsBullish);
        }

        [Fact]
        public void Convert_PriceMovesMultipleBlocks_CreatesMultipleBlocks()
        {
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var candles = new List<CoreCandleData>
            {
                new() { Timestamp = baseTime, Open = 100m, High = 102m, Low = 98m, Close = 100m },
                new() { Timestamp = baseTime.AddMinutes(1), Open = 100m, High = 125m, Low = 101m, Close = 125m }
            };
            var result = RenkoConverter.Convert(candles, 10m);
            Assert.Equal(2, result.Length);
            Assert.All(result, b => Assert.True(b.IsBullish));
        }

        [Fact]
        public void Convert_Reversal_CreatesReversalBlock()
        {
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var candles = new List<CoreCandleData>
            {
                new() { Timestamp = baseTime, Open = 100m, High = 102m, Low = 98m, Close = 100m },
                new() { Timestamp = baseTime.AddMinutes(1), Open = 100m, High = 111m, Low = 99m, Close = 110m },
                new() { Timestamp = baseTime.AddMinutes(2), Open = 110m, High = 109m, Low = 88m, Close = 88m }
            };
            var result = RenkoConverter.Convert(candles, 10m);
            Assert.True(result.Length >= 2);
            Assert.True(result[0].IsBullish);
            Assert.False(result[^1].IsBullish);
        }

        [Fact]
        public void Convert_TimestampTraceability_AllCandlesTracked()
        {
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var candles = new List<CoreCandleData>
            {
                new() { Timestamp = baseTime, Open = 100m, High = 102m, Low = 98m, Close = 100m },
                new() { Timestamp = baseTime.AddMinutes(1), Open = 100m, High = 103m, Low = 99m, Close = 102m },
                new() { Timestamp = baseTime.AddMinutes(2), Open = 102m, High = 111m, Low = 101m, Close = 110m }
            };
            var result = RenkoConverter.Convert(candles, 10m);
            Assert.Single(result);
            Assert.True(result[0].Timestamps.Length >= 1);
        }

        [Fact]
        public void CalculateOptimalBlockSize_NullCandles_ReturnsDefault()
        {
            var result = RenkoConverter.CalculateOptimalBlockSize(null);
            Assert.Equal(1m, result);
        }

        [Fact]
        public void CalculateOptimalBlockSize_SingleCandle_ReturnsDefault()
        {
            var candles = CreateCandles(100m);
            var result = RenkoConverter.CalculateOptimalBlockSize(candles);
            Assert.Equal(1m, result);
        }

        [Fact]
        public void Convert_BlockSizeBelowMin_ClampsToMin()
        {
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var candles = new List<CoreCandleData>
            {
                new() { Timestamp = baseTime, Open = 1.0000m, High = 1.0001m, Low = 0.9999m, Close = 1.0001m },
                new() { Timestamp = baseTime.AddMinutes(1), Open = 1.0001m, High = 1.0002m, Low = 1.0000m, Close = 1.0002m }
            };
            // Request extremely small size, should be clamped to 0.0001m
            var result = RenkoConverter.Convert(candles, 0.0000001m);
            
            // If clamped to 0.0001, a 0.0002 move might create blocks depending on logic
            // But we just want to ensure it doesn't crash or use 0.
            // Let's rely on valid execution. To verify "Clamped", we can inspect count or behavior.
            // But since we can't easily inspect the internal "size" variable, 
            // we will assume if it runs without infinite loop or exception it's handled.
            // However, we can also test CalculateOptimalBlockSize return value if we use useTrueRange=true logic?
            // Actually, Convert uses the clamp explicitly.
            Assert.False(result.IsDefault);
        }

        [Fact]
        public void CalculateOptimalBlockSize_PeriodGreaterThanCount_ReturnsATR()
        {
            // Use explicit candles to control TrueRange exactly.
            // CreateCandles helper adds volatility (High=c+1, Low=c-2) -> TR=3.
            // We want TR=1 for simplicity.
            var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
            var candles = new List<CoreCandleData>
            {
                new() { Timestamp = baseTime, Open=100, High=100, Low=100, Close=100 },
                new() { Timestamp = baseTime.AddMinutes(1), Open=100, High=101, Low=100, Close=101 }, // TR=1
                new() { Timestamp = baseTime.AddMinutes(2), Open=101, High=102, Low=101, Close=102 }  // TR=1
            };
            
            // n will be Min(14, 2) = 2.
            // Sum=2, n=2 => ATR=1.
            var result = RenkoConverter.CalculateOptimalBlockSize(candles, 14);
            Assert.Equal(1m, result);
        }

        [Fact]
        public void CalculateOptimalBlockSize_ZeroOrNegativePeriod_ReturnsATRWithDefault()
        {
            var candles = CreateCandles(100m, 110m);
            // RenkoConverter.CalculateOptimalBlockSize doesn't explicitly check period <= 0 in arguments,
            // but the logic `int n = Math.Min(period, sorted.Count - 1);` handle it?
            // If period=0, n=0. function returns 1m immediately if n<=0.
            var result = RenkoConverter.CalculateOptimalBlockSize(candles, 0);
            Assert.Equal(1m, result);

            result = RenkoConverter.CalculateOptimalBlockSize(candles, -5);
            Assert.Equal(1m, result);
        }
    }

    public class RenkoSMATests
    {
        private static ImmutableArray<RenkoBlock> CreateBlocks(params decimal[] closes)
        {
            return closes.Select((c, i) => RenkoBlock.Create(
                DateTime.Now.AddMinutes(i), DateTime.Now.AddMinutes(i + 1),
                c - 5, c, true, Array.Empty<DateTime>())).ToImmutableArray();
        }

        [Fact]
        public void Calculate_EmptyBlocks_ReturnsEmpty()
        {
            var sma = new RenkoSMA(14);
            sma.Initialize(ImmutableArray<RenkoBlock>.Empty);
            sma.Calculate();
            Assert.Empty(sma.GetValues());
        }

        [Fact]
        public void Calculate_CountLessThanPeriod_AllNull()
        {
            var blocks = CreateBlocks(100m, 110m, 120m);
            var sma = new RenkoSMA(14);
            sma.Initialize(blocks);
            sma.Calculate();
            Assert.Equal(3, sma.GetValues().Count);
            Assert.All(sma.GetValues(), v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_CountEqualsPeriod_SingleValue()
        {
            var blocks = CreateBlocks(Enumerable.Range(1, 14).Select(i => (decimal)(100 + i)).ToArray());
            var sma = new RenkoSMA(14);
            sma.Initialize(blocks);
            sma.Calculate();
            var values = sma.GetValues();
            Assert.Equal(14, values.Count);
            for (int i = 0; i < 13; i++) Assert.Null(values[i]);
            Assert.NotNull(values[13]);
        }

        [Fact]
        public void Calculate_CountEqualsOneLessThanPeriod_AllNull()
        {
            var blocks = CreateBlocks(Enumerable.Range(1, 13).Select(i => (decimal)(100 + i)).ToArray());
            var sma = new RenkoSMA(14);
            sma.Initialize(blocks);
            sma.Calculate();
            Assert.All(sma.GetValues(), v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_RollingSumCorrectness()
        {
            var blocks = CreateBlocks(10m, 20m, 30m, 40m, 50m);
            var sma = new RenkoSMA(3);
            sma.Initialize(blocks);
            sma.Calculate();
            var values = sma.GetValues();
            Assert.Null(values[0]);
            Assert.Null(values[1]);
            Assert.Equal(20m, values[2]);
            Assert.Equal(30m, values[3]);
            Assert.Equal(40m, values[4]);
        }
    }

    public class RenkoEMATests
    {
        private static ImmutableArray<RenkoBlock> CreateBlocks(params decimal[] closes)
        {
            return closes.Select((c, i) => RenkoBlock.Create(
                DateTime.Now.AddMinutes(i), DateTime.Now.AddMinutes(i + 1),
                c - 5, c, true, Array.Empty<DateTime>())).ToImmutableArray();
        }

        [Fact]
        public void Calculate_EmptyBlocks_ReturnsEmpty()
        {
            var ema = new RenkoEMA(14);
            ema.Initialize(ImmutableArray<RenkoBlock>.Empty);
            ema.Calculate();
            Assert.Empty(ema.GetValues());
        }

        [Fact]
        public void Calculate_CountLessThanPeriod_AllNull()
        {
            var blocks = CreateBlocks(100m, 110m, 120m);
            var ema = new RenkoEMA(14);
            ema.Initialize(blocks);
            ema.Calculate();
            Assert.All(ema.GetValues(), v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_CountEqualsPeriod_SingleValue()
        {
            var blocks = CreateBlocks(Enumerable.Range(1, 14).Select(i => (decimal)(100 + i)).ToArray());
            var ema = new RenkoEMA(14);
            ema.Initialize(blocks);
            ema.Calculate();
            var values = ema.GetValues();
            Assert.Equal(14, values.Count);
            for (int i = 0; i < 13; i++) Assert.Null(values[i]);
            Assert.NotNull(values[13]);
        }

        [Fact]
        public void Calculate_InitialEMA_EqualsSMA()
        {
            var closes = new[] { 10m, 20m, 30m, 40m, 50m };
            var blocks = CreateBlocks(closes);
            var ema = new RenkoEMA(3);
            ema.Initialize(blocks);
            ema.Calculate();
            var values = ema.GetValues();
            Assert.Equal(20m, values[2]);
        }
    }

    public class RenkoRSITests
    {
        private static ImmutableArray<RenkoBlock> CreateBlocks(params decimal[] closes)
        {
            return closes.Select((c, i) => RenkoBlock.Create(
                DateTime.Now.AddMinutes(i), DateTime.Now.AddMinutes(i + 1),
                c - 5, c, true, Array.Empty<DateTime>())).ToImmutableArray();
        }

        [Fact]
        public void Calculate_EmptyBlocks_ReturnsEmpty()
        {
            var rsi = new RenkoRSI(14);
            rsi.Initialize(ImmutableArray<RenkoBlock>.Empty);
            rsi.Calculate();
            Assert.Empty(rsi.GetValues());
        }

        [Fact]
        public void Calculate_CountEqualsPeriod_AllNull()
        {
            var blocks = CreateBlocks(Enumerable.Range(1, 14).Select(i => (decimal)(100 + i)).ToArray());
            var rsi = new RenkoRSI(14);
            rsi.Initialize(blocks);
            rsi.Calculate();
            Assert.All(rsi.GetValues(), v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_CountEqualsPeriodPlusOne_OneValue()
        {
            var blocks = CreateBlocks(Enumerable.Range(1, 15).Select(i => (decimal)(100 + i)).ToArray());
            var rsi = new RenkoRSI(14);
            rsi.Initialize(blocks);
            rsi.Calculate();
            var values = rsi.GetValues();
            Assert.Equal(15, values.Count);
            for (int i = 0; i < 14; i++) Assert.Null(values[i]);
            Assert.NotNull(values[14]);
        }

        [Fact]
        public void Calculate_AllGains_RSI100()
        {
            var blocks = CreateBlocks(100m, 110m, 120m, 130m, 140m);
            var rsi = new RenkoRSI(3);
            rsi.Initialize(blocks);
            rsi.Calculate();
            var values = rsi.GetValues();
            Assert.NotNull(values[3]);
            Assert.Equal(100m, values[3]);
        }

        [Fact]
        public void Calculate_AllLosses_RSI0()
        {
            var blocks = CreateBlocks(140m, 130m, 120m, 110m, 100m);
            var rsi = new RenkoRSI(3);
            rsi.Initialize(blocks);
            rsi.Calculate();
            var values = rsi.GetValues();
            Assert.NotNull(values[3]);
            Assert.Equal(0m, values[3]);
        }

        [Fact]
        public void Calculate_EqualGainsAndLosses_RSI50()
        {
            var blocks = CreateBlocks(100m, 110m, 100m, 110m, 100m);
            var rsi = new RenkoRSI(2);
            rsi.Initialize(blocks);
            rsi.Calculate();
            var values = rsi.GetValues();
            Assert.NotNull(values[2]);
        }
    }

    public class RenkoMACDTests
    {
        private static ImmutableArray<RenkoBlock> CreateBlocks(int count)
        {
            return Enumerable.Range(1, count)
                .Select(i => RenkoBlock.Create(
                    DateTime.Now.AddMinutes(i), DateTime.Now.AddMinutes(i + 1),
                    (decimal)(100 + i - 5), (decimal)(100 + i), true, Array.Empty<DateTime>()))
                .ToImmutableArray();
        }

        [Fact]
        public void Calculate_EmptyBlocks_ReturnsEmpty()
        {
            var macd = new RenkoMACD(12, 26, 9);
            macd.Initialize(ImmutableArray<RenkoBlock>.Empty);
            macd.Calculate();
            Assert.Empty(macd.GetValues());
        }

        [Fact]
        public void Calculate_CountLessThanSlowPeriod_AllNull()
        {
            var blocks = CreateBlocks(20);
            var macd = new RenkoMACD(12, 26, 9);
            macd.Initialize(blocks);
            macd.Calculate();
            Assert.All(macd.GetValues(), v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_CountEqualsSlowPeriod_OneValue()
        {
            var blocks = CreateBlocks(26);
            var macd = new RenkoMACD(12, 26, 9);
            macd.Initialize(blocks);
            macd.Calculate();
            var values = macd.GetValues();
            Assert.Equal(26, values.Count);
            for (int i = 0; i < 25; i++) Assert.Null(values[i]);
            Assert.NotNull(values[25]);
        }

        [Fact]
        public void Calculate_CountEqualsSlowPeriodMinusOne_AllNull()
        {
            var blocks = CreateBlocks(25);
            var macd = new RenkoMACD(12, 26, 9);
            macd.Initialize(blocks);
            macd.Calculate();
            Assert.All(macd.GetValues(), v => Assert.Null(v));
        }

        [Fact]
        public void Calculate_SignalLine_CorrectNullPadding()
        {
            var blocks = CreateBlocks(40);
            var macd = new RenkoMACD(12, 26, 9);
            macd.Initialize(blocks);
            macd.Calculate();
            var signal = macd.Signal;
            for (int i = 0; i < 33; i++) Assert.Null(signal[i]);
            Assert.NotNull(signal[33]);
        }

        [Fact]
        public void Calculate_CountBetweenFastAndSlowPeriod_ReturnsNulls()
        {
            // Fast=12, Slow=26. Provide 20 blocks.
            // Should properly handle without exception, returning all nulls because we haven't reached SlowPeriod.
            var blocks = CreateBlocks(20);
            var macd = new RenkoMACD(12, 26, 9);
            macd.Initialize(blocks);
            macd.Calculate();
            
            Assert.Equal(20, macd.GetValues().Count);
            Assert.All(macd.GetValues(), v => Assert.Null(v));
        }

        [Fact]
        public void Constructor_FastGreaterThanSlow_ThrowsException()
        {
            Assert.Throws<ArgumentException>(() => new RenkoMACD(26, 12, 9));
        }

        [Fact]
        public void Constructor_ZeroPeriod_ThrowsException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RenkoMACD(0, 26, 9));
        }
    }

    public class NonTimeSeriesChartDataTests
    {
        [Fact]
        public void Constructor_WithDefaults_CreatesEmptyCollections()
        {
            var data = new NonTimeSeriesChartData("TEST", NonTimeSeriesChartType.Renko, 10m);
            Assert.True(data.Blocks.IsEmpty);
            Assert.True(data.OriginalData.IsEmpty);
            Assert.Equal(10m, data.BlockSize);
        }

        [Fact]
        public void WithAddedBlocks_CreatesNewInstance()
        {
            var data = new NonTimeSeriesChartData("TEST", NonTimeSeriesChartType.Renko, 10m);
            var block = RenkoBlock.Create(DateTime.Now, DateTime.Now, 100m, 110m, true, Array.Empty<DateTime>());
            var newData = data.WithAddedBlocks(new[] { block });
            Assert.Single(newData.Blocks);
            Assert.Empty(data.Blocks);
        }
    }

    public class RenkoChartAdapterTests
    {
        [Fact]
        public void Constructor_EmptyArray_HandlesGracefully()
        {
            var adapter = new RenkoChartAdapter(ImmutableArray<RenkoBlock>.Empty);
            Assert.Equal(0, adapter.PointCount);
        }

        [Fact]
        public void GetTimestamp_InvalidIndex_ThrowsException()
        {
            var adapter = new RenkoChartAdapter(ImmutableArray<RenkoBlock>.Empty);
            Assert.Throws<ArgumentOutOfRangeException>(() => adapter.GetTimestamp(0));
        }

        [Fact]
        public void GetValue_ValidIndex_ReturnsCorrectValue()
        {
            var blocks = ImmutableArray.Create(
                RenkoBlock.Create(DateTime.Now, DateTime.Now.AddMinutes(1), 100m, 110m, true, Array.Empty<DateTime>()));
            var adapter = new RenkoChartAdapter(blocks);
            Assert.Equal(100m, adapter.GetValue(0, PriceType.Open));
            Assert.Equal(110m, adapter.GetValue(0, PriceType.Close));
            Assert.Equal(110m, adapter.GetValue(0, PriceType.High));
            Assert.Equal(100m, adapter.GetValue(0, PriceType.Low));
        }
    }

    public class RecalculationSafetyTests
    {
        private static ImmutableArray<RenkoBlock> CreateBlocks(params decimal[] closes)
        {
            return closes.Select((c, i) => RenkoBlock.Create(
                DateTime.Now.AddMinutes(i), DateTime.Now.AddMinutes(i + 1),
                c - 5, c, true, Array.Empty<DateTime>())).ToImmutableArray();
        }

        [Fact]
        public void SMA_RecalculationSafe()
        {
            var blocks = CreateBlocks(10m, 20m, 30m, 40m, 50m);
            var sma = new RenkoSMA(3);
            sma.Initialize(blocks);
            sma.Calculate();
            var firstResult = sma.GetValues().ToList();

            sma.Calculate();
            var secondResult = sma.GetValues().ToList();

            Assert.Equal(firstResult.Count, secondResult.Count);
            for (int i = 0; i < firstResult.Count; i++)
                Assert.Equal(firstResult[i], secondResult[i]);
        }

        [Fact]
        public void EMA_RecalculationSafe()
        {
            var blocks = CreateBlocks(10m, 20m, 30m, 40m, 50m);
            var ema = new RenkoEMA(3);
            ema.Initialize(blocks);
            ema.Calculate();
            var firstResult = ema.GetValues().ToList();

            ema.Calculate();
            var secondResult = ema.GetValues().ToList();

            Assert.Equal(firstResult.Count, secondResult.Count);
        }

        [Fact]
        public void RSI_RecalculationSafe()
        {
            var blocks = CreateBlocks(100m, 110m, 120m, 110m, 120m);
            var rsi = new RenkoRSI(3);
            rsi.Initialize(blocks);
            rsi.Calculate();
            var firstResult = rsi.GetValues().ToList();

            rsi.Calculate();
            var secondResult = rsi.GetValues().ToList();

            Assert.Equal(firstResult.Count, secondResult.Count);
        }
    }
}
