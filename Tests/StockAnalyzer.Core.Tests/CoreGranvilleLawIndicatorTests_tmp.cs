using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Tests
{
    public class CoreGranvilleLawIndicatorTests
    {
        private static List<CoreCandleData> CreateFlatCandles(int count, decimal price)
        {
            var candles = new List<CoreCandleData>();
            var startDate = new DateTime(2023, 1, 1);
            for (int i = 0; i < count; i++)
            {
                candles.Add(new CoreCandleData(startDate.AddDays(i), price, price, price, price, 100));
            }
            return candles;
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreGranvilleLawIndicator();
            var result = indicator.Calculate(new List<CoreCandleData>());

            Assert.True(result.IsSuccessful);
            Assert.Empty(result.GetSeries("Signals"));
        }

        [Fact]
        public void Calculate_WithInsufficientData_ReturnsNulls()
        {
            var indicator = new CoreGranvilleLawIndicator();
            indicator.Configure(new CoreGranvilleLawParameter { MaPeriod = 10, SlopePeriod = 2 });

            var candles = CreateFlatCandles(5, 100m);
            var result = indicator.Calculate(candles);

            Assert.True(result.IsSuccessful);
            var buySignals = result.GetSeries("Signals").ToList();
            Assert.Equal(5, buySignals.Count);
            Assert.All(buySignals, signal => Assert.Null(signal));
        }

        [Fact]
        public void Calculate_Buy1_NewBuy_Detected()
        {
            var indicator = new CoreGranvilleLawIndicator();
            indicator.Configure(new CoreGranvilleLawParameter { MaPeriod = 5, SlopePeriod = 2 });

            var candles = CreateFlatCandles(6, 100m);
            // MA is flat at 100.
            // Bar 6: Price drops to 90 (below MA)
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(6), 90m, 90m, 90m, 90m, 100));
            // Bar 7: Price breaks above MA to 110
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(7), 110m, 110m, 110m, 110m, 100));

            var result = indicator.Calculate(candles);
            var buySignals = result.GetSeries("Signals").ToList();

            Assert.Equal((decimal)GranvilleLawSignalType.Buy1_NewBuy, buySignals[7]);
        }

        [Fact]
        public void Calculate_Sell1_NewSell_Detected()
        {
            var indicator = new CoreGranvilleLawIndicator();
            indicator.Configure(new CoreGranvilleLawParameter { MaPeriod = 5, SlopePeriod = 2 });

            var candles = CreateFlatCandles(6, 100m);
            // MA is flat at 100.
            // Bar 6: Price rises to 110 (above MA)
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(6), 110m, 110m, 110m, 110m, 100));
            // Bar 7: Price breaks below MA to 90
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(7), 90m, 90m, 90m, 90m, 100));

            var result = indicator.Calculate(candles);
            var sellSignals = result.GetSeries("Signals").ToList();

            Assert.Equal((decimal)GranvilleLawSignalType.Sell1_NewSell, sellSignals[7]);
        }

        [Fact]
        public void Calculate_Buy4_ReversalBuy_Detected()
        {
            var indicator = new CoreGranvilleLawIndicator();
            indicator.Configure(new CoreGranvilleLawParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 10m });

            // Create falling MA (need at least 7 candles)
            var candles = new List<CoreCandleData>();
            var startDate = new DateTime(2023, 1, 1);
            decimal price = 100m;
            for (int i = 0; i < 7; i++)
            {
                candles.Add(new CoreCandleData(startDate.AddDays(i), price, price, price, price, 100));
                price -= 2m; // Constant drop
            }
            
            // Drop price massively to trigger B4 (> 10% deviation)
            candles.Add(new CoreCandleData(startDate.AddDays(7), 70m, 70m, 70m, 70m, 100));

            var result = indicator.Calculate(candles);
            var buySignals = result.GetSeries("Signals").ToList();

            Assert.Equal((decimal)GranvilleLawSignalType.Buy4_ReversalBuy, buySignals.Last());
        }

        [Fact]
        public void Calculate_Sell4_ReversalSell_Detected()
        {
            var indicator = new CoreGranvilleLawIndicator();
            indicator.Configure(new CoreGranvilleLawParameter { MaPeriod = 5, SlopePeriod = 2, DeviationThreshold = 10m });

            // Create rising MA (need at least 7 candles)
            var candles = new List<CoreCandleData>();
            var startDate = new DateTime(2023, 1, 1);
            decimal price = 100m;
            for (int i = 0; i < 7; i++)
            {
                candles.Add(new CoreCandleData(startDate.AddDays(i), price, price, price, price, 100));
                price += 2m; // Constant rise
            }
            
            // Spike price massively to trigger S4 (> 10% deviation)
            candles.Add(new CoreCandleData(startDate.AddDays(7), 130m, 130m, 130m, 130m, 100));

            var result = indicator.Calculate(candles);
            var sellSignals = result.GetSeries("Signals").ToList();

            Assert.Equal((decimal)GranvilleLawSignalType.Sell4_ReversalSell, sellSignals.Last());
        }
    }
}
