using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// CorePolarAmplitudeRatioIndicator consumes the amplitude from
    /// PolarCoordinateDecompositionEngine (Ehlers analytic signal, reused unchanged) and owns the
    /// ATR normalization. These tests characterize the public nullable output series and the
    /// configurable ATR period.
    /// </summary>
    public class CorePolarAmplitudeRatioIndicatorTests
    {
        private static List<CoreCandleData> MakePureSineCandles(int count, double period, double amplitude = 5.0, double baseline = 100.0)
        {
            var list = new List<CoreCandleData>(count);
            var date = new DateTime(2023, 1, 1);
            for (int i = 0; i < count; i++)
            {
                double mid = baseline + amplitude * Math.Sin(2.0 * Math.PI * i / period);
                decimal midDec = (decimal)mid;
                // H/L symmetric about mid and C == mid so Typical (H+L+C)/3 == mid.
                list.Add(new CoreCandleData(date.AddDays(i), midDec, midDec + 1m, midDec - 1m, midDec, 1000));
            }

            return list;
        }

        [Fact]
        public void Factory_RegistersPolarAmplitudeRatio()
        {
            IIndicatorFactory factory = new IndicatorFactory();
            Assert.True(factory.IsRegistered(IndicatorType.PolarAmplitudeRatio));
        }

        [Fact]
        public void IsOverlay_IsFalse()
        {
            Assert.False(new CorePolarAmplitudeRatioIndicator().IsOverlay);
        }

        [Fact]
        public void Configure_AppliesParametersFromParameterObject()
        {
            var indicator = new CorePolarAmplitudeRatioIndicator();
            indicator.Configure(new CorePolarAmplitudeRatioParameter
            {
                AtrPeriod = 20,
                DefaultPeriod = 18,
                MinPeriod = 8,
                MaxPeriod = 40
            });

            Assert.Equal(20, indicator.AtrPeriod);
            Assert.Equal(18, indicator.DefaultPeriod);
            Assert.Equal(8, indicator.MinPeriod);
            Assert.Equal(40, indicator.MaxPeriod);
        }

        [Fact]
        public void Calculate_InsufficientData_AllNull()
        {
            var candles = MakePureSineCandles(10, period: 16.0);
            var indicator = new CorePolarAmplitudeRatioIndicator();

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Equal(10, indicator.Values.Count);
            Assert.True(indicator.Values.All(v => v == null));
        }

        [Fact]
        public void Calculate_FlatLine_AllNullFromMicroAmplitudeGuard()
        {
            var candles = new List<CoreCandleData>();
            var date = new DateTime(2023, 1, 1);
            for (int i = 0; i < 120; i++)
            {
                candles.Add(new CoreCandleData(date.AddDays(i), 100m, 101m, 99m, 100m, 1000));
            }

            var indicator = new CorePolarAmplitudeRatioIndicator();
            indicator.Calculate(candles);

            for (int i = 60; i < 120; i++)
            {
                Assert.Null(indicator.Values[i]);
            }
        }

        [Fact]
        public void Calculate_AmplitudeRatio_MatchesPreMigrationPublicContract_WithConfiguredAtrPeriod()
        {
            const int atrPeriod = 20;
            var candles = MakePureSineCandles(200, period: 20.0);

            var indicator = new CorePolarAmplitudeRatioIndicator { AtrPeriod = atrPeriod };
            indicator.Calculate(candles);

            // Characterize the pre-migration public contract independently: Polar amplitude,
            // SSoT ATR, the strict epsilon guard, and the existing phase-validity null gate.
            var priceSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceType.Typical);
            int n = priceSeries.Count;

            var atrIndicator = new CoreAtrIndicator { Period = atrPeriod };
            atrIndicator.Calculate(candles);
            var atrValues = atrIndicator.Values;

            var prices = new decimal[n];
            for (int i = 0; i < n; i++)
            {
                prices[i] = priceSeries[i];
            }

            var decompositionParams = new HilbertDecompositionParameters(
                DefaultPeriod: IndicatorDefaultConstants.HilbertTransformDefaultPeriod,
                MinPeriod: IndicatorDefaultConstants.HilbertTransformMinPeriod,
                MaxPeriod: IndicatorDefaultConstants.HilbertTransformMaxPeriod,
                SmoothBeta: IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta,
                DeltaLimit: IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit,
                WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars);

            var decomp = PolarCoordinateDecompositionEngine.Decompose(prices, decompositionParams);

            Assert.Equal(n, indicator.Values.Count);
            bool sawValue = false;
            for (int i = 0; i < n; i++)
            {
                var s = decomp[i];
                decimal? atrValue = i < atrValues.Count ? atrValues[i] : null;
                decimal? normalized = atrValue is > CorePolarAmplitudeRatioIndicator.AmplitudeRatioEpsilon
                    ? s.Amplitude / atrValue.Value
                    : null;
                decimal? expected = s.IsWarmup || !s.IsValid || normalized is null
                    ? (decimal?)null
                    : normalized.Value;

                Assert.Equal(expected, indicator.Values[i]);
                if (expected.HasValue)
                {
                    sawValue = true;
                }
            }

            Assert.True(sawValue, "fixture must produce at least one non-null AmplitudeRatio");
        }

        [Fact]
        public void NormalizeAmplitude_PreservesAtrBoundaryAndLargeFiniteSemantics()
        {
            const decimal epsilon = CorePolarAmplitudeRatioIndicator.AmplitudeRatioEpsilon;

            Assert.Null(CorePolarAmplitudeRatioIndicator.NormalizeAmplitude(1m, null));
            Assert.Null(CorePolarAmplitudeRatioIndicator.NormalizeAmplitude(1m, -1m));
            Assert.Null(CorePolarAmplitudeRatioIndicator.NormalizeAmplitude(1m, 0m));
            Assert.Null(CorePolarAmplitudeRatioIndicator.NormalizeAmplitude(1m, epsilon / 2m));
            Assert.Null(CorePolarAmplitudeRatioIndicator.NormalizeAmplitude(1m, epsilon));
            Assert.Equal(
                1m / (epsilon * 2m),
                CorePolarAmplitudeRatioIndicator.NormalizeAmplitude(1m, epsilon * 2m));
            Assert.Equal(
                1m / 1_000_000_000m,
                CorePolarAmplitudeRatioIndicator.NormalizeAmplitude(1m, 1_000_000_000m));
        }

        [Fact]
        public void Calculate_DifferentAtrPeriods_ProduceDifferentRatios()
        {
            var candles = MakePureSineCandles(220, period: 20.0);

            var fast = new CorePolarAmplitudeRatioIndicator { AtrPeriod = 5 };
            var slow = new CorePolarAmplitudeRatioIndicator { AtrPeriod = 50 };
            fast.Calculate(candles);
            slow.Calculate(candles);

            bool sawDifference = false;
            for (int i = 0; i < candles.Count; i++)
            {
                if (fast.Values[i].HasValue && slow.Values[i].HasValue &&
                    Math.Abs(fast.Values[i]!.Value - slow.Values[i]!.Value) > 0.0001m)
                {
                    sawDifference = true;
                    break;
                }
            }

            Assert.True(sawDifference, "changing AtrPeriod must change the normalized ratio");
        }
    }
}
