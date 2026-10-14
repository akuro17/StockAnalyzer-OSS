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

        private static List<CoreCandleData> ReversalSeries() => new()
        {
            new(new DateTime(2023, 1, 1), 100, 100, 100, 100, 100),
            new(new DateTime(2023, 1, 2), 100, 115, 100, 115, 100),
            new(new DateTime(2023, 1, 3), 115, 120, 115, 120, 100),
            new(new DateTime(2023, 1, 4), 120, 120, 108, 108, 100),
            new(new DateTime(2023, 1, 5), 108, 108, 100, 100, 100),
            new(new DateTime(2023, 1, 6), 100, 112, 100, 112, 100),
            new(new DateTime(2023, 1, 7), 112, 125, 112, 125, 100),
            new(new DateTime(2023, 1, 8), 125, 125, 113, 113, 100),
        };

        private static ElementDates[] KagiExpectedDates() => new[]
        {
            new ElementDates(new DateTime(2023, 1, 1), new DateTime(2023, 1, 1)), // flat anchor
            new ElementDates(new DateTime(2023, 1, 2), new DateTime(2023, 1, 3)), // created 01-02, extended 01-03
            new ElementDates(new DateTime(2023, 1, 4), new DateTime(2023, 1, 5)), // reversal confirmed 01-04, extended 01-05
            new ElementDates(new DateTime(2023, 1, 6), new DateTime(2023, 1, 7)),
            new ElementDates(new DateTime(2023, 1, 8), new DateTime(2023, 1, 8)), // never extended: End == Start
        };

        private static ZeroAllocCandleData[] ToZeroAlloc(IEnumerable<CoreCandleData> candles) =>
            candles.Select(c => new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();

        [Fact]
        public void Converter_ElementDates_RecordCreationAndLastUpdate()
        {
            var dates = new List<ElementDates>();
            var output = new List<KagiCandleData>();
            KagiConverter.Convert(ReversalSeries(), 10m, KagiConverter.ReversalType.Fixed, 14, 1.0m,
                ChartRoundingMode.None, AutoFallbackMode.Percentage, output, dates);

            Assert.Equal(KagiExpectedDates(), dates);
            Assert.Equal(output.Count, dates.Count);
            for (int i = 0; i < output.Count; i++)
                Assert.Equal(output[i].Timestamp, dates[i].End); // existing Timestamp semantic: last update
        }

        [Fact]
        public void Converter_WithoutElementDatesBuffer_GivesTheSameSegments()
        {
            var candles = ReversalSeries();
            var withBuffer = new List<KagiCandleData>();
            var without = new List<KagiCandleData>();
            KagiConverter.Convert(candles, 10m, KagiConverter.ReversalType.Fixed, 14, 1.0m,
                ChartRoundingMode.None, AutoFallbackMode.Percentage, withBuffer, new List<ElementDates>());
            KagiConverter.Convert(candles, 10m, KagiConverter.ReversalType.Fixed, 14, 1.0m,
                ChartRoundingMode.None, AutoFallbackMode.Percentage, without);

            Assert.Equal(without, withBuffer);
        }

        [Fact]
        public void Calculator_ElementDates_RecordCreationAndLastUpdate()
        {
            var result = KagiCalculator.Calculate(ToZeroAlloc(ReversalSeries()), new KagiParameters(10m));

            Assert.Equal(KagiExpectedDates(), result.ElementDates.ToArray());
            Assert.Equal(result.Count, result.ElementDates.Length);
        }

        [Fact]
        public void BothConverters_GiveEqualElementDates_IncludingSplitSegments()
        {
            // A deterministic random walk long enough to cross earlier highs/lows (Yin/Yang splits) many times.
            var random = new Random(20261013);
            var candles = new List<CoreCandleData>();
            decimal price = 1000m;
            var day = new DateTime(2020, 1, 1);
            for (int i = 0; i < 600; i++)
            {
                price += random.Next(-30, 31);
                candles.Add(new CoreCandleData(day.AddDays(i), price, price, price, price, 100));
            }

            var dates = new List<ElementDates>();
            var output = new List<KagiCandleData>();
            KagiConverter.Convert(candles, 20m, KagiConverter.ReversalType.Fixed, 14, 1.0m,
                ChartRoundingMode.None, AutoFallbackMode.Percentage, output, dates);
            var calc = KagiCalculator.Calculate(ToZeroAlloc(candles), new KagiParameters(20m));

            Assert.True(output.Count > 20);
            Assert.Equal(output.Count, calc.Count);
            Assert.Equal(dates, calc.ElementDates.ToArray());
            Assert.All(dates, d => Assert.True(d.Start <= d.End));
        }

        [Fact]
        public void Adapter_RejectsElementDatesOfAnotherLength()
        {
            var t = new[] { new DateTime(2023, 1, 1) };
            var p = new[] { 1m };
            Assert.Throws<ArgumentException>(() => new ChartSegmentAdapter(
                ZeroAllocChartDataType.Kagi, t, p, p, p, p, new long[1], new OriginalIndexRange[1], new ElementDates[2]));
            var withoutDates = new ChartSegmentAdapter(
                ZeroAllocChartDataType.Kagi, t, p, p, p, p, new long[1], new OriginalIndexRange[1]);
            Assert.True(withoutDates.ElementDates.IsEmpty);
        }
    }
}
