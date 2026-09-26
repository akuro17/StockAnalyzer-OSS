using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// CorePolarCycleAngularFrequencyIndicator is a thin surface over PolarCoordinateDecompositionEngine
    /// (Ehlers analytic signal, reused unchanged). It reports omega = 2*pi / InstantaneousPeriod
    /// (rad/bar). These tests prove the pass-through and the consistency with the underlying period.
    /// </summary>
    public class CorePolarCycleAngularFrequencyIndicatorTests
    {
        private static List<CoreCandleData> MakePureSineCandles(int count, double period, double amplitude = 5.0, double baseline = 100.0)
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
        public void Factory_RegistersPolarCycleAngularFrequency()
        {
            IIndicatorFactory factory = new IndicatorFactory();
            Assert.True(factory.IsRegistered(IndicatorType.PolarCycleAngularFrequency));
        }

        [Fact]
        public void IsOverlay_IsFalse()
        {
            Assert.False(new CorePolarCycleAngularFrequencyIndicator().IsOverlay);
        }

        [Fact]
        public void Configure_AppliesParametersFromParameterObject()
        {
            var indicator = new CorePolarCycleAngularFrequencyIndicator();
            indicator.Configure(new CorePolarCycleAngularFrequencyParameter { DefaultPeriod = 18, MinPeriod = 8, MaxPeriod = 40 });

            Assert.Equal(18, indicator.DefaultPeriod);
            Assert.Equal(8, indicator.MinPeriod);
            Assert.Equal(40, indicator.MaxPeriod);
        }

        [Fact]
        public void Calculate_InsufficientData_AllNull()
        {
            var candles = MakePureSineCandles(10, period: 16.0);
            var indicator = new CorePolarCycleAngularFrequencyIndicator();
            indicator.Calculate(candles);

            Assert.Equal(10, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => v == null));
        }

        [Fact]
        public void Calculate_CycleAngularFrequency_PassesEngineOutputThrough_AndMatchesTwoPiOverPeriod()
        {
            var candles = MakePureSineCandles(200, period: 20.0);
            var indicator = new CorePolarCycleAngularFrequencyIndicator();
            indicator.Calculate(candles);

            var priceSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceType.Typical);
            var prices = priceSeries.ToArray();

            var decompositionParams = new HilbertDecompositionParameters(
                DefaultPeriod: IndicatorDefaultConstants.HilbertTransformDefaultPeriod,
                MinPeriod: IndicatorDefaultConstants.HilbertTransformMinPeriod,
                MaxPeriod: IndicatorDefaultConstants.HilbertTransformMaxPeriod,
                SmoothBeta: IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta,
                DeltaLimit: IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit,
                WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars);

            var decomp = PolarCoordinateDecompositionEngine.Decompose(prices, decompositionParams);

            bool sawValue = false;
            for (int i = 0; i < prices.Length; i++)
            {
                var s = decomp[i];
                if (s.IsWarmup || !s.IsValid || double.IsNaN(s.CycleAngularFrequency))
                {
                    Assert.Null(indicator.Values[i]);
                }
                else
                {
                    Assert.Equal((decimal)s.CycleAngularFrequency, indicator.Values[i]);
                    double expected = (2.0 * Math.PI) / (double)s.InstantaneousPeriod;
                    Assert.Equal(expected, (double)indicator.Values[i]!.Value, precision: 9);
                    sawValue = true;
                }
            }

            Assert.True(sawValue, "fixture must produce at least one non-null angular frequency");
        }

        [Fact]
        public void Result_ExposesCycleAngularFrequencyAndDiagnosticSeries()
        {
            var candles = MakePureSineCandles(160, period: 20.0);
            var indicator = new CorePolarCycleAngularFrequencyIndicator();

            var result = indicator.Calculate(candles);

            Assert.True(result.HasSeries(IndicatorResult.MainSeriesName));
            Assert.True(result.HasSeries("PhaseAngularVelocity"));
            Assert.True(result.HasSeries("PhaseFrequencyError"));
        }

        [Fact]
        public void Calculate_InsufficientData_AllThreeSeriesNullAndSameLength()
        {
            var candles = MakePureSineCandles(10, period: 16.0);
            var indicator = new CorePolarCycleAngularFrequencyIndicator();
            indicator.Calculate(candles);

            Assert.Equal(10, indicator.Values.Count);
            Assert.Equal(10, indicator.PhaseAngularVelocity.Count);
            Assert.Equal(10, indicator.PhaseFrequencyError.Count);
            Assert.True(indicator.Values.All(v => v == null));
            Assert.True(indicator.PhaseAngularVelocity.All(v => v == null));
            Assert.True(indicator.PhaseFrequencyError.All(v => v == null));
        }

        [Fact]
        public void Calculate_Diagnostics_TrackMainNullGating_AndPassEngineOutputThrough()
        {
            var candles = MakePureSineCandles(200, period: 20.0);
            var indicator = new CorePolarCycleAngularFrequencyIndicator();
            indicator.Calculate(candles);

            var prices = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceType.Typical).ToArray();
            var decompositionParams = new HilbertDecompositionParameters(
                DefaultPeriod: IndicatorDefaultConstants.HilbertTransformDefaultPeriod,
                MinPeriod: IndicatorDefaultConstants.HilbertTransformMinPeriod,
                MaxPeriod: IndicatorDefaultConstants.HilbertTransformMaxPeriod,
                SmoothBeta: IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta,
                DeltaLimit: IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit,
                WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars);
            var decomp = PolarCoordinateDecompositionEngine.Decompose(prices, decompositionParams);

            bool sawValue = false;
            for (int i = 0; i < prices.Length; i++)
            {
                var s = decomp[i];
                if (indicator.Values[i] == null)
                {
                    // Diagnostics are gated in lockstep with the Main series.
                    Assert.Null(indicator.PhaseAngularVelocity[i]);
                    Assert.Null(indicator.PhaseFrequencyError[i]);
                }
                else
                {
                    Assert.Equal((decimal)s.PhaseAngularVelocity, indicator.PhaseAngularVelocity[i]);
                    Assert.Equal(
                        double.IsNaN(s.PhaseFrequencyError) ? (decimal?)null : (decimal)s.PhaseFrequencyError,
                        indicator.PhaseFrequencyError[i]);
                    sawValue = true;
                }
            }

            Assert.True(sawValue, "fixture must produce at least one non-null diagnostic sample");
        }

        [Fact]
        public void ScreenerCatalog_InjectsPolarCycleAngularFrequencyOutputName()
        {
            var provider = new ScreenerCatalogProvider();
            var names = provider.GetOutputSeriesNames(IndicatorType.PolarCycleAngularFrequency);

            Assert.Contains(CorePolarCycleAngularFrequencyIndicator.ScreenerCycleAngularFrequencyOutputName, names);
        }
    }
}
