using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreFFTCycleIndicatorTests
    {
        private const int KnownPeriod = 16;
        private const int WindowSize = 64;
        private const int BarCount = 200;

        private static List<CoreCandleData> CreateSineCandles(int count, decimal amplitude)
        {
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < count; i++)
            {
                decimal mid = 100m + (decimal)(System.Math.Sin(2 * System.Math.PI * i / KnownPeriod) * (double)amplitude);
                candles.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), mid, mid + 1m, mid - 1m, mid, 1000));
            }
            return candles;
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreFFTCycleIndicator();
            indicator.Calculate(new List<CoreCandleData>());

            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_LeavesBarsBeforeFirstFullWindowEmptyInEverySeries()
        {
            var candles = CreateSineCandles(BarCount, 10m);
            var indicator = new CoreFFTCycleIndicator { WindowSize = WindowSize };
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(BarCount, indicator.Values.Count);
            Assert.Equal(BarCount, indicator.CycleStrength.Count);
            Assert.Equal(BarCount, indicator.Oscillator.Count);
            Assert.True(indicator.Values.Take(WindowSize - 1).All(v => v == null));
            Assert.True(indicator.CycleStrength.Take(WindowSize - 1).All(v => v == null));
            Assert.True(indicator.Oscillator.Take(WindowSize - 1).All(v => v == null));
            Assert.True(indicator.Values.Skip(WindowSize - 1).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_SineWave_DetectsKnownCycle()
        {
            var candles = CreateSineCandles(BarCount, 10m);
            var indicator = new CoreFFTCycleIndicator { WindowSize = WindowSize };
            indicator.Calculate(candles);

            foreach (var cycle in indicator.Values.Skip(WindowSize - 1))
            {
                Assert.InRange(cycle!.Value, KnownPeriod - 2m, KnownPeriod + 2m);
            }
        }

        [Fact]
        public void Calculate_SineWaveCycleStrength_ExceedsWhiteNoise()
        {
            var sine = new CoreFFTCycleIndicator { WindowSize = WindowSize };
            sine.Calculate(CreateSineCandles(BarCount, 10m));

            var random = new System.Random(42);
            var noiseCandles = new List<CoreCandleData>();
            for (int i = 0; i < BarCount; i++)
            {
                decimal mid = 100m + (decimal)(random.NextDouble() * 10.0 - 5.0);
                noiseCandles.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), mid, mid + 1m, mid - 1m, mid, 1000));
            }
            var noise = new CoreFFTCycleIndicator { WindowSize = WindowSize };
            noise.Calculate(noiseCandles);

            var sineStrength = sine.CycleStrength[BarCount - 1];
            var noiseStrength = noise.CycleStrength[BarCount - 1];
            Assert.True(sineStrength.HasValue && noiseStrength.HasValue);
            Assert.True(sineStrength!.Value > noiseStrength!.Value * 1.5m,
                $"Expected sine-wave CycleStrength ({sineStrength}) to clearly exceed white-noise CycleStrength ({noiseStrength})");
        }

        [Fact]
        public void Calculate_SineWave_OscillatorTracksKnownPeriod()
        {
            const decimal knownAmplitude = 10m;
            var candles = CreateSineCandles(BarCount, knownAmplitude);
            var indicator = new CoreFFTCycleIndicator { WindowSize = WindowSize };
            indicator.Calculate(candles);

            var filled = indicator.Oscillator.Skip(WindowSize - 1).Select(v => v!.Value).ToList();

            // The Hann window has a coherent gain of ~0.5, so the peak lands near half the input amplitude.
            var maxAbs = filled.Max(v => System.Math.Abs(v));
            Assert.InRange(maxAbs, knownAmplitude * 0.2m, knownAmplitude * 0.8m);

            // A cosine crosses zero twice per cycle: crossings must recur roughly every half period.
            var crossings = new List<int>();
            for (int i = 1; i < filled.Count; i++)
            {
                if ((filled[i - 1] > 0m && filled[i] <= 0m) || (filled[i - 1] < 0m && filled[i] >= 0m))
                {
                    crossings.Add(i);
                }
            }
            Assert.True(crossings.Count >= 3, $"Expected multiple zero-crossings, got {crossings.Count}");

            double averageGap = Enumerable.Range(1, crossings.Count - 1).Select(i => crossings[i] - crossings[i - 1]).Average();
            Assert.InRange(averageGap, (KnownPeriod / 2.0) - 3.0, (KnownPeriod / 2.0) + 3.0);
        }

        [Fact]
        public void Calculate_IsCausal_ValuesDoNotChangeWhenLaterBarsAreRemoved()
        {
            var candles = CreateSineCandles(BarCount, 10m);
            var full = new CoreFFTCycleIndicator { WindowSize = WindowSize };
            full.Calculate(candles);
            var truncated = new CoreFFTCycleIndicator { WindowSize = WindowSize };
            truncated.Calculate(candles.Take(120).ToList());

            Assert.Equal(full.Values.Take(120), truncated.Values);
            Assert.Equal(full.CycleStrength.Take(120), truncated.CycleStrength);
            Assert.Equal(full.Oscillator.Take(120), truncated.Oscillator);
        }

        [Theory]
        [InlineData(IndicatorDefaultConstants.FftCycleMinWindowSize - 1)]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(IndicatorDefaultConstants.FftCycleMaxWindowSize + 1)]
        public void Parameter_Validate_RejectsWindowsOutsideTheSupportedRange(int windowSize)
        {
            var parameter = new StockAnalyzer.Core.Models.Parameters.CoreFFTCycleParameter { WindowSize = windowSize };

            Assert.Throws<System.ArgumentOutOfRangeException>(() => parameter.Validate());
        }

        [Theory]
        [InlineData(IndicatorDefaultConstants.FftCycleMinWindowSize)]
        [InlineData(IndicatorDefaultConstants.FftCycleMaxWindowSize)]
        public void Parameter_Validate_AcceptsTheRangeBounds(int windowSize)
        {
            new StockAnalyzer.Core.Models.Parameters.CoreFFTCycleParameter { WindowSize = windowSize }.Validate();
        }
    }
}
