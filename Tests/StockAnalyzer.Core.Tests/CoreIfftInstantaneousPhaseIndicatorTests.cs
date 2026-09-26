using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// The IFFT Instantaneous Phase indicator is pure C# (analytic signal via
    /// <see cref="FftAnalyticSignal"/>); it needs no Python service. The amplitude line was
    /// split out into the separate overlay indicator <c>CoreIfftInstantaneousAmplitudeIndicator</c>,
    /// so this indicator now exposes only Phase / SineWave / LeadSine.
    /// </summary>
    public class CoreIfftInstantaneousPhaseIndicatorTests
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
                // Median (H+L)/2 wanders on its own oscillation, so it differs from Close by a
                // time-varying amount (not a constant offset).
                decimal mid = (decimal)(baseline + Math.Sin(i * 0.11) * 1.5);
                decimal halfRange = (decimal)(2.0 + Math.Abs(Math.Sin(i * 0.2)) * 3.0);
                list.Add(new CoreCandleData(date.AddDays(i), close, mid + halfRange, mid - halfRange, close, 1000));
            }

            return list;
        }

        [Fact]
        public void IsOverlay_IsFalse()
        {
            Assert.False(new CoreIfftInstantaneousPhaseIndicator().IsOverlay);
        }

        [Fact]
        public void Calculate_WithValidData_SucceedsWithWarmupNulls()
        {
            var candles = MakeCandles(200);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = 32 };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, indicator.Values.Count);
            for (int i = 0; i < indicator.WindowSize - 1; i++)
            {
                Assert.Null(indicator.Values[i]);
                Assert.Null(indicator.SineWave[i]);
                Assert.Null(indicator.LeadSine[i]);
            }

            Assert.True(indicator.Values.Skip(indicator.WindowSize - 1).All(v => v.HasValue));
            Assert.True(indicator.SineWave.Skip(indicator.WindowSize - 1).All(v => v.HasValue));
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsAllNulls()
        {
            var candles = MakeCandles(10);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = 64 };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(10, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => v == null));
            Assert.True(indicator.SineWave.All(v => v == null));
            Assert.True(indicator.LeadSine.All(v => v == null));
        }

        [Fact]
        public async Task CalculateAsync_UsesBaseWrapper_NoPythonServiceNeeded()
        {
            var candles = MakeCandles(120);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = 32 };

            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext(null));

            Assert.True(result.IsSuccessful);
            Assert.Equal(candles.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);
        }

        [Fact]
        public void Result_ExposesSixSeries_NoEnvelope()
        {
            var candles = MakeCandles(120);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = 32 };

            var result = indicator.Calculate(candles);

            Assert.True(result.HasSeries(IndicatorResult.MainSeriesName));
            Assert.True(result.HasSeries("SineWave"));
            Assert.True(result.HasSeries("LeadSine"));
            Assert.True(result.HasSeries("PhaseDelta"));
            Assert.True(result.HasSeries("LocalPeriod"));
            Assert.True(result.HasSeries("PhaseStability"));
            Assert.False(result.HasSeries("Envelope"));
            Assert.DoesNotContain("Envelope", result.SeriesNamesList);
        }

        [Fact]
        public void Configure_AppliesLeadSineShiftDegreesFromParameter()
        {
            var indicator = new CoreIfftInstantaneousPhaseIndicator();
            indicator.Configure(new CoreIfftInstantaneousPhaseParameter { LeadSineShiftDegrees = 90.0 });
            Assert.Equal(90.0, indicator.LeadSineShiftDegrees);
        }

        [Fact]
        public void Calculate_LeadSine_DefaultShiftAngle_MatchesPriorFixed45DegreeBehavior()
        {
            // Regression: parameterizing the shift angle must not change the previously-hardcoded
            // 45-degree behavior at the default.
            var candles = MakeCandles(160, seed: 11);
            int w = 32;
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = w };
            indicator.Calculate(candles);

            double[] medianSamples = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToArray();
            var phase = new double[candles.Count];
            var env = new double[candles.Count];
            FftAnalyticSignal.RollingCausalAnalyticSignal(medianSamples, w, phase, env);

            int probe = 100;
            Assert.NotNull(indicator.LeadSine[probe]);
            double expected = Math.Sin(phase[probe] + Math.PI / 4.0);
            Assert.Equal(expected, (double)indicator.LeadSine[probe]!.Value, precision: 6);
        }

        [Fact]
        public void Calculate_LeadSine_UsesConfiguredShiftAngle()
        {
            var candles = MakeCandles(160, seed: 11);
            int w = 32;
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = w, LeadSineShiftDegrees = 90.0 };
            indicator.Calculate(candles);

            double[] medianSamples = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToArray();
            var phase = new double[candles.Count];
            var env = new double[candles.Count];
            FftAnalyticSignal.RollingCausalAnalyticSignal(medianSamples, w, phase, env);

            int probe = 100;
            Assert.NotNull(indicator.LeadSine[probe]);
            double expected = Math.Sin(phase[probe] + Math.PI / 2.0);
            Assert.Equal(expected, (double)indicator.LeadSine[probe]!.Value, precision: 6);
        }

        [Fact]
        public void Calculate_UsesMedianHlPrice_NotClose()
        {
            var candles = MakeCandles(160, seed: 7);
            int w = 32;

            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = w };
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

            Assert.NotNull(indicator.SineWave[probe]);
            double actualSine = (double)indicator.SineWave[probe]!.Value;
            Assert.Equal(Math.Sin(medPhase[probe]), actualSine, precision: 6);
            Assert.True(Math.Abs(Math.Sin(clsPhase[probe]) - actualSine) > 1e-3,
                $"median-HL and Close phase reconstructions should differ (close sin={Math.Sin(clsPhase[probe])}, actual={actualSine})");
        }

        [Fact]
        public void Calculate_SineAndLeadSine_AreWithinUnitRange()
        {
            var candles = MakeCandles(180, seed: 3);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = 32 };
            indicator.Calculate(candles);

            foreach (var v in indicator.SineWave.Where(v => v.HasValue))
            {
                Assert.InRange(v!.Value, -1.0000001m, 1.0000001m);
            }

            foreach (var v in indicator.LeadSine.Where(v => v.HasValue))
            {
                Assert.InRange(v!.Value, -1.0000001m, 1.0000001m);
            }
        }

        [Fact]
        public void Calculate_Phase_IsWithinZeroTo360Degrees()
        {
            var candles = MakeCandles(180, seed: 5);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = 32 };
            indicator.Calculate(candles);

            foreach (var v in indicator.Values.Where(v => v.HasValue))
            {
                Assert.InRange(v!.Value, 0m, 360m);
            }
        }

        [Fact]
        public void Factory_RegistersIfftInstantaneousPhase()
        {
            IIndicatorFactory factory = new IndicatorFactory();
            Assert.True(factory.IsRegistered(IndicatorType.IFFTInstantaneousPhase));
        }

        [Fact]
        public void Configure_AppliesWindowSizeFromParameter()
        {
            var indicator = new CoreIfftInstantaneousPhaseIndicator();
            indicator.Configure(new CoreIfftInstantaneousPhaseParameter { WindowSize = 48 });
            Assert.Equal(48, indicator.WindowSize);
        }

        /// <summary>
        /// Candles whose Median (H+L)/2 traces an exact pure sine wave of the given period (in
        /// bars), so PhaseDelta/LocalPeriod/PhaseStability can be checked against a known,
        /// noise-free ground truth (mirrors FftAnalyticSignalTests' pure-tone fixtures).
        /// Deliberately zero baseline (not a realistic positive price level): this indicator does
        /// no detrending by design, so a DC-dominated series (Re(z) always strongly positive)
        /// keeps atan2 from sweeping the full circle and breaks the "phase advances linearly"
        /// property these tests check. Matches the zero-baseline fixture already proven correct
        /// in FftAnalyticSignalTests.RollingCausalAnalyticSignal_PureSine_PhaseAdvancesLinearly....
        /// </summary>
        private static List<CoreCandleData> MakePureSineCandles(int count, double period, double amplitude = 5.0, double baseline = 0.0)
        {
            var list = new List<CoreCandleData>(count);
            var date = new DateTime(2023, 1, 1);
            for (int i = 0; i < count; i++)
            {
                double mid = baseline + amplitude * Math.Sin(2.0 * Math.PI * i / period);
                decimal midDec = (decimal)mid;
                list.Add(new CoreCandleData(date.AddDays(i), midDec, midDec + 1m, midDec - 1m, midDec, 1000));
            }

            return list;
        }

        [Fact]
        public void Calculate_PhaseDelta_PureSine_MatchesExpectedStepDegrees()
        {
            const int w = 32;
            const double period = 16.0; // clean bin: w/period = 2 cycles per window
            var candles = MakePureSineCandles(200, period);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = w };
            indicator.Calculate(candles);

            double expectedStepDeg = 360.0 / period;
            for (int i = w + 5; i < candles.Count - 5; i++)
            {
                Assert.NotNull(indicator.PhaseDelta[i]);
                Assert.True(Math.Abs((double)indicator.PhaseDelta[i]!.Value - expectedStepDeg) < 0.1,
                    $"i={i}: PhaseDelta {indicator.PhaseDelta[i]} vs expected {expectedStepDeg}");
            }
        }

        [Fact]
        public void Calculate_LocalPeriod_PureSine_ApproximatesKnownPeriod()
        {
            const int w = 32;
            const double period = 16.0;
            var candles = MakePureSineCandles(200, period);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = w };
            indicator.Calculate(candles);

            for (int i = w + 5; i < candles.Count - 5; i++)
            {
                Assert.NotNull(indicator.LocalPeriod[i]);
                Assert.True(Math.Abs((double)indicator.LocalPeriod[i]!.Value - period) < 0.1,
                    $"i={i}: LocalPeriod {indicator.LocalPeriod[i]} vs expected {period}");
            }
        }

        [Fact]
        public void Calculate_LocalPeriod_IsNullWhenPhaseDeltaNullOrNonPositive()
        {
            // Pure noise (no cyclical structure) drives an unstable phase, including bar-to-bar
            // reversals (PhaseDelta <= 0) -- exercising the degenerate branch that a clean
            // single-tone fixture cannot reach.
            var rng = new Random(2024);
            var candles = new List<CoreCandleData>();
            var date = new DateTime(2023, 1, 1);
            for (int i = 0; i < 200; i++)
            {
                decimal mid = 100m + (decimal)(rng.NextDouble() * 10.0);
                candles.Add(new CoreCandleData(date.AddDays(i), mid, mid + 1m, mid - 1m, mid, 1000));
            }

            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = 32 };
            indicator.Calculate(candles);

            bool sawNonPositiveDelta = false;
            for (int i = 0; i < candles.Count; i++)
            {
                bool deltaIsNullOrNonPositive = !indicator.PhaseDelta[i].HasValue || indicator.PhaseDelta[i]!.Value <= 0m;
                Assert.Equal(deltaIsNullOrNonPositive, !indicator.LocalPeriod[i].HasValue);

                if (indicator.PhaseDelta[i].HasValue && indicator.PhaseDelta[i]!.Value <= 0m)
                {
                    sawNonPositiveDelta = true;
                }
            }

            Assert.True(sawNonPositiveDelta, "fixture must exercise at least one non-positive PhaseDelta to validate the null-mapping branch");
        }

        [Fact]
        public void Calculate_PhaseStability_IsNullBeforeTwoWindowSizesOfWarmup()
        {
            const int w = 32;
            var candles = MakePureSineCandles(150, period: 16.0);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = w };
            indicator.Calculate(candles);

            for (int i = 0; i < 2 * w - 1; i++)
            {
                Assert.Null(indicator.PhaseStability[i]);
            }

            Assert.NotNull(indicator.PhaseStability[2 * w - 1]);
        }

        [Fact]
        public void Calculate_PhaseStability_PureSine_IsNearZero()
        {
            const int w = 32;
            var candles = MakePureSineCandles(150, period: 16.0);
            var indicator = new CoreIfftInstantaneousPhaseIndicator { WindowSize = w };
            indicator.Calculate(candles);

            for (int i = 2 * w - 1; i < candles.Count; i++)
            {
                Assert.NotNull(indicator.PhaseStability[i]);
                Assert.True(indicator.PhaseStability[i]!.Value < 1.0m,
                    $"i={i}: PhaseStability {indicator.PhaseStability[i]} should be near 0 for a clean pure sine");
            }
        }
    }
}
