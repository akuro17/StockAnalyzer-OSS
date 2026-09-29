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
    /// CorePolarPhaseIndicator is a thin surface over PolarCoordinateDecompositionEngine (Ehlers
    /// analytic signal, reused unchanged). It reports instantaneous phase in [0, 360) degrees plus
    /// the unit phasor components. These tests prove the pass-through and the [0, 360) / [-1, 1]
    /// contracts.
    /// </summary>
    public class CorePolarPhaseIndicatorTests
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
        public void Factory_RegistersPolarPhase()
        {
            IIndicatorFactory factory = new IndicatorFactory();
            Assert.True(factory.IsRegistered(IndicatorType.PolarPhase));
        }

        [Fact]
        public void IsOverlay_IsFalse()
        {
            Assert.False(new CorePolarPhaseIndicator().IsOverlay);
        }

        [Fact]
        public void Configure_AppliesParametersFromParameterObject()
        {
            var indicator = new CorePolarPhaseIndicator();
            indicator.Configure(new CorePolarPhaseParameter { DefaultPeriod = 18, MinPeriod = 8, MaxPeriod = 40 });

            Assert.Equal(18, indicator.DefaultPeriod);
            Assert.Equal(8, indicator.MinPeriod);
            Assert.Equal(40, indicator.MaxPeriod);
        }

        [Fact]
        public void Calculate_MainPhase_IsWithinZeroTo360()
        {
            var candles = MakePureSineCandles(200, period: 20.0);
            var indicator = new CorePolarPhaseIndicator();
            indicator.Calculate(candles);

            foreach (var v in indicator.Values.Where(v => v.HasValue))
            {
                Assert.InRange(v!.Value, 0m, (decimal)Math.BitDecrement(360.0));
            }
        }

        [Fact]
        public void Calculate_NormalizedComponents_LieOnUnitCircle()
        {
            var candles = MakePureSineCandles(200, period: 20.0);
            var indicator = new CorePolarPhaseIndicator();
            indicator.Calculate(candles);

            bool sawValue = false;
            for (int i = 0; i < candles.Count; i++)
            {
                if (!indicator.NormalizedInPhase[i].HasValue)
                {
                    continue;
                }

                decimal niDec = indicator.NormalizedInPhase[i]!.Value;
                decimal nqDec = indicator.NormalizedQuadrature[i]!.Value;

                // Each component must stay inside the documented unit interval (clamp guarantee).
                Assert.InRange(niDec, -1m, 1m);
                Assert.InRange(nqDec, -1m, 1m);

                double ni = (double)niDec;
                double nq = (double)nqDec;
                Assert.Equal(1.0, ni * ni + nq * nq, precision: 6);
                sawValue = true;
            }

            Assert.True(sawValue, "fixture must produce at least one non-null phasor sample");
        }

        [Fact]
        public void Calculate_MainPhase_PassesEnginePhaseDegreesThrough()
        {
            var candles = MakePureSineCandles(200, period: 20.0);
            var indicator = new CorePolarPhaseIndicator();
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

            for (int i = 0; i < prices.Length; i++)
            {
                var s = decomp[i];
                if (s.IsWarmup || !s.IsValid)
                {
                    Assert.Null(indicator.Values[i]);
                }
                else
                {
                    Assert.Equal((decimal)s.PhaseDegrees, indicator.Values[i]);
                }
            }
        }

        [Fact]
        public void Calculate_InsufficientData_AllNull()
        {
            var candles = MakePureSineCandles(10, period: 16.0);
            var indicator = new CorePolarPhaseIndicator();
            indicator.Calculate(candles);

            Assert.Equal(10, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => v == null));
            Assert.True(indicator.NormalizedInPhase.All(v => v == null));
            Assert.True(indicator.NormalizedQuadrature.All(v => v == null));
        }

        [Fact]
        public void Result_ExposesPhaseAndPhasorSeries()
        {
            var candles = MakePureSineCandles(160, period: 20.0);
            var indicator = new CorePolarPhaseIndicator();

            var result = indicator.Calculate(candles);

            Assert.True(result.HasSeries(IndicatorResult.MainSeriesName));
            Assert.True(result.HasSeries("NormalizedInPhase"));
            Assert.True(result.HasSeries("NormalizedQuadrature"));
            Assert.True(result.HasSeries("PhaseStability"));
        }

        [Fact]
        public void ScreenerCatalog_InjectsPolarPhaseAngleOutputName()
        {
            var provider = new ScreenerCatalogProvider();
            var names = provider.GetOutputSeriesNames(IndicatorType.PolarPhase);

            Assert.Contains(CorePolarPhaseIndicator.ScreenerPhaseAngleOutputName, names);
        }
    }
}
