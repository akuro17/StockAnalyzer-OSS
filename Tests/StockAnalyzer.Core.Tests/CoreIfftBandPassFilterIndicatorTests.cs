using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// The IFFT Band-Pass Filter indicator plots the auto-tuning, causal band-pass reconstruction
    /// (<see cref="FftBandPassFilter"/>) of the dominant frequency currently present in the trailing
    /// window as a single overlay line. Pure C# (no Python dependency), unlike the similarly-named
    /// <c>FFTCycle</c> ("Dominant Cycle") indicator.
    /// </summary>
    public class CoreIfftBandPassFilterIndicatorTests
    {
        private static List<CoreCandleData> MakeCandles(int count, int seed = 1)
        {
            var rng = new Random(seed);
            var list = new List<CoreCandleData>(count);
            var date = new DateTime(2023, 1, 1);
            for (int i = 0; i < count; i++)
            {
                double baseline = 100.0 + i * 0.05 + Math.Sin(i * 0.35) * 5.0;
                decimal close = (decimal)(baseline + (rng.NextDouble() - 0.5));
                decimal mid = (decimal)(baseline + Math.Sin(i * 0.11) * 1.5);
                decimal halfRange = (decimal)(2.0 + Math.Abs(Math.Sin(i * 0.2)) * 3.0);
                list.Add(new CoreCandleData(date.AddDays(i), close, mid + halfRange, mid - halfRange, close, 1000));
            }

            return list;
        }

        [Fact]
        public void IsOverlay_IsFalse()
        {
            // Displayed in a sub-panel, matching sibling signal-processing indicators
            // (FFTCycle, IFFTInstantaneousPhase) rather than overlaying the main chart.
            Assert.False(new CoreIfftBandPassFilterIndicator().IsOverlay);
        }

        [Fact]
        public void Calculate_WithValidData_SucceedsWithWarmupNulls()
        {
            var candles = MakeCandles(200);
            var indicator = new CoreIfftBandPassFilterIndicator { WindowSize = 32 };

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
            var indicator = new CoreIfftBandPassFilterIndicator { WindowSize = 64 };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(10, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => v == null));
        }

        [Fact]
        public async Task CalculateAsync_UsesBaseWrapper_NoPythonServiceNeeded()
        {
            // Confirms this indicator is NOT Python-dependent, unlike the similarly-named FFTCycle.
            var candles = MakeCandles(120);
            var indicator = new CoreIfftBandPassFilterIndicator { WindowSize = 32 };

            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext(null));

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);
        }

        [Fact]
        public void Calculate_UsesMedianHlPrice_NotClose()
        {
            var candles = MakeCandles(160, seed: 7);
            int w = 32;

            var indicator = new CoreIfftBandPassFilterIndicator { WindowSize = w };
            indicator.Calculate(candles);

            double[] medianSamples = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToArray();
            double[] closeSamples = candles.Select(c => (double)c.Close).ToArray();

            int probe = 100;
            Assert.True(Math.Abs(medianSamples[probe] - closeSamples[probe]) > 1e-3,
                "fixture must make (H+L)/2 and Close differ at the probe");

            var medTrend = new double[candles.Count];
            var clsTrend = new double[candles.Count];
            FftBandPassFilter.RollingCausalTrend(medianSamples, w, indicator.BandWidthBins, medTrend);
            FftBandPassFilter.RollingCausalTrend(closeSamples, w, indicator.BandWidthBins, clsTrend);

            Assert.NotNull(indicator.Values[probe]);
            double actual = (double)indicator.Values[probe]!.Value;
            Assert.Equal(medTrend[probe], actual, precision: 6);
            Assert.True(Math.Abs(clsTrend[probe] - actual) > 1e-6,
                $"median-HL and Close band-pass values should differ (close={clsTrend[probe]}, actual={actual})");
        }

        [Fact]
        public void Calculate_MatchesFftBandPassFilterDirectly()
        {
            var candles = MakeCandles(150, seed: 11);
            int w = 40;
            var indicator = new CoreIfftBandPassFilterIndicator { WindowSize = w, BandWidthBins = 3 };
            indicator.Calculate(candles);

            double[] samples = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToArray();
            var trend = new double[candles.Count];
            FftBandPassFilter.RollingCausalTrend(samples, w, 3, trend);

            for (int i = 0; i < candles.Count; i++)
            {
                if (double.IsNaN(trend[i]))
                {
                    Assert.Null(indicator.Values[i]);
                }
                else
                {
                    Assert.Equal(trend[i], (double)indicator.Values[i]!.Value, precision: 8);
                }
            }
        }

        [Fact]
        public void Factory_RegistersIfftBandPassFilter()
        {
            IIndicatorFactory factory = new IndicatorFactory();
            Assert.True(factory.IsRegistered(IndicatorType.IFFTBandPassFilter));
        }

        [Fact]
        public void Configure_AppliesWindowSizeAndBandWidthBinsFromParameter()
        {
            var indicator = new CoreIfftBandPassFilterIndicator();
            indicator.Configure(new CoreIfftBandPassFilterParameter { WindowSize = 48, BandWidthBins = 5 });
            Assert.Equal(48, indicator.WindowSize);
            Assert.Equal(5, indicator.BandWidthBins);
        }
    }
}
