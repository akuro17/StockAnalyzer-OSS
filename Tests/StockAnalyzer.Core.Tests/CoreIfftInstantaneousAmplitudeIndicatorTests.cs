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
    /// The IFFT Instantaneous Amplitude indicator plots |z[n]| (the analytic-signal magnitude,
    /// forward FFT -> negative frequencies zeroed -> inverse FFT) as a single overlay line on the
    /// main chart. It was split out of the former IFFT Instantaneous Phase "Envelope" series.
    /// </summary>
    public class CoreIfftInstantaneousAmplitudeIndicatorTests
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
        public void IsOverlay_IsTrue()
        {
            // |z| is a price-scale line, so it overlays the main chart.
            Assert.True(new CoreIfftInstantaneousAmplitudeIndicator().IsOverlay);
        }

        [Fact]
        public void Calculate_WithValidData_SucceedsWithWarmupNulls()
        {
            var candles = MakeCandles(200);
            var indicator = new CoreIfftInstantaneousAmplitudeIndicator { WindowSize = 32 };

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
            var indicator = new CoreIfftInstantaneousAmplitudeIndicator { WindowSize = 64 };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(10, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => v == null));
        }

        [Fact]
        public async Task CalculateAsync_UsesBaseWrapper_NoPythonServiceNeeded()
        {
            var candles = MakeCandles(120);
            var indicator = new CoreIfftInstantaneousAmplitudeIndicator { WindowSize = 32 };

            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext(null));

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);
        }

        [Fact]
        public void Calculate_UsesMedianHlPrice_NotClose()
        {
            var candles = MakeCandles(160, seed: 7);
            int w = 32;

            var indicator = new CoreIfftInstantaneousAmplitudeIndicator { WindowSize = w };
            indicator.Calculate(candles);

            double[] medianSamples = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToArray();
            double[] closeSamples = candles.Select(c => (double)c.Close).ToArray();

            int probe = 100;
            Assert.True(Math.Abs(medianSamples[probe] - closeSamples[probe]) > 1e-3,
                "fixture must make (H+L)/2 and Close differ at the probe");

            var medPhase = new double[candles.Count];
            var medEnv = new double[candles.Count];
            var clsPhase = new double[candles.Count];
            var clsEnv = new double[candles.Count];
            FftAnalyticSignal.RollingCausalAnalyticSignal(medianSamples, w, medPhase, medEnv);
            FftAnalyticSignal.RollingCausalAnalyticSignal(closeSamples, w, clsPhase, clsEnv);

            Assert.NotNull(indicator.Values[probe]);
            double actual = (double)indicator.Values[probe]!.Value;
            Assert.Equal(medEnv[probe], actual, precision: 6);
            Assert.True(Math.Abs(clsEnv[probe] - actual) > 1e-3,
                $"median-HL and Close amplitudes should differ (close={clsEnv[probe]}, actual={actual})");
        }

        [Fact]
        public void Amplitude_IsAtLeastMedianPrice()
        {
            // |z[n]| >= |Re(z[n])| = median price (Re of the analytic signal is the input signal).
            var candles = MakeCandles(180, seed: 9);
            int w = 32;
            var indicator = new CoreIfftInstantaneousAmplitudeIndicator { WindowSize = w };
            indicator.Calculate(candles);

            for (int i = 0; i < candles.Count; i++)
            {
                if (!indicator.Values[i].HasValue)
                {
                    continue;
                }

                decimal medianPrice = (candles[i].High + candles[i].Low) / 2m;
                Assert.True(indicator.Values[i]!.Value >= medianPrice - 1e-6m,
                    $"i={i}: amplitude {indicator.Values[i]!.Value} should be >= median price {medianPrice}");
            }
        }

        [Fact]
        public void Amplitude_MatchesAnalyticSignalEnvelope()
        {
            // The split preserved the exact former "Envelope" values.
            var candles = MakeCandles(150, seed: 11);
            int w = 40;
            var indicator = new CoreIfftInstantaneousAmplitudeIndicator { WindowSize = w };
            indicator.Calculate(candles);

            double[] samples = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToArray();
            var phase = new double[candles.Count];
            var envelope = new double[candles.Count];
            FftAnalyticSignal.RollingCausalAnalyticSignal(samples, w, phase, envelope);

            for (int i = 0; i < candles.Count; i++)
            {
                if (double.IsNaN(envelope[i]))
                {
                    Assert.Null(indicator.Values[i]);
                }
                else
                {
                    Assert.Equal(envelope[i], (double)indicator.Values[i]!.Value, precision: 8);
                }
            }
        }

        [Fact]
        public void Factory_RegistersIfftInstantaneousAmplitude()
        {
            IIndicatorFactory factory = new IndicatorFactory();
            Assert.True(factory.IsRegistered(IndicatorType.IFFTInstantaneousAmplitude));
        }

        [Fact]
        public void Configure_AppliesWindowSizeFromParameter()
        {
            var indicator = new CoreIfftInstantaneousAmplitudeIndicator();
            indicator.Configure(new CoreIfftInstantaneousAmplitudeParameter { WindowSize = 48 });
            Assert.Equal(48, indicator.WindowSize);
        }
    }
}
