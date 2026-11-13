using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests
{
    public class CorePivotPointsAdvancedIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
        {
            var startDate = DateTime.Today;
            return closePrices.Select((price, i) => new CoreCandleData(
                startDate.AddDays(i), price, price + 5, price - 5, price, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CorePivotPointsAdvancedIndicator();
            indicator.Calculate(new List<CoreCandleData>());
            Assert.Empty(indicator.Values);
        }

        // Previous bar: O=102, H=110, L=100, C=105 (D = 10). The second bar only receives the levels.
        private static List<CoreCandleData> PreviousBarThenOne() => new()
        {
            new CoreCandleData(new DateTime(2026, 1, 5), 102m, 110m, 100m, 105m, 1000),
            new CoreCandleData(new DateTime(2026, 1, 6), 106m, 112m, 104m, 108m, 1000),
        };

        private static decimal?[] Levels(IIndicatorResult result)
            => new[] { "Main", "R1", "R2", "R3", "R4", "S1", "S2", "S3", "S4" }.Select(name => result.GetSeries(name)[1]).ToArray();

        private static void AssertLevels(decimal?[] expected, decimal?[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] is null) { Assert.Null(actual[i]); continue; }
                Assert.NotNull(actual[i]);
                Assert.Equal(expected[i]!.Value, actual[i]!.Value, 10);
            }
        }

        // Order: Main(P), R1, R2, R3, R4, S1, S2, S3, S4
        [Fact]
        public void Standard_ComputesTheNineSeriesFromThePreviousBar()
        {
            var indicator = new CorePivotPointsAdvancedIndicator { PivotType = PivotPointType.Standard };
            AssertLevels(new decimal?[] { 105m, 110m, 115m, 120m, null, 100m, 95m, 90m, null }, Levels(indicator.Calculate(PreviousBarThenOne())));
        }

        [Fact]
        public void Fibonacci_UsesTheThreeRatios()
        {
            var indicator = new CorePivotPointsAdvancedIndicator { PivotType = PivotPointType.Fibonacci };
            AssertLevels(new decimal?[] { 105m, 108.82m, 111.18m, 115m, null, 101.18m, 98.82m, 95m, null }, Levels(indicator.Calculate(PreviousBarThenOne())));
        }

        [Fact]
        public void Woodie_UsesThePreviousCloseWeightedTwice()
        {
            var indicator = new CorePivotPointsAdvancedIndicator { PivotType = PivotPointType.Woodie };
            AssertLevels(new decimal?[] { 105m, 110m, 115m, 120m, null, 100m, 95m, 90m, null }, Levels(indicator.Calculate(PreviousBarThenOne())));
        }

        [Fact]
        public void Woodie_PivotChangesWithThePreviousCloseNotThePreviousOpen()
        {
            var candles = PreviousBarThenOne();
            candles[0] = new CoreCandleData(candles[0].Timestamp, 90m, 110m, 100m, 104m, 1000); // another open, close 104
            var indicator = new CorePivotPointsAdvancedIndicator { PivotType = PivotPointType.Woodie };
            var pivot = indicator.Calculate(candles).GetSeries("Main")[1];
            Assert.Equal((110m + 100m + 2m * 104m) / 4m, pivot);
        }

        [Fact]
        public void Camarilla_UsesTheFourLiteralMultipliers()
        {
            var indicator = new CorePivotPointsAdvancedIndicator { PivotType = PivotPointType.Camarilla };
            decimal p = (110m + 100m + 105m) / 3m;
            AssertLevels(
                new decimal?[] { p, 105m + 11m / 12m, 105m + 11m / 6m, 107.75m, 110.5m, 105m - 11m / 12m, 105m - 11m / 6m, 102.25m, 99.5m },
                Levels(indicator.Calculate(PreviousBarThenOne())));
        }

        // R4/S4 are hidden from the output catalog (so the generic L2 causality test skips them: null for every type but Camarilla);
        // for Camarilla they are checked here: non-null from bar 1 and unchanged below a poisoned tail.
        [Fact]
        public void Camarilla_R4AndS4_AreNonNullAndCausal()
        {
            const int count = 80;
            const int poisonStart = 50;
            var original = new List<CoreCandleData>();
            var poisoned = new List<CoreCandleData>();
            for (int i = 0; i < count; i++)
            {
                decimal close = 100m + (i * 5 % 11);
                original.Add(new CoreCandleData(new DateTime(2026, 1, 5).AddDays(i), close, close + 3m, close - 2m, close, 1000));
                poisoned.Add(i < poisonStart
                    ? original[i]
                    : new CoreCandleData(original[i].Timestamp, 900m, 2000m, 5m, 1500m, 1000));
            }

            var a = new CorePivotPointsAdvancedIndicator { PivotType = PivotPointType.Camarilla }.Calculate(original);
            var b = new CorePivotPointsAdvancedIndicator { PivotType = PivotPointType.Camarilla }.Calculate(poisoned);

            foreach (var name in new[] { "R4", "S4" })
            {
                var sa = a.GetSeries(name);
                var sb = b.GetSeries(name);
                Assert.Equal(count, sa.Count);
                Assert.Null(sa[0]);
                for (int i = 1; i < count; i++) Assert.NotNull(sa[i]);
                // Bar i reads candle i-1 only: bars 0..poisonStart read untouched candles, bar poisonStart + 1 is the first to read a poisoned one.
                for (int i = 0; i <= poisonStart; i++) Assert.Equal(sa[i], sb[i]);
                Assert.NotEqual(sa[poisonStart + 1], sb[poisonStart + 1]);
            }
        }

        [Fact]
        public void FirstBar_AllSeriesNull_AndEverySeriesHasTheCandleCount()
        {
            var indicator = new CorePivotPointsAdvancedIndicator();
            var result = indicator.Calculate(PreviousBarThenOne());
            foreach (var name in new[] { "Main", "R1", "R2", "R3", "R4", "S1", "S2", "S3", "S4" })
            {
                Assert.Equal(2, result.GetSeries(name).Count);
                Assert.Null(result.GetSeries(name)[0]);
            }
        }

        [Fact]
        public void SingleBar_AllSeriesNull()
        {
            var indicator = new CorePivotPointsAdvancedIndicator();
            var result = indicator.Calculate(new List<CoreCandleData> { PreviousBarThenOne()[0] });
            foreach (var name in new[] { "Main", "R1", "R2", "R3", "R4", "S1", "S2", "S3", "S4" })
            {
                var series = result.GetSeries(name);
                Assert.Single(series);
                Assert.Null(series[0]);
            }
        }

        [Theory]
        [InlineData(PivotPointType.Standard)]
        [InlineData(PivotPointType.Fibonacci)]
        [InlineData(PivotPointType.Woodie)]
        [InlineData(PivotPointType.Camarilla)]
        public void ZeroRange_EveryLevelEqualsThePivotOrTheClose(PivotPointType type)
        {
            var candles = new List<CoreCandleData>
            {
                new(new DateTime(2026, 1, 5), 100m, 100m, 100m, 100m, 1000),
                new(new DateTime(2026, 1, 6), 100m, 100m, 100m, 100m, 1000),
            };
            var result = new CorePivotPointsAdvancedIndicator { PivotType = type }.Calculate(candles);
            foreach (var level in Levels(result).Where(v => v.HasValue))
            {
                Assert.Equal(100m, level!.Value);
            }
        }

        [Fact]
        public void ParameterObject_SelectsThePivotType()
        {
            var indicator = new CorePivotPointsAdvancedIndicator();
            indicator.Configure(new StockAnalyzer.Core.Models.Parameters.CorePivotPointsAdvancedParameter { PivotType = PivotPointType.Camarilla });
            Assert.Equal(PivotPointType.Camarilla, indicator.PivotType);
        }

        [Fact]
        public void Calculate_WithSampleData_ReturnsValues()
        {
            var indicator = new CorePivotPointsAdvancedIndicator();
            var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
            indicator.Calculate(candles);
            Assert.Equal(candles.Count, indicator.Values.Count);
        }
    }
}
