using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Candles;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Benchmarks
{
    /// <summary>
    /// Measurement-only records (time and allocated bytes) of the chart-pattern flag indicators (plan ChartPatterns56, probe P-4, decision D-9 A "measure first"). There is no pass/fail
    /// threshold on the numbers: they go to the test output and to the step log, and a later run decides from them whether a per-calculation memo is justified.
    /// </summary>
    public class PatternFlagPerformanceMeasurementTests
    {
        private const int MeasuredRuns = 7;
        private const int WorstCaseRuns = 20;
        private const int SeriesLength = 2_000;
        private const int LargeSeriesLength = 10_000;
        private const int Seed = 42;
        private const double StepRange = 10.0;
        private const decimal StartPrice = 1000m;
        private const decimal HalfRange = 5m;
        private const int WorstCaseAtrPeriod = 200;
        private const int WorstCasePriceZoneLookback = 100;
        private const int WorstCaseMaxPivotInterval = 200;
        private const int WorstCaseMaxLastPivotAge = 200;
        private const decimal SawtoothSwingFraction = 0.04m;
        private const decimal TightZigZagPercent = 0.5m;
        private readonly ITestOutputHelper _output;

        public PatternFlagPerformanceMeasurementTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static CoreCandleData[] CreateCandles(int count)
        {
            var list = new CoreCandleData[count];
            var date = new DateTime(2000, 1, 3);
            var rng = new Random(Seed);
            decimal price = StartPrice;
            for (int i = 0; i < count; i++)
            {
                decimal open = price;
                price += (decimal)(rng.NextDouble() - 0.5) * (decimal)StepRange;
                list[i] = new CoreCandleData(date.AddDays(i), open, Math.Max(open, price) + HalfRange, Math.Min(open, price) - HalfRange, price, 1000 + i);
            }

            return list;
        }

        private static (double MedianMs, long MedianBytes) Measure(Action action)
        {
            action(); // JIT warm-up
            var times = new List<double>();
            var bytes = new List<long>();
            for (int run = 0; run < MeasuredRuns; run++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                action();
                sw.Stop();
                bytes.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            bytes.Sort();
            return (times[MeasuredRuns / 2], bytes[MeasuredRuns / 2]);
        }

        [Fact]
        public void Measure_OneCandlestickFlag_AndAllOfThem()
        {
            CoreCandleData[] candles = CreateCandles(SeriesLength);
            var factory = new IndicatorFactory();
            IndicatorType[] types = PatternFlagIndicatorCatalog.Rows.Select(r => r.Type).ToArray();
            Assert.NotEmpty(types);

            IndicatorType single = types[0];
            var (singleMs, singleBytes) = Measure(() =>
            {
                var result = factory.Create(single)!.Calculate(candles);
                Assert.True(result.IsSuccessful);
            });
            _output.WriteLine($"MEASURE one flag ({single}), {SeriesLength} bars: median {singleMs:F2} ms, {singleBytes / 1024.0:F1} KiB allocated");

            var (allMs, allBytes) = Measure(() =>
            {
                foreach (IndicatorType type in types)
                {
                    var result = factory.Create(type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                }
            });
            _output.WriteLine($"MEASURE all {types.Length} flags, {SeriesLength} bars: median {allMs:F2} ms ({allMs / types.Length:F2} ms per flag), {allBytes / 1024.0:F1} KiB allocated");
        }

        [Fact]
        public void Measure_HarmonicFlags_PV4()
        {
            var factory = new IndicatorFactory();
            IndicatorType[] harmonics = { IndicatorType.HarmonicGartley, IndicatorType.HarmonicButterfly, IndicatorType.HarmonicBat };

            foreach (int length in new[] { 250, SeriesLength })
            {
                CoreCandleData[] candles = CreateCandles(length);
                foreach (IndicatorType type in harmonics)
                {
                    var (ms, bytes) = Measure(() =>
                    {
                        var result = factory.Create(type)!.Calculate(candles);
                        Assert.True(result.IsSuccessful);
                    });
                    _output.WriteLine($"PV-4 {type}, {length} bars: median {ms:F2} ms, {bytes / 1024.0:F1} KiB allocated");
                }
            }
        }

        [Fact]
        public void Measure_CandlesPart1_PV2()
        {
            var factory = new IndicatorFactory();
            CoreCandleData[] candles = CreateCandles(SeriesLength);
            IndicatorType[] representative =
            {
                IndicatorType.CandleHammer,
                IndicatorType.CandleBullishEngulfing,
                IndicatorType.CandleBullishKicking,
            };

            foreach (IndicatorType type in representative)
            {
                var (ms, bytes) = Measure(() =>
                {
                    var result = factory.Create(type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                });
                _output.WriteLine($"PV-2 {type}, {SeriesLength} bars: median {ms:F2} ms, {bytes / 1024.0:F1} KiB allocated");
            }

            IndicatorType[] part1All = PatternFlagIndicatorCatalog.Rows
                .Where(r => r.Category == CoreIndicatorCategory.CandlestickPatterns && r.Model == PatternFlagEvaluationModel.FullSeries)
                .Select(r => r.Type)
                .ToArray();

            var (allMs, allBytes) = Measure(() =>
            {
                foreach (IndicatorType type in part1All)
                {
                    var result = factory.Create(type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                }
            });
            _output.WriteLine($"PV-2 all {part1All.Length} Part1 candle flags, {SeriesLength} bars: median {allMs:F2} ms ({allMs / part1All.Length:F2} ms per flag), {allBytes / 1024.0:F1} KiB allocated");
        }

        [Fact]
        public void Measure_CandlesPart2_PV2()
        {
            var factory = new IndicatorFactory();
            CoreCandleData[] candles = CreateCandles(SeriesLength);
            IndicatorType[] representative =
            {
                IndicatorType.CandleMorningStar,
                IndicatorType.CandleThreeWhiteSoldiers,
                IndicatorType.CandleBullishAbandonedBaby,
            };

            foreach (IndicatorType type in representative)
            {
                var (ms, bytes) = Measure(() =>
                {
                    var result = factory.Create(type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                });
                _output.WriteLine($"PV-2 Part2 {type}, {SeriesLength} bars: median {ms:F2} ms, {bytes / 1024.0:F1} KiB allocated");
            }

            IndicatorType[] part2All = PatternFlagIndicatorCatalog.Rows
                .Where(r => r.Category == CoreIndicatorCategory.CandlestickPatterns
                            && r.Model == PatternFlagEvaluationModel.FullSeries
                            && CandlePatternFlagTable.Get(r.Type).Length == 3)
                .Select(r => r.Type)
                .ToArray();
            Assert.Equal(16, part2All.Length);

            var (allMs, allBytes) = Measure(() =>
            {
                foreach (IndicatorType type in part2All)
                {
                    var result = factory.Create(type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                }
            });
            _output.WriteLine($"PV-2 all {part2All.Length} Part2 candle flags, {SeriesLength} bars: median {allMs:F2} ms ({allMs / part2All.Length:F2} ms per flag), {allBytes / 1024.0:F1} KiB allocated");
        }

        [Fact]
        public void Measure_CandlesPart3AndHhHl_PV2()
        {
            var factory = new IndicatorFactory();
            CoreCandleData[] candles = CreateCandles(SeriesLength);
            IndicatorType[] representative =
            {
                IndicatorType.CandleRisingThreeMethods,
                IndicatorType.CandleBullishBreakaway,
                IndicatorType.MarketStructureHhHl,
            };

            foreach (IndicatorType type in representative)
            {
                var (ms, bytes) = Measure(() =>
                {
                    var result = factory.Create(type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                });
                _output.WriteLine($"PV-2 Part3 {type}, {SeriesLength} bars: median {ms:F2} ms, {bytes / 1024.0:F1} KiB allocated");
            }

            IndicatorType[] part3All = PatternFlagIndicatorCatalog.Rows
                .Where(r => r.Category == CoreIndicatorCategory.CandlestickPatterns && CandlePatternFlagTable.Get(r.Type).Length >= 4)
                .Select(r => r.Type)
                .ToArray();
            Assert.Equal(9, part3All.Length);

            var (allMs, allBytes) = Measure(() =>
            {
                foreach (IndicatorType type in part3All)
                {
                    var result = factory.Create(type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                }
            });
            _output.WriteLine($"PV-2 all {part3All.Length} Part3 (4/5-bar) candle flags, {SeriesLength} bars: median {allMs:F2} ms ({allMs / part3All.Length:F2} ms per flag), {allBytes / 1024.0:F1} KiB allocated");
        }

        [Fact]
        public void Measure_AllFamiliesD9Budget_V2_13()
        {
            var factory = new IndicatorFactory();
            CoreCandleData[] candles = CreateCandles(SeriesLength);
            PatternFlagSpec[] nonCandles = PatternFlagIndicatorCatalog.Rows
                .Where(r => r.Category != CoreIndicatorCategory.CandlestickPatterns)
                .ToArray();
            PatternFlagSpec[] candlesAll = PatternFlagIndicatorCatalog.Rows
                .Where(r => r.Category == CoreIndicatorCategory.CandlestickPatterns)
                .ToArray();

            Assert.Equal(14, nonCandles.Length);
            Assert.Equal(43, candlesAll.Length);

            var nonCandleResults = new List<(IndicatorType Type, double Ms, double KiB)>(nonCandles.Length);
            foreach (PatternFlagSpec spec in nonCandles)
            {
                var (ms, bytes) = Measure(() =>
                {
                    var result = factory.Create(spec.Type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                });
                double kib = bytes / 1024.0;
                nonCandleResults.Add((spec.Type, ms, kib));
                _output.WriteLine($"D9 non-candle {spec.Type}, {SeriesLength} bars: median {ms:F2} ms, {kib:F1} KiB allocated");
            }

            var candleResults = new List<(IndicatorType Type, double Ms, double KiB)>(candlesAll.Length);
            foreach (PatternFlagSpec spec in candlesAll)
            {
                var (ms, bytes) = Measure(() =>
                {
                    var result = factory.Create(spec.Type)!.Calculate(candles);
                    Assert.True(result.IsSuccessful);
                });
                candleResults.Add((spec.Type, ms, bytes / 1024.0));
            }

            var slowestNonCandle = nonCandleResults.OrderByDescending(x => x.Ms).First();
            var slowestCandles = candleResults.OrderByDescending(x => x.Ms).Take(5).ToArray();
            double candleMedian = candleResults.Select(x => x.Ms).OrderBy(x => x).ElementAt(candleResults.Count / 2);
            double candleTotal = candleResults.Sum(x => x.Ms);

            _output.WriteLine($"D9 summary non-candle max: {slowestNonCandle.Type} = {slowestNonCandle.Ms:F2} ms ({slowestNonCandle.KiB:F1} KiB) vs budget 250 ms");
            _output.WriteLine($"D9 summary candle (n={candleResults.Count}): sum {candleTotal:F2} ms, median {candleMedian:F2} ms, max {slowestCandles[0].Type} = {slowestCandles[0].Ms:F2} ms ({slowestCandles[0].KiB:F1} KiB) vs budget 25 ms");
            foreach (var c in slowestCandles)
            {
                _output.WriteLine($"D9 top candle {c.Type}: median {c.Ms:F2} ms, {c.KiB:F1} KiB allocated");
            }
        }

        [Fact]
        public void Measure_Screener500BarCost_V2_13()
        {
            const int ScreenerBars = 500;
            CoreCandleData[] candles = CreateCandles(ScreenerBars);
            IndicatorType[] representatives =
            {
                IndicatorType.CandleHammer,
                IndicatorType.CandleBullishEngulfing,
                IndicatorType.PatternHeadAndShoulders,
                IndicatorType.PatternDoubleTopBottom,
                IndicatorType.PatternFlag,
                IndicatorType.MarketStructureBos,
                IndicatorType.MarketStructureHhHl,
                IndicatorType.MarketStructureLlLh,
                IndicatorType.HarmonicGartley,
                IndicatorType.GranvilleBuySignal,
                IndicatorType.ElliottWaveImpulse,
            };

            foreach (IndicatorType type in representatives)
            {
                var side = new ScreenerIndicatorSideConfig
                {
                    IndicatorType = type,
                    CategoryType = ScreenerItemCategoryType.Indicator,
                    TimeFrame = TimeFrame.D1,
                };
                var (ms, bytes) = Measure(() =>
                {
                    _ = ScreenerValueExtractor.Default.ExtractValueNullable(side, candles);
                });
                _output.WriteLine($"SCREENER500 {type}, {ScreenerBars} bars: median {ms:F3} ms, {bytes / 1024.0:F1} KiB allocated");
            }
        }

        private static CoreCandleData[] CreateHighDensitySawtoothCandles(int count)
        {
            var list = new CoreCandleData[count];
            var date = new DateTime(2000, 1, 3);
            for (int i = 0; i < count; i++)
            {
                bool up = (i % 2) == 0;
                decimal prev = up ? StartPrice * (1m - SawtoothSwingFraction) : StartPrice * (1m + SawtoothSwingFraction);
                decimal close = up ? StartPrice * (1m + SawtoothSwingFraction) : StartPrice * (1m - SawtoothSwingFraction);
                list[i] = new CoreCandleData(
                    date.AddDays(i),
                    prev,
                    Math.Max(prev, close) + HalfRange,
                    Math.Min(prev, close) - HalfRange,
                    close,
                    1_000 + i);
            }

            return list;
        }

        private static (double MedianMs, double P95Ms, long MedianBytes, int Gen0, int Gen1, int Gen2) MeasureDetailed(Action action, int runs = WorstCaseRuns)
        {
            action(); // JIT warm-up
            var times = new List<double>(runs);
            var bytes = new List<long>(runs);
            int totalGen0 = 0;
            int totalGen1 = 0;
            int totalGen2 = 0;
            for (int run = 0; run < runs; run++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                int g0Before = GC.CollectionCount(0);
                int g1Before = GC.CollectionCount(1);
                int g2Before = GC.CollectionCount(2);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                action();
                sw.Stop();
                bytes.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                times.Add(sw.Elapsed.TotalMilliseconds);
                totalGen0 += GC.CollectionCount(0) - g0Before;
                totalGen1 += GC.CollectionCount(1) - g1Before;
                totalGen2 += GC.CollectionCount(2) - g2Before;
            }

            times.Sort();
            bytes.Sort();
            int p95Idx = Math.Clamp((int)Math.Ceiling(0.95 * runs) - 1, 0, runs - 1);
            return (times[runs / 2], times[p95Idx], bytes[runs / 2], totalGen0, totalGen1, totalGen2);
        }

        private static CoreIndicatorParameterBase CreateWorstCaseParameter(IndicatorType type)
        {
            if (PatternFlagIndicatorCatalog.Get(type).Category == CoreIndicatorCategory.CandlestickPatterns)
            {
                var cp = StockAnalyzer.Core.Models.Parameters.CoreCandlePatternParameter.CreateFor(type);
                cp.AtrPeriod = WorstCaseAtrPeriod;
                cp.PriceZoneLookbackBars = WorstCasePriceZoneLookback;
                return cp;
            }

            return type switch
            {
                IndicatorType.MarketStructureHhHl or IndicatorType.MarketStructureLlLh => new StockAnalyzer.Core.Models.Parameters.CoreStructureTrendParameter
                {
                    ZigZagThresholdPercent = TightZigZagPercent,
                    MinSwingAtrMultiplier = 0m,
                    MaxPivotIntervalBars = WorstCaseMaxPivotInterval,
                    MaxLastPivotAgeBars = WorstCaseMaxLastPivotAge,
                },
                IndicatorType.MarketStructureBos or IndicatorType.MarketStructureChoch => new StockAnalyzer.Core.Models.Parameters.CoreStructureShiftParameter
                {
                    ZigZagThresholdPercent = TightZigZagPercent,
                    MinSwingAtrMultiplier = 0m,
                },
                IndicatorType.PatternFlag => new StockAnalyzer.Core.Models.Parameters.CoreFlagPatternParameter
                {
                    MaxPoleBars = StockAnalyzer.Core.Models.Parameters.PatternFlagParameterLimits.FlagMaxPoleBarsMax,
                    MaxFlagBars = StockAnalyzer.Core.Models.Parameters.PatternFlagParameterLimits.FlagMaxFlagBarsMax,
                    MinPoleMovePercent = (decimal)StockAnalyzer.Core.Models.Parameters.PatternFlagParameterLimits.FlagPoleMovePercentMin,
                    MinPoleAtrMultiplier = (decimal)StockAnalyzer.Core.Models.Parameters.PatternFlagParameterLimits.FlagPoleAtrMultiplierMin,
                },
                IndicatorType.PatternHeadAndShoulders => new StockAnalyzer.Core.Models.Parameters.CoreHeadAndShouldersParameter
                {
                    ZigZagThresholdPercent = TightZigZagPercent,
                },
                IndicatorType.PatternDoubleTopBottom => new StockAnalyzer.Core.Models.Parameters.CoreDoubleTopBottomParameter
                {
                    ZigZagThresholdPercent = TightZigZagPercent,
                },
                IndicatorType.HarmonicGartley or IndicatorType.HarmonicButterfly or IndicatorType.HarmonicBat => new StockAnalyzer.Core.Models.Parameters.CoreHarmonicPatternParameter
                {
                    MinLegSwingPercent = TightZigZagPercent,
                    MinLegAtrMultiplier = 0m,
                    MaxSpanBars = WorstCaseMaxPivotInterval,
                },
                _ => PatternFlagIndicatorCatalog.Get(type).CreateDefaultParameter(),
            };
        }

        [Fact]
        public void Measure_WorstCaseAndScaling_Task3A()
        {
            var factory = new IndicatorFactory();
            IndicatorType[] worstCaseTypes =
            {
                IndicatorType.CandleBullishEngulfing,
                IndicatorType.CandleThreeWhiteSoldiers,
                IndicatorType.CandleRisingThreeMethods,
                IndicatorType.CandleBullishBreakaway,
                IndicatorType.PatternFlag,
                IndicatorType.PatternHeadAndShoulders,
                IndicatorType.PatternDoubleTopBottom,
                IndicatorType.MarketStructureBos,
                IndicatorType.MarketStructureChoch,
                IndicatorType.MarketStructureHhHl,
                IndicatorType.MarketStructureLlLh,
                IndicatorType.HarmonicGartley,
                IndicatorType.GranvilleBuySignal,
                IndicatorType.ElliottWaveImpulse,
            };

            foreach (int length in new[] { SeriesLength, LargeSeriesLength })
            {
                CoreCandleData[] sawtooth = CreateHighDensitySawtoothCandles(length);
                foreach (IndicatorType type in worstCaseTypes)
                {
                    CoreIndicatorParameterBase parameter = CreateWorstCaseParameter(type);
                    var (medMs, p95Ms, kib, g0, g1, g2) = MeasureDetailed(() =>
                    {
                        var result = factory.Create(type, parameter)!.Calculate(sawtooth);
                        Assert.True(result.IsSuccessful, result.ErrorMessage);
                    });
                    _output.WriteLine($"TASK3A sawtooth N={length} {type}: median {medMs:F2} ms, P95 {p95Ms:F2} ms, {kib / 1024.0:F1} KiB, GC({g0}/{g1}/{g2})");
                }
            }
        }
    }
}
