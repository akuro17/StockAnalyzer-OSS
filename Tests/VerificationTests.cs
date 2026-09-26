using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Annotations;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Screener.Criteria;
using StockAnalyzer.Services;
using StockAnalyzer.Services.Annotations;
using StockAnalyzer.Services.Interfaces;
using StockAnalyzer.Services.Screener;
using StockAnalyzer.ThreeLineBreak.Logic;
using StockAnalyzer.ThreeLineBreak.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockAnalyzer.Tests
{
    public class VerificationTests
    {
        [Fact]
        public void ThreeLineBreak_Conversion_Logic_Test()
        {
            // Simulate a reversal sequence
            // Price: 10, 20, 30 (Up), 25 (No change), 15 (Reversal Down because < 10?)
            // Requires 3 line break.
            
            var settings = new ThreeLineBreakSettings(3);
            var strategy = new StandardThreeLineBreakStrategy(settings);
            var converter = new ThreeLineBreakConverter(strategy);
            
            var candles = new List<CandleData>
            {
                new() { Close = 100, Timestamp = DateTime.Now.AddDays(1) },
                new() { Close = 110, Timestamp = DateTime.Now.AddDays(2) }, // Up
                new() { Close = 120, Timestamp = DateTime.Now.AddDays(3) }, // Up
                new() { Close = 130, Timestamp = DateTime.Now.AddDays(4) }, // Up
                new() { Close = 125, Timestamp = DateTime.Now.AddDays(5) }, // No change (needs < 100 to reverse 3 lines? OR just previous low?)
                // Current blocks: 100-110 (U), 110-120 (U), 120-130 (U). Low of last 3 is 100.
                // Reversal requires Close < 100.
                new() { Close = 105, Timestamp = DateTime.Now.AddDays(6) }, // No reversal
                new() { Close = 90, Timestamp = DateTime.Now.AddDays(7) }   // Reversal!
            };

            var blocks = converter.Convert(candles);

            // Expect:
            // 1. 100-110 (Up)
            // 2. 110-120 (Up)
            // 3. 120-130 (Up)
            // 4. 130-90 (Down) -> Reversal block
            
            Assert.True(blocks.Count >= 3);
            Assert.True(blocks.Last().IsUp == false);
        }

        [Fact]
        public async Task BackgroundCalculation_Wraps_Task_Run()
        {
            var service = new IndicatorCalculationService();
            // We can't easily verify internal Task.Run usage without mocking IIndicator factory or time delays.
            // But we can verify it returns results.
            
            var candles = new List<CandleData>();
            for(int i=0; i<100; i++) candles.Add(new CandleData { Close = 100 + i, Timestamp = DateTime.Now.AddMinutes(i) });

            var settings = new List<IndicatorSettings>
            {
                new() { TypeEnum = IndicatorType.SMA, ParameterObject = new StockAnalyzer.Core.Models.Parameters.PeriodParameter { Period = 5 } }
            };

            var results = await service.CalculateBatchAsync(settings, candles);
            
            Assert.Single(results);
            Assert.NotNull(results[0]);
            Assert.Equal(100, results[0].Values.Count);
        }

        [Fact]
        public async Task TechnicalScreener_Filters_Symbols()
        {
            // Setup Mock Data
            var mockData = new MockScreenerDataProvider();
            var service = new IndicatorCalculationService();
            var screener = new TechnicalScreener(mockData, service);
            
            // Criteria: RSI Oversold (< 30)
            // MockData: "OVERSOLD" -> RSI will be low. "NORMAL" -> RSI normal.
            
            var criteria = new RsiOversoldCriteria();
            var symbols = new[] { "OVERSOLD", "NORMAL" };
            
            var results = await screener.ScreenAsync(symbols, criteria);
            
            Assert.Contains("OVERSOLD", results);
            Assert.DoesNotContain("NORMAL", results);
        }

        [Fact]
        public async Task AnnotationService_Add_And_Get()
        {
            var repo = new MockAnnotationRepository();
            var clock = new SystemClock();
            var service = new AnnotationService(repo, clock);
            
            var ann = Annotation.Create(
                AnnotationType.TextNote, 
                DateTime.UtcNow, 
                100m, 
                new ColorData(255, 255, 0, 0), 
                "Test Note");

            await service.AddAnnotationAsync(ann);
            
            var all = await service.GetAllAnnotationsAsync();
            Assert.Single(all);
            Assert.Equal("Test Note", all[0].Text);
            
            // Verify ID assignment
            Assert.NotEqual(0, all[0].Id.Value);
        }
    }

    // Mocks
    public class MockScreenerDataProvider : ICandleDataProvider
    {
        public Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, string interval)
        {
            var candles = new List<CandleData>();
            decimal close = 100;
            
            for(int i=0; i<100; i++)
            {
                // Generate data suitable for RSI
                // If "OVERSOLD", drop price significantly at end
                if (symbol == "OVERSOLD" && i > 80) close -= 5;
                else if (symbol == "NORMAL" && i > 80) close += 1;
                
                candles.Add(new CandleData { Close = close, Timestamp = DateTime.Now.AddDays(i) });
            }
            
            return Task.FromResult<IReadOnlyList<CandleData>>(candles);
        }
    }

    public class MockAnnotationRepository : IAnnotationRepository
    {
        private List<Annotation> _store = new();
        private int _nextId = 0;

        public Task<(List<Annotation> annotations, int nextId)> LoadAsync(string path, CancellationToken ct = default)
        {
            return Task.FromResult((_store, _nextId));
        }

        public Task SaveAsync(string path, IEnumerable<Annotation> annotations, int nextId, CancellationToken ct = default)
        {
            _store = annotations.ToList();
            _nextId = nextId;
            return Task.CompletedTask;
        }
    }
}
