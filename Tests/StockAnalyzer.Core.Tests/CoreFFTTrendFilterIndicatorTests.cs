using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using Xunit;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// The FFT Trend Filter is now a pure-C# synchronous indicator (was a Python IPC delegate).
    /// </summary>
    public class CoreFFTTrendFilterIndicatorTests
    {
        private static List<CoreCandleData> MakeCandles(int count, int seed = 1)
        {
            var rng = new Random(seed);
            var list = new List<CoreCandleData>(count);
            var date = new DateTime(2023, 1, 1);
            for (int i = 0; i < count; i++)
            {
                decimal mid = 100m + i * 0.1m + (decimal)(Math.Sin(i * 0.3) * 5.0 + (rng.NextDouble() - 0.5));
                // High/Low deliberately asymmetric so (H+L)/2 (= mid + 1) differs from Close (= mid).
                list.Add(new CoreCandleData(date.AddDays(i), mid, mid + 4m, mid - 2m, mid, 1000));
            }

            return list;
        }

        [Fact]
        public void IsOverlay_IsTrue()
        {
            // FFT Trend Filter reconstructs a price-scale line, so it overlays the main chart.
            Assert.True(new CoreFFTTrendFilterIndicator().IsOverlay);
        }

        [Fact]
        public void Calculate_WithValidData_SucceedsSynchronouslyWithWarmupNulls()
        {
            var candles = MakeCandles(120);
            var indicator = new CoreFFTTrendFilterIndicator { WindowSize = 32, NumHarmonics = 4 };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, indicator.Values.Count);
            for (int i = 0; i < indicator.WindowSize - 1; i++)
            {
                Assert.Null(indicator.Values[i]);
            }

            Assert.True(indicator.Values.Skip(indicator.WindowSize - 1).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsAllNulls()
        {
            var candles = MakeCandles(10);
            var indicator = new CoreFFTTrendFilterIndicator { WindowSize = 32, NumHarmonics = 4 };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(10, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => v == null));
        }

        [Fact]
        public async Task CalculateAsync_UsesBaseWrapper_NoPythonServiceNeeded()
        {
            var candles = MakeCandles(80);
            var indicator = new CoreFFTTrendFilterIndicator { WindowSize = 16, NumHarmonics = 3 };

            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext(null));

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);
        }

        [Fact]
        public void Calculate_UsesMedianHlPrice_NotClose()
        {
            var candles = MakeCandles(100, seed: 7);
            int w = 24;
            int h = 5;

            var indicator = new CoreFFTTrendFilterIndicator { WindowSize = w, NumHarmonics = h };
            indicator.Calculate(candles);

            double[] medianSamples = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToArray();
            double[] closeSamples = candles.Select(c => (double)c.Close).ToArray();

            var medianTrend = new double[candles.Count];
            var closeTrend = new double[candles.Count];
            FftLowPassFilter.RollingCausalTrend(medianSamples, w, h, medianTrend);
            FftLowPassFilter.RollingCausalTrend(closeSamples, w, h, closeTrend);

            int probe = 60;
            Assert.NotNull(indicator.Values[probe]);
            double actual = (double)indicator.Values[probe]!.Value;
            Assert.Equal(medianTrend[probe], actual, precision: 6);
            Assert.True(Math.Abs(closeTrend[probe] - actual) > 1e-3,
                $"median-HL and Close reconstructions should differ (close={closeTrend[probe]}, actual={actual})");
        }
    }
}
