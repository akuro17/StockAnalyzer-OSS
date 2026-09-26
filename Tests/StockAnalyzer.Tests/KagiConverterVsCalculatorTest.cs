using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Tests
{
    /// <summary>
    /// Diagnostic test to compare KagiConverter output vs KagiCalculator output.
    /// This test verifies that both produce identical Close values (which SMA(1) uses).
    /// </summary>
    public class KagiConverterVsCalculatorTest
    {
        private readonly ITestOutputHelper _output;
        
        public KagiConverterVsCalculatorTest(ITestOutputHelper output) => _output = output;

        [Fact]
        public void BothConverters_ShouldProduceIdenticalCloseValues()
        {
            // Test data with multiple reversals
            var candles = new List<CoreCandleData>
            {
                new(new DateTime(2023, 1, 1), 100, 100, 100, 100, 100),
                new(new DateTime(2023, 1, 2), 100, 115, 100, 115, 100),
                new(new DateTime(2023, 1, 3), 115, 120, 115, 120, 100),
                new(new DateTime(2023, 1, 4), 120, 120, 108, 108, 100),  // Reversal down (120-108=12 >= 10)
                new(new DateTime(2023, 1, 5), 108, 108, 100, 100, 100),  // Continue down
                new(new DateTime(2023, 1, 6), 100, 112, 100, 112, 100),  // Reversal up (100+10=110 <= 112)
                new(new DateTime(2023, 1, 7), 112, 125, 112, 125, 100),  // Continue up
                new(new DateTime(2023, 1, 8), 125, 125, 113, 113, 100),  // Reversal down (125-113=12 >= 10)
            };

            decimal threshold = 10m;

            // Path 1: KagiConverter
            var converterResult = KagiConverter.Convert(candles, threshold);
            
            // Path 2: KagiCalculator (via ZeroAllocCandleData)
            var zeroAllocCandles = candles.Select(c => 
                new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)
            ).ToArray();
            var calcResult = KagiCalculator.Calculate(zeroAllocCandles, new KagiParameters(threshold));

            _output.WriteLine($"KagiConverter segments: {converterResult.Count}");
            for (int i = 0; i < converterResult.Count; i++)
            {
                var seg = converterResult[i];
                _output.WriteLine($"  [{i}] Open={seg.Open}, Close={seg.Close}, IsYang={seg.IsYang}, Dir={( seg.Close >= seg.Open ? "Up" : "Down" )}");
            }
            
            _output.WriteLine($"\nKagiCalculator segments: {calcResult.Count}");
            var calcCloses = calcResult.Closes.Span;
            var calcOpens = calcResult.Opens.Span;
            var calcVols = calcResult.Volumes.Span;
            for (int i = 0; i < calcResult.Count; i++)
            {
                _output.WriteLine($"  [{i}] Open={calcOpens[i]}, Close={calcCloses[i]}, IsYang={calcVols[i] == 1}, Dir={( calcCloses[i] >= calcOpens[i] ? "Up" : "Down" )}");
            }

            // Assert same count
            Assert.Equal(converterResult.Count, calcResult.Count);

            // Assert same Close values (this is what SMA(1) uses)
            for (int i = 0; i < converterResult.Count; i++)
            {
                Assert.Equal(converterResult[i].Close, calcCloses[i]);
                Assert.Equal(converterResult[i].Open, calcOpens[i]);
            }
        }
    }
}
