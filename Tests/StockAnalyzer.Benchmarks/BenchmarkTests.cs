using Xunit;
using Xunit.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Optimization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace StockAnalyzer.Benchmarks
{
    public class BenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public BenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static List<CoreCandleData> CreateDummyCandles(int count)
        {
            var list = new List<CoreCandleData>(count);
            var date = DateTime.Today.AddDays(-count);
            var rng = new Random(42);
            decimal price = 1000m;

            for (int i = 0; i < count; i++)
            {
                price += (decimal)(rng.NextDouble() - 0.5) * 10;
                list.Add(new CoreCandleData(date.AddDays(i), price, price, price, price, 1000));
            }
            return list;
        }

        [Fact]
        public void Benchmark_Sma_Parallel_vs_Sequential()
        {
            int dataCount = 100_000; // 100k data points
            var candles = CreateDummyCandles(dataCount);
            var indicator = new CoreSmaIndicator { Period = 20 };
            
            // Warmup JIT
            indicator.Calculate(candles.Take(100).ToList(), new OptimizationContext(ExecutionMode.Balanced));

            var sw = Stopwatch.StartNew();
            indicator.Calculate(candles, new OptimizationContext(ExecutionMode.HighPerformance));
            sw.Stop();
            long parallelTime = sw.ElapsedMilliseconds;

            _output.WriteLine($"SMA (HighPerformance) 100k: {parallelTime}ms");
            
            Assert.True(parallelTime < 500, "SMA 100k calculation should be under 500ms");
        }

        [Fact]
        public void Benchmark_Bollinger_Parallel()
        {
            int dataCount = 100_000;
            var candles = CreateDummyCandles(dataCount);
            var indicator = new CoreBollingerBandsIndicator { Period = 20 };

            var sw = Stopwatch.StartNew();
            indicator.Calculate(candles, new OptimizationContext(ExecutionMode.HighPerformance));
            sw.Stop();
            long time = sw.ElapsedMilliseconds;

            _output.WriteLine($"Bollinger (HighPerformance) 100k: {time}ms");
            Assert.True(time < 1000, "Bollinger 100k calculation should be under 1000ms");
        }

        [Fact]
        public void Benchmark_Ema_Parallel()
        {
            int dataCount = 100_000;
            var candles = CreateDummyCandles(dataCount);
            var indicator = new CoreEmaIndicator { Period = 20 };

            var sw = Stopwatch.StartNew();
            indicator.Calculate(candles, new OptimizationContext(ExecutionMode.HighPerformance));
            sw.Stop();
            long time = sw.ElapsedMilliseconds;

            _output.WriteLine($"EMA (HighPerformance) 100k: {time}ms");
            Assert.True(time < 500, "EMA 100k calculation should be under 500ms");
        }

        [Fact]
        public void Benchmark_Macd_Parallel()
        {
            int dataCount = 100_000;
            var candles = CreateDummyCandles(dataCount);
            var indicator = new CoreMacdIndicator { FastPeriod = 12, SlowPeriod = 26, SignalPeriod = 9 };

            var sw = Stopwatch.StartNew();
            indicator.Calculate(candles, new OptimizationContext(ExecutionMode.HighPerformance));
            sw.Stop();
            long time = sw.ElapsedMilliseconds;

            _output.WriteLine($"MACD (HighPerformance) 100k: {time}ms");
            Assert.True(time < 1000, "MACD 100k calculation should be under 1000ms");
        }
    }
}
