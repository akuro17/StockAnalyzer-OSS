using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CoreKeltnerChannelIndicatorTests
    {
        // Close and Open are set far apart so a Price-Source-driven center line is
        // trivially distinguishable from a hardcoded-Close center line.
        private static List<CoreCandleData> CreateTestCandles(int count)
        {
            var startDate = DateTime.Today;
            var candles = new List<CoreCandleData>();
            for (int i = 0; i < count; i++)
            {
                decimal close = 10m + i;
                decimal open = 500m + i; // deliberately far from Close
                candles.Add(new CoreCandleData(startDate.AddDays(i), open, close + 10, close - 10, close, 1000));
            }
            return candles;
        }

        private static (List<decimal?> Values, List<decimal?> Upper, List<decimal?> Lower) CalculateOldKeltner(
            IReadOnlyList<CoreCandleData> candles, int emaPeriod, int atrPeriod, decimal multiplier, PriceType priceSource = PriceType.Close)
        {
            var values = new List<decimal?>();
            var upper = new List<decimal?>();
            var lower = new List<decimal?>();

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, priceSource);
            decimal mult = 2m / (emaPeriod + 1);
            decimal? ema = null;

            var atr = new CoreAtrIndicator { Period = atrPeriod };
            atr.Calculate(candles);

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Math.Max(emaPeriod, atrPeriod) - 1 || !atr.Values[i].HasValue)
                {
                    values.Add(null);
                    upper.Add(null);
                    lower.Add(null);
                    continue;
                }

                if (ema == null)
                {
                    decimal sum = 0;
                    for (int j = 0; j < emaPeriod; j++) sum += priceSeries[i - j] ?? 0m;
                    ema = sum / emaPeriod;
                }
                else
                {
                    ema = ((priceSeries[i] ?? 0m) - ema.Value) * mult + ema.Value;
                }

                values.Add(ema);
                upper.Add(ema + multiplier * atr.Values[i]!.Value);
                lower.Add(ema - multiplier * atr.Values[i]!.Value);
            }

            return (values, upper, lower);
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreKeltnerChannelIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public void Calculate_DefaultPriceSource_IsClose()
        {
            var indicator = new CoreKeltnerChannelIndicator();
            Assert.Equal(PriceType.Close, indicator.PriceSource);
        }

        [Fact]
        public void Calculate_ChangingPriceSourceToOpen_ChangesCenterLine()
        {
            var candles = CreateTestCandles(30);

            var closeBased = new CoreKeltnerChannelIndicator { EmaPeriod = 5, AtrPeriod = 5, PriceSource = PriceType.Close };
            closeBased.Calculate(candles);

            var openBased = new CoreKeltnerChannelIndicator { EmaPeriod = 5, AtrPeriod = 5, PriceSource = PriceType.Open };
            openBased.Calculate(candles);

            int lastIndex = candles.Count - 1;
            Assert.NotNull(closeBased.Values[lastIndex]);
            Assert.NotNull(openBased.Values[lastIndex]);
            // Before the fix, PriceSource was ignored and both would be identical (Close-based).
            Assert.NotEqual(closeBased.Values[lastIndex], openBased.Values[lastIndex]);
        }

        [Theory]
        [InlineData(20, 10)]
        [InlineData(20, 5)]
        [InlineData(30, 14)]
        [InlineData(15, 7)]
        public void Calculate_AtrPeriodLessThanEmaPeriod_MatchesPreChangeAlgorithm(int emaPeriod, int atrPeriod)
        {
            var candles = CreateTestCandles(200);
            var indicator = new CoreKeltnerChannelIndicator
            {
                EmaPeriod = emaPeriod,
                AtrPeriod = atrPeriod,
                Multiplier = 2.0m,
                PriceSource = PriceType.Close
            };
            var result = indicator.Calculate(candles);

            var (oldValues, oldUpper, oldLower) = CalculateOldKeltner(candles, emaPeriod, atrPeriod, 2.0m, PriceType.Close);

            Assert.True(result.IsSuccessful);
            Assert.Equal(200, indicator.Values.Count);
            Assert.Equal(200, oldValues.Count);

            for (int i = 0; i < 200; i++)
            {
                Assert.Equal(oldValues[i], indicator.Values[i]);
                Assert.Equal(oldUpper[i], indicator.UpperBand[i]);
                Assert.Equal(oldLower[i], indicator.LowerBand[i]);
            }
        }

        [Fact]
        public void Calculate_EmaPeriod5AtrPeriod20_CenterLineMatchesCoreEmaIndicator()
        {
            var candles = CreateTestCandles(200);
            var keltner = new CoreKeltnerChannelIndicator { EmaPeriod = 5, AtrPeriod = 20, Multiplier = 2.0m };
            var ema = new CoreEmaIndicator { Period = 5 };

            var keltnerResult = keltner.Calculate(candles);
            var emaResult = ema.Calculate(candles);

            Assert.True(keltnerResult.IsSuccessful);
            Assert.True(emaResult.IsSuccessful);

            // CoreAtrIndicator with Period=20 has first non-null at index 20 (index 0..19 are null).
            // Thus keltner values for index < 20 are null.
            for (int i = 0; i < 20; i++)
            {
                Assert.Null(keltner.Values[i]);
                Assert.Null(keltner.UpperBand[i]);
                Assert.Null(keltner.LowerBand[i]);
            }

            // For index >= 20, center line matches CoreEmaIndicator
            for (int i = 20; i < 200; i++)
            {
                Assert.NotNull(keltner.Values[i]);
                Assert.Equal(ema.Values[i], keltner.Values[i]);
            }
        }

        [Fact]
        public void Calculate_EmaPeriod5AtrPeriod19_CenterLineMatchesCoreEmaIndicatorFromIndex19()
        {
            var candles = CreateTestCandles(200);
            var keltner = new CoreKeltnerChannelIndicator { EmaPeriod = 5, AtrPeriod = 19, Multiplier = 2.0m };
            var ema = new CoreEmaIndicator { Period = 5 };

            var keltnerResult = keltner.Calculate(candles);
            var emaResult = ema.Calculate(candles);

            Assert.True(keltnerResult.IsSuccessful);
            Assert.True(emaResult.IsSuccessful);

            // CoreAtrIndicator with Period=19 has first non-null at index 19 (index 0..18 are null).
            for (int i = 0; i < 19; i++)
            {
                Assert.Null(keltner.Values[i]);
            }

            for (int i = 19; i < 200; i++)
            {
                Assert.NotNull(keltner.Values[i]);
                Assert.Equal(ema.Values[i], keltner.Values[i]);
            }
        }
    }
}
