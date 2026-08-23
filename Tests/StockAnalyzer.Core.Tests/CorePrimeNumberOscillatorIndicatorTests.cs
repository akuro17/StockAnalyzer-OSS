using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Tests
{
    public class CorePrimeNumberOscillatorIndicatorTests
    {
        #region PrimeNumberHelper Tests

        [Theory]
        [InlineData(0, 2, 2)]
        [InlineData(1, 2, 2)]
        [InlineData(2, 2, 2)]
        [InlineData(3, 3, 3)]
        [InlineData(4, 3, 5)]
        [InlineData(5, 5, 5)]
        [InlineData(6, 5, 7)]
        [InlineData(7, 7, 7)]
        [InlineData(8, 7, 11)]
        [InlineData(9, 7, 11)]
        [InlineData(10, 7, 11)]
        [InlineData(11, 11, 11)]
        public void PrimeNumberHelper_FindNearestPrimes_ReturnsExpectedPrimes(int input, int expectedLower, int expectedUpper)
        {
            var (lower, upper) = PrimeNumberHelper.FindNearestPrimes(input);
            Assert.Equal(expectedLower, lower);
            Assert.Equal(expectedUpper, upper);
        }

        [Fact]
        public void PrimeNumberHelper_LargeValue_ClampsSafely()
        {
            var (lower, upper) = PrimeNumberHelper.FindNearestPrimes(3_000_000);
            Assert.True(lower > 1_999_000);
            Assert.Equal(lower, upper);
        }

        #endregion

        #region CorePrimeNumberOscillatorIndicator Calculation Tests

        [Fact]
        public void Calculate_WithEmptyCandles_ReturnsEmptyResult()
        {
            var indicator = new CorePrimeNumberOscillatorIndicator();
            var result = indicator.Calculate(new List<CoreCandleData>());

            Assert.True(result.IsSuccessful);
            Assert.Empty(indicator.Values);
            Assert.Empty(indicator.BuySignals);
            Assert.Empty(indicator.SellSignals);
        }

        [Fact]
        public void Calculate_WithNullCandles_ReturnsFailure()
        {
            var indicator = new CorePrimeNumberOscillatorIndicator();
            var result = indicator.Calculate(null!);

            Assert.False(result.IsSuccessful);
        }

        [Fact]
        public void Calculate_WithLowPrice_ClampsToTwoAndCalculatesCorrectly()
        {
            // Close = 0.05, Scale = 10.0 -> 0.05 * 10 = 0.5 -> Round -> 1 -> Clamped to 2
            // scaled = 2 -> (2, 2) -> diffUpper = 0, diffLower = 0 -> PNO = 0.0
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(DateTime.Today, 0.05m, 0.06m, 0.04m, 0.05m, 100)
            };

            var indicator = new CorePrimeNumberOscillatorIndicator { ScaleMultiplier = 10.0m };
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            Assert.Single(indicator.Values);
            Assert.Equal(0.0m, indicator.Values[0]);
        }

        [Fact]
        public void Calculate_WithExactPrimeAndModulusValues_MatchesFormula()
        {
            // Scale = 10.0
            // Close = 0.7 -> scaled = 7 -> (7, 7) -> PNO = 0.0
            // Close = 0.8 -> scaled = 8 -> (7, 11) -> diffUpper = 3, diffLower = 1 -> Lower closer -> PNO = -1 / 10 = -0.1
            // Close = 1.0 -> scaled = 10 -> (7, 11) -> diffUpper = 1, diffLower = 3 -> Upper closer -> PNO = +1 / 10 = +0.1
            // Close = 0.6 -> scaled = 6 -> (5, 7) -> diffUpper = 1, diffLower = 1 -> Equal -> PNO = 0.0
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(DateTime.Today.AddDays(0), 0.7m, 0.7m, 0.7m, 0.7m, 100),
                new CoreCandleData(DateTime.Today.AddDays(1), 0.8m, 0.8m, 0.8m, 0.8m, 100),
                new CoreCandleData(DateTime.Today.AddDays(2), 1.0m, 1.0m, 1.0m, 1.0m, 100),
                new CoreCandleData(DateTime.Today.AddDays(3), 0.6m, 0.6m, 0.6m, 0.6m, 100)
            };

            var indicator = new CorePrimeNumberOscillatorIndicator { ScaleMultiplier = 10.0m };
            indicator.Calculate(candles);

            Assert.Equal(0.0m, indicator.Values[0]);
            Assert.Equal(-0.1m, indicator.Values[1]);
            Assert.Equal(0.1m, indicator.Values[2]);
            Assert.Equal(0.0m, indicator.Values[3]);
        }

        [Fact]
        public void Calculate_WithNegativePrice_SafelyEmitsZero()
        {
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(DateTime.Today, -10m, -5m, -15m, -10m, 100)
            };

            var indicator = new CorePrimeNumberOscillatorIndicator();
            indicator.Calculate(candles);

            Assert.Single(indicator.Values);
            Assert.Equal(0.0m, indicator.Values[0]);
        }

        #endregion

        #region Consecutive Extrema Plateau & Deduplication Tests

        [Fact]
        public void Calculate_ConsecutiveExtremaPlateau_TriggersBuyAndSellSignals()
        {
            // Parameters: LookbackPeriod (W) = 3, ConsecutiveExtremaPeriods (K) = 2, Tolerance = 0.0
            // We construct PNO values:
            // Day 0: PNO = 0.0 (Close = 0.7)
            // Day 1: PNO = -0.1 (Close = 0.8)
            // Day 2: PNO = -0.1 (Close = 0.8) -> W=3 window: [0.0, -0.1, -0.1], min=-0.1. K=2 Plateau formed (-0.1 == -0.1).
            //        PNO < 0 and PNO <= trough + Tol -> BUY Signal at Low (0.75m)
            // Day 3: PNO = +0.1 (Close = 1.0)
            // Day 4: PNO = +0.1 (Close = 1.0) -> W=3 window: [-0.1, +0.1, +0.1], max=+0.1. K=2 Plateau formed (+0.1 == +0.1).
            //        PNO > 0 and PNO >= peak - Tol -> SELL Signal at High (1.05m)

            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(DateTime.Today.AddDays(0), 0.7m, 0.75m, 0.65m, 0.7m, 100),
                new CoreCandleData(DateTime.Today.AddDays(1), 0.8m, 0.85m, 0.75m, 0.8m, 100),
                new CoreCandleData(DateTime.Today.AddDays(2), 0.8m, 0.85m, 0.75m, 0.8m, 100),
                new CoreCandleData(DateTime.Today.AddDays(3), 1.0m, 1.05m, 0.95m, 1.0m, 100),
                new CoreCandleData(DateTime.Today.AddDays(4), 1.0m, 1.05m, 0.95m, 1.0m, 100),
            };

            var indicator = new CorePrimeNumberOscillatorIndicator
            {
                ScaleMultiplier = 10.0m,
                LookbackPeriod = 3,
                ConsecutiveExtremaPeriods = 2,
                Tolerance = 0.0m
            };

            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);

            // Check Buy Signals
            Assert.Null(indicator.BuySignals[0]);
            Assert.Null(indicator.BuySignals[1]);
            Assert.Equal(0.75m, indicator.BuySignals[2]); // Buy signal fired on Low
            Assert.Null(indicator.BuySignals[3]);
            Assert.Null(indicator.BuySignals[4]);

            // Check Sell Signals
            Assert.Null(indicator.SellSignals[0]);
            Assert.Null(indicator.SellSignals[1]);
            Assert.Null(indicator.SellSignals[2]);
            Assert.Null(indicator.SellSignals[3]);
            Assert.Equal(1.05m, indicator.SellSignals[4]); // Sell signal fired on High
        }

        [Fact]
        public void Calculate_ConsecutivePlateauDeduplication_SuppressesSubsequentSignals()
        {
            // 4 consecutive bars of PNO = -0.1 (Close = 0.8)
            // W = 3, K = 2
            // Day 0: PNO = -0.1
            // Day 1: PNO = -0.1
            // Day 2: PNO = -0.1 -> First plateau at extrema -> BUY Signal fired!
            // Day 3: PNO = -0.1 -> Second consecutive plateau bar -> Suppressed (null)
            // Day 4: PNO = -0.1 -> Third consecutive plateau bar -> Suppressed (null)
            var candles = Enumerable.Range(0, 5)
                .Select(i => new CoreCandleData(DateTime.Today.AddDays(i), 0.8m, 0.85m, 0.75m, 0.8m, 100))
                .ToList();

            var indicator = new CorePrimeNumberOscillatorIndicator
            {
                ScaleMultiplier = 10.0m,
                LookbackPeriod = 3,
                ConsecutiveExtremaPeriods = 2,
                Tolerance = 0.0m
            };

            indicator.Calculate(candles);

            Assert.Equal(0.75m, indicator.BuySignals[2]);
            Assert.Null(indicator.BuySignals[3]); // Suppressed
            Assert.Null(indicator.BuySignals[4]); // Suppressed
        }

        [Fact]
        public void Calculate_PlateauBreakAndReentry_TriggersNewSignal()
        {
            // W = 3, K = 2
            // Day 0, 1, 2: PNO = -0.1 -> Buy on Day 2
            // Day 3: PNO = 0.0 (Close = 0.7) -> Plateau broken
            // Day 4: PNO = -0.1 (Close = 0.8)
            // Day 5: PNO = -0.1 (Close = 0.8) -> Plateau re-entered -> Buy on Day 5
            var candles = new List<CoreCandleData>
            {
                new CoreCandleData(DateTime.Today.AddDays(0), 0.8m, 0.85m, 0.75m, 0.8m, 100),
                new CoreCandleData(DateTime.Today.AddDays(1), 0.8m, 0.85m, 0.75m, 0.8m, 100),
                new CoreCandleData(DateTime.Today.AddDays(2), 0.8m, 0.85m, 0.75m, 0.8m, 100), // Buy
                new CoreCandleData(DateTime.Today.AddDays(3), 0.7m, 0.75m, 0.65m, 0.7m, 100), // Break
                new CoreCandleData(DateTime.Today.AddDays(4), 0.8m, 0.85m, 0.75m, 0.8m, 100), // Start new plateau
                new CoreCandleData(DateTime.Today.AddDays(5), 0.8m, 0.85m, 0.75m, 0.8m, 100), // Re-entry Buy
            };

            var indicator = new CorePrimeNumberOscillatorIndicator
            {
                ScaleMultiplier = 10.0m,
                LookbackPeriod = 3,
                ConsecutiveExtremaPeriods = 2,
                Tolerance = 0.0m
            };

            indicator.Calculate(candles);

            Assert.Equal(0.75m, indicator.BuySignals[2]);
            Assert.Null(indicator.BuySignals[3]);
            Assert.Null(indicator.BuySignals[4]);
            Assert.Equal(0.75m, indicator.BuySignals[5]); // Re-triggered
        }

        #endregion

        #region Factory & Parameter Tests

        [Fact]
        public void Factory_CanCreateAndConfigurePrimeNumberOscillator()
        {
            var param = new CorePrimeNumberOscillatorParameter
            {
                ScaleMultiplier = 20.0m,
                ConsecutiveExtremaPeriods = 3,
                LookbackPeriod = 10,
                Tolerance = 0.5m
            };

            var indicator = IndicatorFactory.Default.Create(IndicatorType.PrimeNumberOscillator, param) as CorePrimeNumberOscillatorIndicator;

            Assert.NotNull(indicator);
            Assert.Equal(20.0m, indicator.ScaleMultiplier);
            Assert.Equal(3, indicator.ConsecutiveExtremaPeriods);
            Assert.Equal(10, indicator.LookbackPeriod);
            Assert.Equal(0.5m, indicator.Tolerance);
            Assert.False(indicator.IsOverlay);
        }

        [Fact]
        public void Parameter_Validate_ThrowsOnInvalidRanges()
        {
            var param = new CorePrimeNumberOscillatorParameter();

            param.ScaleMultiplier = 0.0m;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
            param.ScaleMultiplier = 10.0m;

            param.ConsecutiveExtremaPeriods = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
            param.ConsecutiveExtremaPeriods = 2;

            param.LookbackPeriod = 1;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
            param.LookbackPeriod = 5;

            param.Tolerance = -1.0m;
            Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        }

        #endregion
    }
}
