using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.ViewModels;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels
{
    public class SynchronousSynchronizationContext : System.Threading.SynchronizationContext
    {
        public override void Post(System.Threading.SendOrPostCallback d, object state)
        {
            d(state);
        }
    }

    public class ChartStateViewModelTests
    {
        private readonly ChartStateViewModel _vm;

        public ChartStateViewModelTests()
        {
            _vm = new ChartStateViewModel(new SynchronousSynchronizationContext());
        }

        private static MarketData CreateMarketData(int count)
        {
            var candles = Enumerable.Range(0, count).Select(i => new CandleData
            {
                Timestamp = DateTime.Today.AddDays(i),
                Open = 100 + i,
                High = 102 + i,
                Low = 99 + i,
                Close = 101 + i,
                Volume = 1000
            }).ToList();

            return new MarketData(Symbol.Create("TEST"), TimeInterval.OneDay, candles, "MOCK");
        }

        [Fact]
        public async Task UpdateVisibleCandles_HandlesNullOrEmptyData_ResetsState()
        {
            // Priority 1: Verify Null Data Handling
            
            // Should not crash on null
            await _vm.UpdateVisibleCandlesAsync(null, new ObservableCollection<IIndicator>());
            
            Assert.Empty(_vm.VisibleCandles);
            Assert.Equal(0, _vm.MaxPrice);
            Assert.Equal(0, _vm.MinPrice);

            // Should not crash on empty
            var emptyData = new MarketData(Symbol.Create("TEST"), TimeInterval.OneDay, new List<CandleData>(), "MOCK");
            await _vm.UpdateVisibleCandlesAsync(emptyData, new ObservableCollection<IIndicator>());

            Assert.Empty(_vm.VisibleCandles);
        }

        [Fact]
        public async Task Scroll_WithIchimoku_AllowsFutureDisplacement()
        {
            // Priority 2: Verify Future Scroll (Ichimoku logic)
            var count = 100;
            var data = CreateMarketData(count);
            var visibleCount = _vm.VisibleCandleCount; // default 50
            
            // Initially, we are at the end: StartIndex = 100 - 50 = 50
            // If we try to scroll RIGHT, it should be clamped at 50 because there is no future data.
            
            // 1. Base case: No indicators
            await _vm.UpdateVisibleCandlesAsync(data, new ObservableCollection<IIndicator>());
            _vm.StartIndex = count - visibleCount; // Set to end
            
            // Try scroll right
            _vm.Scroll(10, data, new ObservableCollection<IIndicator>());
            Assert.Equal(count - visibleCount, _vm.StartIndex); // Should not move further

            // 2. Ichimoku case: Displacement = 26
            var ichimoku = new IchimokuIndicator();
            ichimoku.Parameters.Displacement = 26;
            var indicators = new ObservableCollection<IIndicator> { ichimoku };

            // Reset start index to base max
            _vm.StartIndex = count - visibleCount; 
            
            // Try scroll right
            _vm.Scroll(10, data, indicators);
            
            // Should be allowed to move into future
            // Max allowed should be BaseMax (50) + Displacement (26) = 76
            // We moved 10, so new StartIndex should be 60
            Assert.True(_vm.StartIndex > count - visibleCount, "Should allow scrolling into future");
            Assert.Equal(count - visibleCount + 10, _vm.StartIndex);
        }

        [Fact]
        public async Task Zoom_ClampsToMinVisibleCandles()
        {
            // Priority 3: Verify Zoom Limits
            var data = CreateMarketData(100);
            await _vm.UpdateVisibleCandlesAsync(data, new ObservableCollection<IIndicator>());
            
            // Default VisibleCandleCount is 50.
            // Try to Zoom IN (reduce visible count) by a huge amount
            // e.g. 100
            
            _vm.Zoom(100, data, new ObservableCollection<IIndicator>());
            
            // Should be clamped to MinVisibleCandles (10)
            Assert.Equal(10, _vm.VisibleCandleCount);
            Assert.Equal(10, _vm.VisibleCandles.Count);
        }
    }
}
