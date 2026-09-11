using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Services.Infrastructure;
using StockAnalyzer.Services.Drawing;

namespace StockAnalyzer.Tests.ViewModels
{
    public class TestDispatcher : IDispatcherService
    {
        public bool CheckAccess() => true;
        public void BeginInvoke(Action action) => action();
        public void Invoke(Action action) => action();
    }

    public class ChartViewModelTests
    {
        private readonly ChartViewModel _vm;

        public ChartViewModelTests()
        {
            var dispatcher = new TestDispatcher();
            var registry = new DrawingToolRegistry(dispatcher);
            var drawing = new DrawingViewModel(dispatcher, registry);
            _vm = new ChartViewModel(drawing);
        }
        private static MarketData CreateMarketData(int count)
        {
            var candles = Enumerable.Range(0, count).Select(i => new CandleData
            {
                Timestamp = DateTime.Now.AddDays(i),
                Open = 100 + i,
                High = 102 + i,
                Low = 99 + i,
                Close = 101 + i,
                Volume = 1000
            }).ToList();

            return new MarketData(Symbol.Create("TEST"), TimeInterval.OneDay, candles, "MOCK");
        }

        [Fact]
        public void ApplyChartTypeConversion_Renko_ConvertsToRenkoCandles()
        {
            // Arrange
            var vm = _vm;
            var data = CreateMarketData(20);
            vm.SetChartData(data); // Initial set (Candlestick)

            // Act
            vm.SelectedChartType = ChartType.Renko; // Triggers conversion

            // Assert
            Assert.NotNull(vm.ChartData);
            Assert.NotEqual(data.Count, vm.ChartData.Count); // Renko blocks count != candle count
            Assert.Equal(ChartType.Renko, vm.SelectedChartType);
            
            // Verify content is Renko-like (OHLC implying brick)
            var first = vm.ChartData[0];
            // In our Renko implementation, RenderHigh/Low are set.
            // Check that volume is 0 as per our simplified mapping
            Assert.Equal(0, first.Volume);
        }

        [Fact]
        public void SelectedChartTypeIndex_Setter_UpdatesEnum()
        {
            var vm = _vm;
            vm.SelectedChartTypeIndex = 4; // Renko
            Assert.Equal(ChartType.Renko, vm.SelectedChartType);

            vm.SelectedChartTypeIndex = 0; // Candlestick
            Assert.Equal(ChartType.Candlestick, vm.SelectedChartType);
        }

        [Fact]
        public void SetChartData_SwitchToRenkoAndBack_PreservesOriginalData()
        {
            // Priority 4: Verify data state preservation
            var vm = _vm;
            var originalData = CreateMarketData(20);
            
            // 1. Initial Set
            vm.SetChartData(originalData);
            Assert.Equal(20, vm.ChartData.Count);
            
            // 2. Switch to Renko
            vm.SelectedChartType = ChartType.Renko;
            Assert.Equal(ChartType.Renko, vm.SelectedChartType);
            Assert.NotEqual(20, vm.ChartData.Count); // Should change
            
            // 3. Switch back to Candle
            vm.SelectedChartType = ChartType.Candlestick;
            
            // 4. Verify Original Data Restored
            Assert.Equal(20, vm.ChartData.Count);
            Assert.Equal(originalData[0].Close, vm.ChartData[0].Close);
            Assert.Equal(originalData[19].Close, vm.ChartData[19].Close);
        }

        [Fact]
        public void ApplyChartTypeConversion_Renko_GeneratesCorrectBlockCount()
        {
            // Priority 5: Verify Renko Generation Logic
            var vm = _vm;
            
            // Create sufficient data for ATR calculation (Renko Auto Box Size usually needs history)
            // Generate 50 candles with small movement to stabilize ATR, then a massive jump
            var candles = new List<CandleData>();
            var startDate = DateTime.Today;
            decimal price = 100m;
            
            // Increase history to 50 for better ATR stability
            for (int i = 0; i < 50; i++)
            {
                candles.Add(new CandleData { Timestamp = startDate.AddDays(i), Open = price, High = price + 1, Low = price - 1, Close = price, Volume = 100 });
                // price stays around 100
            }
            
            // Massive jump at index 20: 100 -> 200 (100% increase)
            // Even with a conservative ATR, this should produce multiple blocks
            candles.Add(new CandleData { Timestamp = startDate.AddDays(50), Open = price, High = 200, Low = 100, Close = 200, Volume = 1000 });
            
            var data = new MarketData(Symbol.Create("TEST"), TimeInterval.OneDay, candles, "MOCK");
            
            vm.SetChartData(data);
            vm.SelectedChartType = ChartType.Renko;
            
            // With a jump from 100 to 200, we expect multiple blocks
            Assert.NotNull(vm.ChartData);
            Assert.True(vm.ChartData.Count > 1, $"Expected multiple blocks for a 100% price jump, got {vm.ChartData.Count}. (ATR-based BoxSize might be too large)");
        }
    }
}
