using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// Objective side-effect-zero proof for the SSoT consolidation of CoreHilbertTransformIndicator:
    /// its Ehlers Canonical Homodyne Discriminator pipeline was removed and replaced with a call to
    /// the single shared implementation, <see cref="HilbertDecompositionEngine"/>. These tests assert
    /// the indicator's three output series (Main / InPhase / Quadrature) are decimal-exact projections
    /// of the engine result across representative fixtures, and that the two configurations the old
    /// inline code tolerated still do not throw.
    /// </summary>
    public class CoreHilbertTransformConsolidationTests
    {
        private const int WarmupBars = 50;
        private readonly DateTime _baseDate = new(2023, 1, 1);

        private static decimal[] GenerateSinePrices(int count, double period, double amplitude = 10.0, double basePrice = 100.0)
        {
            var prices = new decimal[count];
            for (int i = 0; i < count; i++)
            {
                double angle = (2.0 * Math.PI * i) / period;
                prices[i] = (decimal)(basePrice + amplitude * Math.Sin(angle));
            }

            return prices;
        }

        private static HilbertDecompositionParameters DefaultEngineParameters() => new(
            DefaultPeriod: IndicatorDefaultConstants.HilbertTransformDefaultPeriod,
            MinPeriod: IndicatorDefaultConstants.HilbertTransformMinPeriod,
            MaxPeriod: IndicatorDefaultConstants.HilbertTransformMaxPeriod,
            SmoothBeta: IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta,
            DeltaLimit: IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit,
            WarmupBars: WarmupBars);

        private static void AssertMatchesEngine(decimal[] prices)
        {
            var series = prices.Select(p => (decimal?)p).ToList();

            var indicator = new CoreHilbertTransformIndicator();
            indicator.CalculateSeries(series);

            var expected = HilbertDecompositionEngine.Decompose(prices, DefaultEngineParameters());

            Assert.Equal(prices.Length, indicator.Values.Count);
            Assert.Equal(prices.Length, indicator.InPhase.Count);
            Assert.Equal(prices.Length, indicator.Quadrature.Count);

            for (int i = 0; i < prices.Length; i++)
            {
                if (i < WarmupBars)
                {
                    Assert.Null(indicator.Values[i]);
                    Assert.Null(indicator.InPhase[i]);
                    Assert.Null(indicator.Quadrature[i]);
                }
                else
                {
                    var s = expected[i];
                    Assert.Equal(s.DominantCycle, indicator.Values[i]);
                    Assert.Equal(s.InPhase, indicator.InPhase[i]);
                    Assert.Equal(s.Quadrature, indicator.Quadrature[i]);
                }
            }
        }

        [Fact]
        public void Output_MatchesEngine_Sine20()
        {
            AssertMatchesEngine(GenerateSinePrices(150, period: 20.0));
        }

        [Fact]
        public void Output_MatchesEngine_Sine30()
        {
            AssertMatchesEngine(GenerateSinePrices(180, period: 30.0));
        }

        [Fact]
        public void Output_MatchesEngine_ConstantPrices()
        {
            var prices = new decimal[100];
            Array.Fill(prices, 100m);
            AssertMatchesEngine(prices);
        }

        [Fact]
        public void Output_MatchesEngine_ScaledAndOffset()
        {
            AssertMatchesEngine(GenerateSinePrices(120, period: 20.0, amplitude: 100.0, basePrice: 1000.0));
            AssertMatchesEngine(GenerateSinePrices(120, period: 20.0, amplitude: 10.0, basePrice: 5000.0));
        }

        [Fact]
        public void Output_MatchesEngine_SingleBar()
        {
            // n == 1 hits the engine's fast path (IsWarmup == false there); the indicator must
            // still emit null for the bar because it is inside the warm-up window.
            var series = new List<decimal?> { 123.45m };
            var indicator = new CoreHilbertTransformIndicator();
            indicator.CalculateSeries(series);

            Assert.Equal(1, indicator.Values.Count);
            Assert.Null(indicator.Values[0]);
            Assert.Null(indicator.InPhase[0]);
            Assert.Null(indicator.Quadrature[0]);
        }

        [Fact]
        public void EmptyInput_ReturnsMainOnlyResult()
        {
            var indicator = new CoreHilbertTransformIndicator();
            var result = indicator.CalculateSeries(new List<decimal?>());

            Assert.True(result.IsSuccessful);
            Assert.Empty(result.MainValues);
            Assert.DoesNotContain("InPhase", result.SeriesNamesList);
            Assert.DoesNotContain("Quadrature", result.SeriesNamesList);
        }

        [Fact]
        public void NonPositiveDeltaLimit_DoesNotThrow_AndClampsLikeBefore()
        {
            var indicator = new CoreHilbertTransformIndicator();
            indicator.Configure(new CoreHilbertTransformParameter { DeltaLimit = -5.0m });

            var series = GenerateSinePrices(80, period: 20.0).Select(p => (decimal?)p).ToList();
            var result = indicator.CalculateSeries(series);

            Assert.True(result.IsSuccessful);

            // DeltaLimit <= 0 is floored to epsilon, identical to the engine's own internal guard.
            var expected = HilbertDecompositionEngine.Decompose(
                GenerateSinePrices(80, period: 20.0),
                DefaultEngineParameters() with { DeltaLimit = 1e-10m });

            for (int i = WarmupBars; i < 80; i++)
            {
                Assert.Equal(expected[i].DominantCycle, indicator.Values[i]);
            }
        }

        [Fact]
        public void Result_StillExposesThreeSeries()
        {
            var indicator = new CoreHilbertTransformIndicator();
            var series = GenerateSinePrices(120, period: 20.0).Select(p => (decimal?)p).ToList();
            var result = indicator.CalculateSeries(series);

            Assert.True(result.HasSeries(IndicatorResult.MainSeriesName));
            Assert.True(result.HasSeries("InPhase"));
            Assert.True(result.HasSeries("Quadrature"));
        }
    }
}
