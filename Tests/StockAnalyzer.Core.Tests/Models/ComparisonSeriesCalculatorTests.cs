using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Tests.Models
{
    public class ComparisonSeriesCalculatorTests
    {
        private const string Primary = "BASE";
        private const string Other = "OTHER";
        private const double ZTolerance = 1e-9;

        private static readonly DateTime Origin = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static ComparisonAlignedData Build(params (string Symbol, decimal?[] Closes)[] rows)
        {
            int length = rows[0].Closes.Length;
            var timestamps = new DateTime[length];
            for (int i = 0; i < length; i++) timestamps[i] = Origin.AddDays(i);

            var series = new Dictionary<string, CandleData?[]>();
            foreach (var (symbol, closes) in rows)
            {
                var candles = new CandleData?[length];
                for (int i = 0; i < length; i++)
                {
                    if (closes[i] is { } close) candles[i] = new CandleData(timestamps[i], close, close, close, close, 0);
                }

                series[symbol] = candles;
            }

            return new ComparisonAlignedData(rows[0].Symbol, timestamps, series, new List<string>());
        }

        private static decimal?[] Values(ComparisonSeriesResult result, string symbol) => result.Series[symbol].Values;

        [Fact]
        public void Performance_RebasesEverySymbolToItsCloseAtTheFirstWindowBar()
        {
            var data = Build((Primary, new decimal?[] { 100m, 110m, 121m }), (Other, new decimal?[] { 50m, 45m, 50m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 3, ComparisonMode.Performance, 20);

            Assert.Equal(new decimal?[] { 0m, 10m, 21m }, Values(result, Primary));
            Assert.Equal(new decimal?[] { 0m, -10m, 0m }, Values(result, Other));
            Assert.Equal(0, result.Series[Primary].BaseIndex);
            Assert.Equal(100m, result.Series[Primary].BasePrice);
        }

        [Fact]
        public void Performance_BaseIsTheCloseAtTheWindowStartNotAtTheFirstBarOfTheTimeline()
        {
            var data = Build((Primary, new decimal?[] { 100m, 200m, 220m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 1, 2, ComparisonMode.Performance, 20);

            Assert.Equal(new decimal?[] { 0m, 10m }, Values(result, Primary));
            Assert.Equal(1, result.Series[Primary].BaseIndex);
        }

        [Fact]
        public void Performance_MissingBaseBarUsesTheNearestEarlierClose()
        {
            var data = Build((Primary, new decimal?[] { 100m, 100m, 100m, 100m }), (Other, new decimal?[] { 40m, null, 44m, 48m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 1, 3, ComparisonMode.Performance, 20);

            Assert.Equal(0, result.Series[Other].BaseIndex);
            Assert.Equal(40m, result.Series[Other].BasePrice);
            Assert.Equal(new decimal?[] { null, 10m, 20m }, Values(result, Other));
        }

        [Fact]
        public void Performance_WhenNothingEarlierExistsTheNearestLaterCloseIsTheBaseAndEarlierValuesStayUndefined()
        {
            var data = Build((Primary, new decimal?[] { 100m, 100m, 100m, 100m }), (Other, new decimal?[] { null, null, 50m, 55m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 4, ComparisonMode.Performance, 20);

            Assert.Equal(2, result.Series[Other].BaseIndex);
            Assert.Equal(new decimal?[] { null, null, 0m, 10m }, Values(result, Other));
        }

        [Fact]
        public void Performance_ZeroBaseGivesNoValuesButKeepsTheBase()
        {
            var data = Build((Primary, new decimal?[] { 0m, 5m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 2, ComparisonMode.Performance, 20);

            Assert.Equal(new decimal?[] { null, null }, Values(result, Primary));
            Assert.Equal(0m, result.Series[Primary].BasePrice);
        }

        [Fact]
        public void Performance_SeriesWithoutAnyCloseHasNoBase()
        {
            var data = Build((Primary, new decimal?[] { 100m, 100m }), (Other, new decimal?[] { null, null }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 2, ComparisonMode.Performance, 20);

            Assert.Null(result.Series[Other].BaseIndex);
            Assert.Null(result.Series[Other].BasePrice);
            Assert.Equal(new decimal?[] { null, null }, Values(result, Other));
        }

        [Fact]
        public void Ratio_IsTheCloseOverThePrimaryCloseAndThePrimaryRowIsOne()
        {
            var data = Build((Primary, new decimal?[] { 100m, 200m, 0m, null }), (Other, new decimal?[] { 50m, 50m, 50m, 50m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 4, ComparisonMode.Ratio, 20);

            Assert.Equal(new decimal?[] { 0.5m, 0.25m, null, null }, Values(result, Other));
            Assert.Equal(new decimal?[] { 1m, 1m, null, null }, Values(result, Primary));
            Assert.Null(result.Series[Other].BaseIndex);
        }

        [Fact]
        public void Spread_IsTheCloseMinusThePrimaryCloseAndThePrimaryRowIsZero()
        {
            var data = Build((Primary, new decimal?[] { 100m, 105m, null }), (Other, new decimal?[] { 90m, 92m, 95m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 3, ComparisonMode.Spread, 20);

            Assert.Equal(new decimal?[] { -10m, -13m, null }, Values(result, Other));
            Assert.Equal(new decimal?[] { 0m, 0m, null }, Values(result, Primary));
        }

        [Fact]
        public void ZScore_UsesThePopulationDeviationOverTheWindowAndIsUndefinedDuringWarmUp()
        {
            var data = Build((Primary, new decimal?[] { 1m, 2m, 3m, 4m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 4, ComparisonMode.ZScore, 3);

            decimal?[] values = Values(result, Primary);
            Assert.Null(values[0]);
            Assert.Null(values[1]);
            // window {1,2,3}: mean 2, population sd sqrt(2/3) -> (3-2)/sd = sqrt(1.5)
            Assert.Equal(Math.Sqrt(1.5), (double)values[2]!.Value, ZTolerance);
            // window {2,3,4}: same shape
            Assert.Equal(Math.Sqrt(1.5), (double)values[3]!.Value, ZTolerance);
        }

        [Fact]
        public void ZScore_WarmUpIsMeasuredOnTheTimelineNotOnTheWindow()
        {
            var data = Build((Primary, new decimal?[] { 1m, 2m, 3m, 4m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 2, 2, ComparisonMode.ZScore, 3);

            Assert.All(Values(result, Primary), v => Assert.NotNull(v));
        }

        [Fact]
        public void ZScore_UndefinedForAFlatWindowAndForAWindowWithAMissingClose()
        {
            var data = Build((Primary, new decimal?[] { 5m, 5m, 5m, 5m }), (Other, new decimal?[] { 1m, null, 3m, 4m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 4, ComparisonMode.ZScore, 3);

            Assert.All(Values(result, Primary), v => Assert.Null(v));
            Assert.Null(Values(result, Other)[2]);
            Assert.Null(Values(result, Other)[3]);
        }

        [Theory]
        [InlineData(0.9, false)]
        [InlineData(1.1, true)]
        public void ZScore_FlatnessGuardSwitchesAtTheSharedEpsilon(double deviationOverEpsilon, bool isDefined)
        {
            // Window [a, a+d, a] with period 3 has the population deviation d * sqrt(2) / 3.
            double d = deviationOverEpsilon * ComparisonSeriesDefaults.FlatWindowDeviationEpsilon * 3.0 / Math.Sqrt(2.0);
            var data = Build((Primary, new decimal?[] { 100m, 100m + (decimal)d, 100m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 3, ComparisonMode.ZScore, 3);

            Assert.Equal(isDefined, Values(result, Primary)[2].HasValue);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void ZScore_PeriodBelowTwoNeverProducesAValue(int period)
        {
            var data = Build((Primary, new decimal?[] { 1m, 2m, 3m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 3, ComparisonMode.ZScore, period);

            Assert.All(Values(result, Primary), v => Assert.Null(v));
        }

        [Fact]
        public void WindowIsClampedToTheTimeline()
        {
            var data = Build((Primary, new decimal?[] { 100m, 110m, 120m }));

            Assert.Equal(new decimal?[] { 0m, 10m, 20m }, Values(ComparisonSeriesCalculator.Calculate(data, -5, 99, ComparisonMode.Performance, 20), Primary));
            Assert.Empty(Values(ComparisonSeriesCalculator.Calculate(data, 3, 5, ComparisonMode.Performance, 20), Primary));
            Assert.Empty(Values(ComparisonSeriesCalculator.Calculate(data, 0, 0, ComparisonMode.Performance, 20), Primary));
        }

        [Fact]
        public void SingleBarTimeline_PerformanceIsZeroAndZScoreIsUndefined()
        {
            var data = Build((Primary, new decimal?[] { 100m }));

            Assert.Equal(new decimal?[] { 0m }, Values(ComparisonSeriesCalculator.Calculate(data, 0, 1, ComparisonMode.Performance, 20), Primary));
            Assert.Equal(new decimal?[] { null }, Values(ComparisonSeriesCalculator.Calculate(data, 0, 1, ComparisonMode.ZScore, 20), Primary));
        }

        [Fact]
        public void ResultIsKeyedLikeTheAlignedSeries()
        {
            var data = Build((Primary, new decimal?[] { 1m, 2m }), (Other, new decimal?[] { 3m, 4m }));

            var result = ComparisonSeriesCalculator.Calculate(data, 0, 2, ComparisonMode.Spread, 20);

            Assert.Equal(data.Series.Keys.OrderBy(k => k), result.Series.Keys.OrderBy(k => k));
        }

        [Fact]
        public void Calculate_RejectsNullData()
        {
            Assert.Throws<ArgumentNullException>(() => ComparisonSeriesCalculator.Calculate(null!, 0, 1, ComparisonMode.Performance, 20));
        }

        [Fact]
        public void DefaultZScorePeriodIsThePersistedChartDefaultAndTheSettingsUseIt()
        {
            // 20 is what existing users' settings files and the documentation mean by "default"; changing it needs a decision, not a refactor.
            Assert.Equal(20, ComparisonSeriesDefaults.DefaultZScorePeriod);
            Assert.Equal(ComparisonSeriesDefaults.DefaultZScorePeriod, new GlobalChartSettings().ComparisonZScorePeriod);
            Assert.InRange(ComparisonSeriesDefaults.DefaultZScorePeriod, ComparisonSeriesDefaults.MinZScorePeriod, ComparisonSeriesDefaults.MaxZScorePeriod);
        }

        // ---- Parity with the chart's own price-range calculator (the GUI path that is already in Core) ----

        public static IEnumerable<object[]> Modes() => Enum.GetValues<ComparisonMode>().Select(m => new object[] { m });

        [Theory]
        [MemberData(nameof(Modes))]
        public void ValueRangeMatchesComparisonPriceRangeCalculatorForEveryBar(ComparisonMode mode)
        {
            const int length = 40;
            var primary = new decimal?[length];
            var other = new decimal?[length];
            var late = new decimal?[length];
            for (int i = 0; i < length; i++)
            {
                primary[i] = 100m + (i * 3m) + (i % 5 * 2m);
                other[i] = 80m + (i % 7 * 11m) - (i * 0.5m);
                late[i] = i < 6 ? null : 200m - (i % 4 * 9m) + (i * 1.5m);
            }

            other[15] = null;
            var data = Build((Primary, primary), (Other, other), ("LATE", late));
            const int window = 25;
            int start = length - window;

            var values = ComparisonSeriesCalculator.Calculate(data, start, window, mode, 5);
            var range = new ComparisonPriceRangeCalculator(data, start, mode, 5);
            bool primaryIsStatic = mode is ComparisonMode.Ratio or ComparisonMode.Spread;

            for (int k = 0; k < window; k++)
            {
                var candidates = new List<decimal>();
                foreach (var (symbol, series) in values.Series)
                {
                    if (primaryIsStatic && symbol == Primary) continue;
                    if (series.Values[k] is { } v) candidates.Add(v);
                }

                var (high, low) = range.GetPriceRange(new CoreCandleData(data.Timestamps[start + k], 0, 0, 0, 0, 0));
                if (candidates.Count == 0) continue;

                decimal expectedMax = candidates.Max();
                decimal expectedMin = candidates.Min();
                // The chart widens a too-narrow range around its midpoint; otherwise the range is exactly the value range.
                Assert.Equal((expectedMax + expectedMin) / 2m, (high + low) / 2m, 12);
                Assert.True(high - low >= expectedMax - expectedMin);
                Assert.True(high >= expectedMax && low <= expectedMin);
            }
        }

        // The chart's range calculator and Calculate share one base search: exact bar, else nearest earlier, else nearest later.
        [Theory]
        [InlineData(-3)]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(15)]
        [InlineData(38)]
        [InlineData(39)]
        public void PerformanceBaseSearchMatchesComparisonPriceRangeCalculatorForEveryStart(int startIndex)
        {
            const int length = 40;
            var primary = new decimal?[length];
            var gapAtStart = new decimal?[length];
            var late = new decimal?[length];
            for (int i = 0; i < length; i++)
            {
                primary[i] = 100m + (i * 3m) + (i % 5 * 2m);
                gapAtStart[i] = 80m + (i % 7 * 11m) - (i * 0.5m);
                late[i] = i < 6 ? null : 200m - (i % 4 * 9m) + (i * 1.5m);
            }

            gapAtStart[15] = null;
            gapAtStart[38] = null;
            var data = Build((Primary, primary), (Other, gapAtStart), ("LATE", late));
            int firstBar = Math.Clamp(startIndex, 0, length - 1);

            var values = ComparisonSeriesCalculator.Calculate(data, startIndex, length, ComparisonMode.Performance, 5);
            var range = new ComparisonPriceRangeCalculator(data, startIndex, ComparisonMode.Performance, 5);

            for (int bar = firstBar; bar < length; bar++)
            {
                var candidates = values.Series.Values.Select(s => s.Values[bar - Math.Max(0, startIndex)]).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                var (high, low) = range.GetPriceRange(new CoreCandleData(data.Timestamps[bar], 0, 0, 0, 0, 0));
                if (candidates.Count == 0) continue;

                Assert.Equal((candidates.Max() + candidates.Min()) / 2m, (high + low) / 2m, 12);
                Assert.True(high >= candidates.Max() && low <= candidates.Min());
            }
        }

        [Fact]
        public void Performance_ValueRangeEqualsTheChartRangeWhenItIsWideEnough()
        {
            var data = Build((Primary, new decimal?[] { 100m, 130m, 90m }), (Other, new decimal?[] { 100m, 80m, 140m }));

            var values = ComparisonSeriesCalculator.Calculate(data, 0, 3, ComparisonMode.Performance, 20);
            var range = new ComparisonPriceRangeCalculator(data, 0, ComparisonMode.Performance);

            for (int k = 0; k < 3; k++)
            {
                var all = values.Series.Values.Select(s => s.Values[k]!.Value).ToList();
                var (high, low) = range.GetPriceRange(new CoreCandleData(data.Timestamps[k], 0, 0, 0, 0, 0));
                if (all.Max() - all.Min() >= 2m)
                {
                    Assert.Equal(all.Max(), high);
                    Assert.Equal(all.Min(), low);
                }
            }
        }
    }
}
