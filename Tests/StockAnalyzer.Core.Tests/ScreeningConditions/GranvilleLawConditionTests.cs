using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.ScreeningConditions;

namespace StockAnalyzer.Core.Tests.ScreeningConditions
{
    public class GranvilleLawConditionTests
    {
        private static List<CandleData> CreateFlatCandles(int count, decimal price)
        {
            var candles = new List<CandleData>();
            var startDate = new DateTime(2023, 1, 1);
            for (int i = 0; i < count; i++)
            {
                candles.Add(new CandleData(startDate.AddDays(i), price, price, price, price, 100));
            }
            return candles;
        }

        [Fact]
        public void IsMet_WithInsufficientData_ReturnsFalse()
        {
            var condition = new GranvilleLawCondition(GranvilleLawConditionType.AnyBuy, new CoreGranvilleLawParameter { MaPeriod = 10, SlopePeriod = 2 });
            var candles = CreateFlatCandles(5, 100m); // Insufficient data

            var result = condition.IsMet(candles);

            Assert.False(result);
        }

        [Fact]
        public void IsMet_Buy1_NewBuy_MatchesAnyBuyAndSpecificBuy()
        {
            var parameter = new CoreGranvilleLawParameter { MaPeriod = 5, SlopePeriod = 2 };
            var candles = CreateFlatCandles(6, 100m);
            candles.Add(new CandleData(DateTime.Today.AddDays(6), 90m, 90m, 90m, 90m, 100)); // Price drops below MA
            candles.Add(new CandleData(DateTime.Today.AddDays(7), 110m, 110m, 110m, 110m, 100)); // Price breaks above MA
            
            // Expected signal: Buy1 on the last candle

            var conditionAnyBuy = new GranvilleLawCondition(GranvilleLawConditionType.AnyBuy, parameter);
            var conditionBuy1 = new GranvilleLawCondition(GranvilleLawConditionType.Buy1_NewBuy, parameter);
            var conditionBuy2 = new GranvilleLawCondition(GranvilleLawConditionType.Buy2_PullbackBuy, parameter);
            var conditionAnySell = new GranvilleLawCondition(GranvilleLawConditionType.AnySell, parameter);

            Assert.True(conditionAnyBuy.IsMet(candles));
            Assert.True(conditionBuy1.IsMet(candles));
            Assert.False(conditionBuy2.IsMet(candles)); // Should not match Buy2
            Assert.False(conditionAnySell.IsMet(candles)); // Should not match Sell
        }

        [Fact]
        public void IsMet_Sell1_NewSell_MatchesAnySellAndSpecificSell()
        {
            var parameter = new CoreGranvilleLawParameter { MaPeriod = 5, SlopePeriod = 2 };
            var candles = CreateFlatCandles(6, 100m);
            candles.Add(new CandleData(DateTime.Today.AddDays(6), 110m, 110m, 110m, 110m, 100)); // Price rises above MA
            candles.Add(new CandleData(DateTime.Today.AddDays(7), 90m, 90m, 90m, 90m, 100)); // Price breaks below MA
            
            // Expected signal: Sell1 on the last candle

            var conditionAnySell = new GranvilleLawCondition(GranvilleLawConditionType.AnySell, parameter);
            var conditionSell1 = new GranvilleLawCondition(GranvilleLawConditionType.Sell1_NewSell, parameter);
            var conditionSell2 = new GranvilleLawCondition(GranvilleLawConditionType.Sell2_ReturnSell, parameter);
            var conditionAnyBuy = new GranvilleLawCondition(GranvilleLawConditionType.AnyBuy, parameter);

            Assert.True(conditionAnySell.IsMet(candles));
            Assert.True(conditionSell1.IsMet(candles));
            Assert.False(conditionSell2.IsMet(candles)); // Should not match Sell2
            Assert.False(conditionAnyBuy.IsMet(candles)); // Should not match Buy
        }
    }
}
