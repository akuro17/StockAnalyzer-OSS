using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Optimization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Benchmarks
{
    public class VerificationTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price, price, price, 1000
            )).ToList();
        }

        private static List<CoreCandleData> CreateLargeCandles(int count)
        {
            var candles = new List<CoreCandleData>();
            var date = DateTime.Today;
            for (int i = 0; i < count; i++)
            {
                candles.Add(new CoreCandleData(date.AddDays(i), 100, 100, 100, 100, 100));
            }
            return candles;
        }

        [Fact]
        public void Ema_Calculate_SmallData_ReturnsCorrectValues()
        {
            var indicator = new CoreEmaIndicator { Period = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18 });

            indicator.Calculate(candles);

            Assert.Equal(5, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.Values[1]);
            Assert.Equal(12m, indicator.Values[2]); // First EMA = SMA (10+12+14)/3 = 12
            Assert.Equal(14m, indicator.Values[3]); // (16-12) * 0.5 + 12 = 14
            Assert.Equal(16m, indicator.Values[4]); // (18-14) * 0.5 + 14 = 16
        }

        [Fact]
        public void Ema_Calculate_Parallel_LargeData_ReturnsValidResults()
        {
            int count = 2500; // Above default threshold of 2000 just in case
            var candles = CreateLargeCandles(count);
            var indicator = new CoreEmaIndicator { Period = 20 };
            
            // Use HighPerformance to encourage parallelism (threshold 1000)
            var context = new OptimizationContext(ExecutionMode.HighPerformance);
            indicator.Calculate(candles, context);

            Assert.Equal(count, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.NotNull(indicator.Values[count - 1]);
        }

        [Fact]
        public void Macd_Calculate_SmallData_CalculatesLines()
        {
            var indicator = new CoreMacdIndicator { FastPeriod = 3, SlowPeriod = 6, SignalPeriod = 3 };
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });

            indicator.Calculate(candles);

            Assert.Equal(candles.Count, indicator.Values.Count);
            
            Assert.Null(indicator.Values[4]);
            Assert.NotNull(indicator.Values[5]); 
            
            Assert.Null(indicator.Signal[6]);
            Assert.NotNull(indicator.Signal[7]);
            
            Assert.NotNull(indicator.Histogram.Last());
        }

        [Fact]
        public void Macd_Calculate_Parallel_LargeData_ReturnsValidResults()
        {
            int count = 2500;
            var candles = CreateLargeCandles(count);
             var indicator = new CoreMacdIndicator { FastPeriod = 12, SlowPeriod = 26, SignalPeriod = 9 };

            // Use HighPerformance
            var context = new OptimizationContext(ExecutionMode.HighPerformance);
            indicator.Calculate(candles, context);

            Assert.Equal(count, indicator.Values.Count);
            Assert.NotNull(indicator.Values[count - 1]);
            Assert.NotNull(indicator.Signal[count - 1]);
            Assert.NotNull(indicator.Histogram[count - 1]);
        }
    }
}
