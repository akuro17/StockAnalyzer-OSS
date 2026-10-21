#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Opt-in measurements and stop gates that precede the Week / Month time frames of the Scatter metrics (plan "ScatterTimeframes", T0, M-1..M-6).
/// Runs only when <see cref="MeasureVariable"/> is set (otherwise every fact is reported as skipped), so the normal suite never pays for it.
/// It reads the real Daily / Weekly / Monthly Parquet files of the repository's Data folder read-only and changes nothing.
/// M-2 (the Weekly / Monthly files equal the chart's aggregation of the daily bars) and M-3 (the fixed-range exception table holds on W1 / MN1)
/// are gates: they fail the fact when the condition does not hold. The others only report.
/// </summary>
public sealed class ScatterTimeFrameMeasurementTests : IClassFixture<ScatterMeasurementData>
{
    private const string MeasureVariable = "SA_SCATTER_MEASURE";

    // M-1: one discarded warm-up run, then the median of this many runs.
    private const int MeasuredRuns = 5;
    private const int AuditBarCount = 600;
    private const int AuditSeed = 20261021;
    private const int LongAuditBarCount = 12000;
    private const double TailPercentile = 0.90;
    private const int MaxListedItems = 40;
    private const int BytesPerMegabyte = 1024 * 1024;
    private const int LongSmaPeriod = 200;
    private const int FullHistoryCount = 0;
    // M-3b reference part (report only): a time budget per time frame, the newest daily bars only, and how many slow indicators to name.
    private const int ReferenceSecondsPerTimeFrame = 420;
    private const int ReferenceDailyTailBars = 2000;
    private const int ReferenceParallelismDivisor = 2;
    private const int SlowestListed = 5;

    private static readonly TimeFrame[] TimeFrames = { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 };
    private static readonly TimeFrame[] AggregatedFrames = { TimeFrame.W1, TimeFrame.MN1 };

    /// <summary>Too slow for a 127-ticker real-data run (about 68 s for 61 weekly series); the synthetic gate of M-3 still covers it on D1, W1 and MN1.</summary>
    private static readonly IndicatorType[] ReferenceExcludedIndicators = { IndicatorType.HiddenMarkovModel };
    private static readonly ScreenerCatalogProvider Catalog = new();
    private static readonly ScreenerValueExtractor Extractor = ScreenerValueExtractor.Default;

    private readonly ITestOutputHelper _output;
    private readonly ScatterMeasurementData _data;

    public ScatterTimeFrameMeasurementTests(ITestOutputHelper output, ScatterMeasurementData data)
    {
        _output = output;
        _data = data;
    }

    // ===== M-1: performance baseline of the daily evaluator =====

    [EnvironmentFact(MeasureVariable)]
    public async Task M1_PerformanceBaseline_OfTheDailyEvaluator_AndTheLoadPhases()
    {
        IReadOnlyList<string> symbols = _data.Symbols;
        var settings = new ScatterMetricTestSettings();
        var evaluator = new ScatterMetricEvaluator(_data.Service, Array.Empty<IMarketDataProvider>(), Extractor, settings, Catalog);
        _output.WriteLine($"M-1 conditions: {symbols.Count} tickers, ScatterMaxParallelism={settings.ScatterMaxParallelism}, real ParquetDataService, runs = 1 warm-up (discarded) + {MeasuredRuns} (median)");

        ScatterMetricDefinition rsi = DefaultIndicator(IndicatorType.RSI);
        ScatterMetricDefinition sma = DefaultIndicator(IndicatorType.SMA);
        ScatterMetricDefinition obv = DefaultIndicator(IndicatorType.OBV);
        int standardCount = RequiredCount(rsi);
        int fullCount = RequiredCount(obv);
        _output.WriteLine($"Required bars: RSI default = {RequiredCount(rsi)}, SMA default = {RequiredCount(sma)}, OBV = {fullCount} (0 = all history)");

        // Baseline B: the daily evaluator, whole call then its two phases. It was measured at T0 (235.3 ms, step log) before the Week / Month change; on today's evaluator it is the case "a" of M-1b.
        _output.WriteLine("-- whole evaluator (current code, D1/D1) --");
        await ReportAsync("B  D1/D1 RSI x SMA (all tickers, whole ComputeAsync)", async () =>
        {
            ScatterEvaluationResult result = await evaluator.ComputeAsync(symbols, rsi, sma, CancellationToken.None);
            Assert.Equal(symbols.Count, result.TotalCount);
        });
        await ReportAsync("B' D1/D1 RSI x OBV (full history)", async () =>
        {
            ScatterEvaluationResult result = await evaluator.ComputeAsync(symbols, rsi, obv, CancellationToken.None);
            Assert.Equal(symbols.Count, result.TotalCount);
        });

        _output.WriteLine("-- phase (a): load only, same parallelism --");
        foreach (TimeFrame frame in TimeFrames)
        {
            foreach (int count in new[] { standardCount, fullCount })
            {
                string label = $"(a) load {frame} count={(count == FullHistoryCount ? "all" : count.ToString(CultureInfo.InvariantCulture))}";
                await ReportAsync(label, () => LoadAllAsync(symbols, frame, count, settings.ScatterMaxParallelism));
            }
        }

        _output.WriteLine("-- phase (c): extraction only (candles already loaded; the X and Y reads of every ticker, sequential) --");
        foreach (TimeFrame frame in TimeFrames)
        {
            var loaded = new List<IReadOnlyList<CandleData>>();
            foreach (string symbol in symbols)
            {
                loaded.Add(await _data.Service.LoadCandlesAsync(symbol, frame, standardCount));
            }

            await ReportAsync($"(c) extract RSI x SMA on {frame} candles (count={standardCount}, read as TimeFrame=D1)", () =>
            {
                ScreenerIndicatorSideConfig x = ScatterMetricTestKit.DaySourceOf(rsi);
                ScreenerIndicatorSideConfig y = ScatterMetricTestKit.DaySourceOf(sma);
                foreach (IReadOnlyList<CandleData> candles in loaded)
                {
                    if (candles.Count == 0) continue;
                    _ = Extractor.ExtractValueNullable(x, candles, default(TickerMetadata));
                    _ = Extractor.ExtractValueNullable(y, candles, default(TickerMetadata));
                }

                return Task.CompletedTask;
            });
        }
    }

    // ===== M-1b: the evaluator per combination of time frames, relative to D1/D1 of the SAME run (plan S11', definition of 2026-10-22) =====

    // Plan S11': in normal operation every other combination is judged against D1/D1 measured in this very run, in time and in allocation.
    // No absolute figure is stored: a figure taken in another session drifts with the machine (the same code ran 241-346 ms within an hour).
    // The check "the Week / Month change did not slow D1/D1" is a before/after comparison, so it is a manual procedure (old evaluator swapped in,
    // alternating pairs in both orders, ratio of medians <= 1.10 and equal allocation); see the step log. It cannot be a test of the current code alone.
    private const double TimeFrameToleranceFactor = 3.0;

    /// <summary>Report only: prints the five medians and allocations, each ratio to D1/D1 and a verdict line against the provisional factor; the decision stays with the owner.</summary>
    [EnvironmentFact(MeasureVariable)]
    public async Task M1b_EvaluatorPerTimeFrameCombination_RelativeToDailyOfTheSameRun()
    {
        IReadOnlyList<string> symbols = _data.Symbols;
        var settings = new ScatterMetricTestSettings();
        var evaluator = new ScatterMetricEvaluator(_data.Service, Array.Empty<IMarketDataProvider>(), Extractor, settings, Catalog);
        _output.WriteLine($"M-1b conditions: {symbols.Count} tickers, ScatterMaxParallelism={settings.ScatterMaxParallelism}, real ParquetDataService, RSI x SMA (defaults), runs = 1 warm-up (discarded) + {MeasuredRuns} (median)");

        var cases = new (string Name, TimeFrame X, TimeFrame Y)[]
        {
            ("a D1/D1", TimeFrame.D1, TimeFrame.D1),
            ("w W1/W1", TimeFrame.W1, TimeFrame.W1),
            ("m MN1/MN1", TimeFrame.MN1, TimeFrame.MN1),
            ("x D1/W1", TimeFrame.D1, TimeFrame.W1),
            ("y W1/MN1", TimeFrame.W1, TimeFrame.MN1),
        };

        var medians = new Dictionary<string, (double Milliseconds, long AllocatedBytes)>();
        foreach ((string name, TimeFrame xFrame, TimeFrame yFrame) in cases)
        {
            ScatterMetricDefinition x = DefaultIndicator(IndicatorType.RSI);
            ScatterMetricDefinition y = DefaultIndicator(IndicatorType.SMA);
            x.Source.TimeFrame = xFrame;
            y.Source.TimeFrame = yFrame;
            ScatterEvaluationResult last = null!;
            medians[name] = await ReportAsync($"{name} RSI x SMA (all tickers, whole ComputeAsync)", async () =>
            {
                last = await evaluator.ComputeAsync(symbols, x, y, CancellationToken.None);
                Assert.Equal(symbols.Count, last.TotalCount);
            });
            _output.WriteLine($"   {name}: valid points {last.ValidCount}/{last.TotalCount}, failed {last.FailedCount}");
        }

        (double aMilliseconds, long aBytes) = medians["a D1/D1"];
        _output.WriteLine($"a D1/D1 of this run: {aMilliseconds:F1} ms, {(double)aBytes / BytesPerMegabyte:F2} MB/run (the reference of the ratios below)");
        bool pass = true;
        foreach ((string name, _, _) in cases.Skip(1))
        {
            double timeRatio = medians[name].Milliseconds / aMilliseconds;
            double allocationRatio = (double)medians[name].AllocatedBytes / aBytes;
            _output.WriteLine($"{name} / a: time {timeRatio:F2}, allocation {allocationRatio:F2} (threshold {TimeFrameToleranceFactor:F2} each)");
            pass &= timeRatio <= TimeFrameToleranceFactor && allocationRatio <= TimeFrameToleranceFactor;
        }

        _output.WriteLine(pass ? "M-1b provisional thresholds: WITHIN" : "M-1b provisional thresholds: OVER (stop and report)");
    }

    // ===== M-2: the Weekly / Monthly Parquet files equal the aggregation of the daily bars (STOP GATE) =====

    [EnvironmentFact(MeasureVariable)]
    public async Task M2_Gate_WeeklyAndMonthlyParquet_EqualTheAggregatedDailyBars()
    {
        IReadOnlyDictionary<string, SymbolSeries> all = await _data.GetSeriesAsync();
        var failures = new List<string>();

        foreach (TimeFrame frame in AggregatedFrames)
        {
            int symbolsCompared = 0, commonPeriods = 0, mismatchedPeriods = 0, parquetOnlyPeriods = 0, aggregateOnlyPeriods = 0, aggregateOnlyFinal = 0, aggregateOnlyMiddle = 0, duplicateKeys = 0;
            decimal maxRelativeDifference = 0m;
            var dateDifferences = new SortedDictionary<int, int>();
            var finalPeriodGaps = new List<string>();
            var samples = new List<string>();

            foreach (SymbolSeries series in all.Values)
            {
                IReadOnlyList<CandleData> daily = series.D1;
                IReadOnlyList<CandleData> parquet = series.For(frame);
                IReadOnlyList<CandleData> aggregated = TimeFrameAggregator.Aggregate(daily, frame);
                symbolsCompared++;

                if (parquet.Count == 0 && aggregated.Count > 0)
                {
                    failures.Add($"{frame} {series.Symbol}: no {frame} file (or no rows) although {aggregated.Count} aggregated periods exist");
                    continue;
                }

                var parquetByKey = new Dictionary<int, CandleData>();
                foreach (CandleData candle in parquet)
                {
                    if (!parquetByKey.TryAdd(PeriodKey(candle.Timestamp, frame), candle))
                    {
                        duplicateKeys++;
                    }
                }

                var aggregatedByKey = new Dictionary<int, CandleData>();
                foreach (CandleData candle in aggregated)
                {
                    aggregatedByKey[PeriodKey(candle.Timestamp, frame)] = candle;
                }

                int lastAggregatedKey = aggregated.Count == 0 ? int.MinValue : PeriodKey(aggregated[^1].Timestamp, frame);
                foreach ((int key, CandleData expected) in aggregatedByKey)
                {
                    if (!parquetByKey.TryGetValue(key, out CandleData actual))
                    {
                        aggregateOnlyPeriods++;
                        if (key == lastAggregatedKey)
                        {
                            aggregateOnlyFinal++;
                            finalPeriodGaps.Add($"{series.Symbol}: {frame} file ends {(parquet.Count == 0 ? "(empty)" : parquet[^1].Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}, daily ends {daily[^1].Timestamp:yyyy-MM-dd}, missing period {key}");
                        }
                        else
                        {
                            aggregateOnlyMiddle++;
                            failures.Add($"{frame} {series.Symbol}: period {key} exists in the daily aggregation but not in the file, and it is not the final period");
                        }

                        continue;
                    }

                    commonPeriods++;
                    int days = (actual.Timestamp.Date - expected.Timestamp.Date).Days;
                    dateDifferences[days] = dateDifferences.GetValueOrDefault(days) + 1;
                    if (!SameOhlcv(expected, actual))
                    {
                        mismatchedPeriods++;
                        maxRelativeDifference = Math.Max(maxRelativeDifference, MaxRelativeDifference(expected, actual));
                        if (samples.Count < MaxListedItems)
                        {
                            samples.Add($"{series.Symbol} {key}: file {actual} vs aggregated {expected}");
                        }
                    }
                }

                parquetOnlyPeriods += parquetByKey.Keys.Count(key => !aggregatedByKey.ContainsKey(key));
            }

            _output.WriteLine($"== M-2 {frame}: tickers compared {symbolsCompared}; common periods {commonPeriods}; OHLCV mismatches {mismatchedPeriods} (max relative difference {maxRelativeDifference}); " +
                              $"periods only in the file {parquetOnlyPeriods}; periods only in the aggregation {aggregateOnlyPeriods} (final period {aggregateOnlyFinal}, middle {aggregateOnlyMiddle}); duplicate keys inside the files {duplicateKeys}");
            _output.WriteLine($"   final-period gaps ({finalPeriodGaps.Count}):");
            foreach (string gap in finalPeriodGaps) _output.WriteLine("     " + gap);
            _output.WriteLine($"   date difference (file date - first business day of the period) -> count: {string.Join(", ", dateDifferences.Select(pair => $"{pair.Key}d:{pair.Value}"))}");
            foreach (string sample in samples) _output.WriteLine("   mismatch: " + sample);

            if (mismatchedPeriods > 0) failures.Add($"{frame}: {mismatchedPeriods} periods differ in Open/High/Low/Close/Volume");
            if (parquetOnlyPeriods > 0) failures.Add($"{frame}: {parquetOnlyPeriods} periods exist only in the file");
        }

        ReportNamingAndLags(all);
        WriteGate("M-2", failures);
        Assert.True(failures.Count == 0, "M-2 STOP: " + string.Join(" | ", failures.Take(MaxListedItems)));
    }

    // ===== M-3: the fixed-range exception table on W1 / MN1 (STOP GATE) =====

    [EnvironmentFact(MeasureVariable)]
    public void M3_Gate_FixedRangeExceptionTable_HoldsOnWeeklyAndMonthlySeries()
    {
        var table = ScatterAxisRangeResolver.OutputsOutsideTheirRange.Select(pair => $"{pair.Type}.{pair.Output}").ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<(IndicatorType Type, decimal Min, decimal Max, IReadOnlyList<string> Outputs)> fixedIndicators = FixedRangeIndicators();
        _output.WriteLine($"M-3: indicators with a fixed range = {fixedIndicators.Count}; exception table = {table.Count} pairs");

        List<CandleData> daily600 = ScatterMetricTestKit.Bars(AuditBarCount, AuditSeed);
        List<CandleData> daily12000 = ScatterMetricTestKit.Bars(LongAuditBarCount, AuditSeed);
        var failures = new List<string>();

        SortedDictionary<string, RangeExcursion> control = OutsideRange(fixedIndicators, daily600);
        WriteOutside("control D1 Bars(600)", control);
        // The table is the union over all audit series (daily, weekly, an SSA spike) and real data, so one daily series shows a subset of it.
        if (!control.Keys.ToHashSet(StringComparer.Ordinal).IsSubsetOf(table))
        {
            failures.Add("the control shows outputs that are not in the exception table (the procedure is wrong or the table is incomplete): " +
                         string.Join(", ", control.Keys.Where(key => !table.Contains(key))));
        }

        foreach ((TimeFrame frame, IReadOnlyList<CandleData> bars) in new (TimeFrame, IReadOnlyList<CandleData>)[]
                 {
                     (TimeFrame.W1, TimeFrameAggregator.AggregateDailyToWeekly(daily12000)),
                     (TimeFrame.MN1, TimeFrameAggregator.AggregateDailyToMonthly(daily12000)),
                 })
        {
            SortedDictionary<string, RangeExcursion> found = OutsideRange(fixedIndicators, bars);
            WriteOutside($"{frame} Bars({LongAuditBarCount}) aggregated -> {bars.Count} bars", found);
            string[] notInTable = found.Keys.Where(key => !table.Contains(key)).ToArray();
            _output.WriteLine($"   {frame} outputs outside their range that are NOT in the table: {(notInTable.Length == 0 ? "none" : string.Join(", ", notInTable))}");
            _output.WriteLine($"   {frame} table pairs that stay inside their range here: {string.Join(", ", table.Where(key => !found.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal))}");
            foreach (string key in notInTable)
            {
                failures.Add($"{frame}: {key} {found[key]}");
            }
        }

        WriteGate("M-3", failures);
        Assert.True(failures.Count == 0, "M-3 STOP: " + string.Join(" | ", failures));
    }

    // ===== M-3b: the fixed-range exceptions on the real data, without the slowest indicator (report only) =====

    [EnvironmentFact(MeasureVariable)]
    public async Task M3b_Reference_FixedRangeExceptions_OnRealData_WithoutTheSlowestIndicator()
    {
        var table = ScatterAxisRangeResolver.OutputsOutsideTheirRange.Select(pair => $"{pair.Type}.{pair.Output}").ToHashSet(StringComparer.Ordinal);
        List<(IndicatorType Type, decimal Min, decimal Max, IReadOnlyList<string> Outputs)> indicators =
            FixedRangeIndicators().Where(i => !ReferenceExcludedIndicators.Contains(i.Type)).ToList();
        IReadOnlyDictionary<string, SymbolSeries> all = await _data.GetSeriesAsync();
        int parallelism = Math.Max(1, Environment.ProcessorCount / ReferenceParallelismDivisor);
        _output.WriteLine($"M-3b: {indicators.Count} fixed-range indicators (excluded: {string.Join(", ", ReferenceExcludedIndicators)}); exception table = {table.Count} pairs; " +
                          $"{all.Count} tickers; parallelism {parallelism}; budget {ReferenceSecondsPerTimeFrame} s per time frame; D1 reads the newest {ReferenceDailyTailBars} bars");

        var timings = new ConcurrentDictionary<IndicatorType, TimeSpan>();
        var foundEverywhere = new SortedDictionary<string, RangeExcursion>(StringComparer.Ordinal);
        var incomplete = new List<TimeFrame>();
        foreach (TimeFrame frame in new[] { TimeFrame.W1, TimeFrame.MN1, TimeFrame.D1 })
        {
            SymbolSeries[] tickers = all.Values.ToArray();
            var perTicker = new SortedDictionary<string, RangeExcursion>?[tickers.Length];
            var stopwatch = Stopwatch.StartNew();
            bool OverBudget() => stopwatch.Elapsed.TotalSeconds > ReferenceSecondsPerTimeFrame;
            long barsRead = 0;
            Parallel.For(0, tickers.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, i =>
            {
                if (OverBudget()) return;
                IReadOnlyList<CandleData> bars = frame == TimeFrame.D1 ? tickers[i].D1.TakeLast(ReferenceDailyTailBars).ToList() : tickers[i].For(frame);
                SortedDictionary<string, RangeExcursion> found = OutsideRange(indicators, bars, timings, OverBudget);
                if (OverBudget()) return;
                perTicker[i] = found;
                Interlocked.Add(ref barsRead, bars.Count);
            });

            int done = perTicker.Count(found => found is not null);
            if (done < tickers.Length) incomplete.Add(frame);
            var count = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var extremes = new SortedDictionary<string, RangeExcursion>(StringComparer.Ordinal);
            foreach (SortedDictionary<string, RangeExcursion>? found in perTicker)
            {
                if (found is null) continue;
                foreach ((string key, RangeExcursion excursion) in found)
                {
                    count[key] = count.GetValueOrDefault(key) + 1;
                    extremes[key] = extremes.TryGetValue(key, out RangeExcursion? seen) ? seen.Merge(excursion) : excursion;
                    foundEverywhere[key] = foundEverywhere.TryGetValue(key, out RangeExcursion? total) ? total.Merge(excursion) : excursion;
                }
            }

            string scope = frame == TimeFrame.D1 ? $"newest {ReferenceDailyTailBars} bars" : "all rows";
            _output.WriteLine($"== {frame} real data ({scope}): {done}/{tickers.Length} tickers, {barsRead} bars, {stopwatch.Elapsed.TotalSeconds:F1} s{(done < tickers.Length ? "  NOT COMPLETE (time budget used up)" : string.Empty)}");
            foreach ((string key, int tickersOutside) in count)
            {
                _output.WriteLine($"   {key}: {tickersOutside}/{done} tickers; {extremes[key]}{(table.Contains(key) ? string.Empty : "   <-- NOT in the table")}");
            }

            _output.WriteLine($"   table pairs that stay inside their range on every ticker here: {string.Join(", ", table.Where(key => !count.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).DefaultIfEmpty("none"))}");
        }

        string[] additions = foundEverywhere.Keys.Where(key => !table.Contains(key)).ToArray();
        _output.WriteLine($"outputs outside their range on the real data (any time frame) that are NOT in the table: {(additions.Length == 0 ? "none" : string.Join(", ", additions))}");
        _output.WriteLine($"union of the current table and the real-data findings (before adding the synthetic finding of M-3): {table.Count + additions.Length} pairs");
        _output.WriteLine($"{(incomplete.Count == 0 ? "all time frames completed" : "INCOMPLETE time frames: " + string.Join(", ", incomplete))}; slowest indicators (total CPU over the run): " +
                          string.Join(", ", timings.OrderByDescending(pair => pair.Value).Take(SlowestListed).Select(pair => $"{pair.Key} {pair.Value.TotalSeconds:F1} s")));
    }

    // ===== M-4: data scale and whether a value exists =====

    [EnvironmentFact(MeasureVariable)]
    public async Task M4_DataScale_AndValueAvailability_PerTimeFrame()
    {
        IReadOnlyDictionary<string, SymbolSeries> all = await _data.GetSeriesAsync();
        _output.WriteLine($"M-4: tickers (Daily files) = {all.Count}");
        foreach (TimeFrame frame in TimeFrames)
        {
            int[] counts = all.Values.Select(series => series.For(frame).Count).ToArray();
            _output.WriteLine($"rows per ticker {frame}: {Summarize(counts)}");
        }

        var metrics = new List<(string Name, ScatterMetricDefinition Metric)>
        {
            ("SMA (default)", DefaultIndicator(IndicatorType.SMA)),
            ($"SMA (Period={LongSmaPeriod})", DefaultIndicator(IndicatorType.SMA, null, ("Period", LongSmaPeriod))),
            ("EMA", DefaultIndicator(IndicatorType.EMA)),
            ("RSI", DefaultIndicator(IndicatorType.RSI)),
            ("MACD", DefaultIndicator(IndicatorType.MACD)),
            ("ADX", DefaultIndicator(IndicatorType.ADX)),
            ("BollingerBands (BB)", DefaultIndicator(IndicatorType.BB)),
            ("ATR", DefaultIndicator(IndicatorType.ATR)),
            ("CCI", DefaultIndicator(IndicatorType.CCI)),
            ("Stoch", DefaultIndicator(IndicatorType.Stoch)),
            ("Donchian Position", DefaultIndicator(IndicatorType.Donchian, "Position")),
            ("Price Close", ScatterMetricTestKit.Indicator(IndicatorType.Price, 0, "Close")),
            ("Column Close", ColumnNamed("Close")),
            ("Column Change %", ColumnNamed("Change %")),
            ("Column Volume", ColumnNamed("Volume")),
        };

        _output.WriteLine("metric | required bars R | value exists / tickers per time frame (Offset 0, extractor on the loaded bars, TimeFrame=D1 clone) | tickers with fewer rows than R | threw");
        foreach ((string name, ScatterMetricDefinition metric) in metrics)
        {
            int required = RequiredCount(metric);
            ScreenerIndicatorSideConfig source = ScatterMetricTestKit.DaySourceOf(metric);
            var cells = new List<string>();
            foreach (TimeFrame frame in TimeFrames)
            {
                int withValue = 0, tooShort = 0, threw = 0;
                foreach (SymbolSeries series in all.Values)
                {
                    IReadOnlyList<CandleData> candles = series.For(frame);
                    if (required > 0 && candles.Count < required) tooShort++;
                    if (candles.Count == 0) continue;
                    try
                    {
                        if (Extractor.ExtractValueNullable(source, candles, default(TickerMetadata)) is not null) withValue++;
                    }
                    catch (Exception)
                    {
                        threw++;
                    }
                }

                cells.Add($"{frame} {withValue}/{all.Count} (short {tooShort}{(threw > 0 ? $", threw {threw}" : string.Empty)})");
            }

            _output.WriteLine($"{name} ({metric.Source.FormattedParameters}, output {metric.Source.OutputName}) | R={required} | {string.Join(" | ", cells)}");
        }
    }

    // ===== M-5: data quality and freshness =====

    [EnvironmentFact(MeasureVariable)]
    public async Task M5_DataQuality_AndFreshness()
    {
        IReadOnlyDictionary<string, SymbolSeries> all = await _data.GetSeriesAsync();
        long rows = 0, duplicateDates = 0, notAscending = 0, closeNotPositive = 0, highBelowLow = 0, highBelowBody = 0, lowAboveBody = 0, zeroVolume = 0;
        var perSymbolIssues = new List<string>();
        foreach (SymbolSeries series in all.Values)
        {
            IReadOnlyList<CandleData> bars = series.D1;
            long dup = 0, order = 0, bad = 0;
            for (int i = 0; i < bars.Count; i++)
            {
                CandleData bar = bars[i];
                rows++;
                if (i > 0)
                {
                    if (bar.Timestamp == bars[i - 1].Timestamp) dup++;
                    else if (bar.Timestamp < bars[i - 1].Timestamp) order++;
                }

                if (bar.Close <= 0m) { closeNotPositive++; bad++; }
                if (bar.High < bar.Low) { highBelowLow++; bad++; }
                if (bar.High < Math.Max(bar.Open, bar.Close)) { highBelowBody++; bad++; }
                if (bar.Low > Math.Min(bar.Open, bar.Close)) { lowAboveBody++; bad++; }
                if (bar.Volume == 0) zeroVolume++;
            }

            duplicateDates += dup;
            notAscending += order;
            if (dup + order + bad > 0) perSymbolIssues.Add($"{series.Symbol}: duplicate dates {dup}, not ascending {order}, invalid bars {bad}");
        }

        _output.WriteLine($"M-5 daily quality: rows {rows}; duplicate dates {duplicateDates}; not ascending {notAscending}; Close<=0 {closeNotPositive}; High<Low {highBelowLow}; " +
                          $"High<max(Open,Close) {highBelowBody}; Low>min(Open,Close) {lowAboveBody}; Volume=0 {zeroVolume} ({(rows == 0 ? 0 : 100.0 * zeroVolume / rows):F2}%)");
        foreach (string issue in perSymbolIssues.Take(MaxListedItems)) _output.WriteLine("   " + issue);

        DateTime[] lastDates = all.Values.Where(series => series.D1.Count > 0).Select(series => series.D1[^1].Timestamp.Date).ToArray();
        DateTime newest = lastDates.Max();
        _output.WriteLine($"last daily date: min {lastDates.Min():yyyy-MM-dd}, max {newest:yyyy-MM-dd}, distinct {lastDates.Distinct().Count()}; " +
                          $"by date: {string.Join(", ", lastDates.GroupBy(d => d).OrderBy(g => g.Key).Select(g => $"{g.Key:MM-dd}:{g.Count()}"))}");
        _output.WriteLine($"tickers whose last day is in an older ISO week than the newest ticker's: {lastDates.Count(d => PeriodKey(d, TimeFrame.W1) != PeriodKey(newest, TimeFrame.W1))}; " +
                          $"in an older calendar month: {lastDates.Count(d => PeriodKey(d, TimeFrame.MN1) != PeriodKey(newest, TimeFrame.MN1))} (of {lastDates.Length})");

        ReportNamingAndLags(all);
    }

    // ===== M-6: sensitivity to the loaded history length =====

    [EnvironmentFact(MeasureVariable)]
    public async Task M6_SensitivityToTheLoadedHistoryLength_OnWeeklyAndMonthly()
    {
        IReadOnlyDictionary<string, SymbolSeries> all = await _data.GetSeriesAsync();
        var metrics = new List<(string Name, ScatterMetricDefinition Metric)>
        {
            ("RSI (default)", DefaultIndicator(IndicatorType.RSI)),
            ("EMA (default)", DefaultIndicator(IndicatorType.EMA)),
            ("MACD (default, first output)", DefaultIndicator(IndicatorType.MACD)),
        };

        _output.WriteLine("M-6: value on the last N rows (N = required bars R, loaded with LoadCandlesAsync(symbol, tf, R)) vs the value on the whole file");
        foreach (TimeFrame frame in AggregatedFrames)
        {
            foreach ((string name, ScatterMetricDefinition metric) in metrics)
            {
                int required = RequiredCount(metric);
                ScreenerIndicatorSideConfig source = ScatterMetricTestKit.DaySourceOf(metric);
                var absolute = new List<decimal>();
                var relative = new List<decimal>();
                int compared = 0, identical = 0, longerThanR = 0, onlyOneSideHasAValue = 0;
                foreach (SymbolSeries series in all.Values)
                {
                    IReadOnlyList<CandleData> whole = series.For(frame);
                    if (whole.Count == 0) continue;
                    IReadOnlyList<CandleData> tail = await _data.Service.LoadCandlesAsync(series.Symbol, frame, required);
                    if (whole.Count > required) longerThanR++;
                    decimal? wholeValue = Extractor.ExtractValueNullable(source, whole, default(TickerMetadata));
                    decimal? tailValue = Extractor.ExtractValueNullable(source, tail, default(TickerMetadata));
                    if (wholeValue is null != tailValue is null)
                    {
                        onlyOneSideHasAValue++;
                        continue;
                    }

                    if (wholeValue is null || tailValue is null) continue;
                    compared++;
                    decimal difference = Math.Abs(wholeValue.Value - tailValue.Value);
                    if (difference == 0m) identical++;
                    absolute.Add(difference);
                    relative.Add(wholeValue.Value == 0m ? difference : difference / Math.Abs(wholeValue.Value));
                }

                _output.WriteLine($"{frame} {name}: R={required}; tickers with more rows than R = {longerThanR}; compared {compared}; identical {identical}/{compared}; value on only one side {onlyOneSideHasAValue}; " +
                                  $"absolute difference max {(absolute.Count == 0 ? 0m : absolute.Max())} median {MedianOf(absolute)}; relative difference max {(relative.Count == 0 ? 0m : relative.Max())} median {MedianOf(relative)}");
            }
        }
    }

    // ===== Shared measurement and reporting helpers =====

    /// <summary>Runs <paramref name="run"/> once (discarded) and <see cref="MeasuredRuns"/> times, prints the figures and returns the median time (ms) and median allocation (bytes) of one run.</summary>
    private async Task<(double Milliseconds, long AllocatedBytes)> ReportAsync(string label, Func<Task> run)
    {
        await run();
        var times = new double[MeasuredRuns];
        var allocations = new long[MeasuredRuns];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        double workingSetBefore = (double)Process.GetCurrentProcess().WorkingSet64 / BytesPerMegabyte;
        for (int i = 0; i < MeasuredRuns; i++)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            long started = Stopwatch.GetTimestamp();
            await run();
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[i] = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        }

        double workingSetAfter = (double)Process.GetCurrentProcess().WorkingSet64 / BytesPerMegabyte;
        double[] sortedTimes = times.OrderBy(t => t).ToArray();
        long medianAllocation = allocations.OrderBy(a => a).ToArray()[MeasuredRuns / 2];
        double median = sortedTimes[MeasuredRuns / 2];
        _output.WriteLine($"{label}: median {median:F1} ms (min {sortedTimes[0]:F1}, max {sortedTimes[^1]:F1}); allocated {(double)medianAllocation / BytesPerMegabyte:F2} MB/run; working set {workingSetBefore:F0} -> {workingSetAfter:F0} MB");
        return (median, medianAllocation);
    }

    private async Task LoadAllAsync(IReadOnlyList<string> symbols, TimeFrame frame, int count, int parallelism)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };
        await Parallel.ForEachAsync(symbols, options, async (symbol, token) =>
        {
            token.ThrowIfCancellationRequested();
            _ = await _data.Service.LoadCandlesAsync(symbol, frame, count);
        });
    }

    private void WriteGate(string gate, IReadOnlyList<string> failures)
    {
        _output.WriteLine(failures.Count == 0
            ? $"GATE {gate}: PASS"
            : $"GATE {gate}: STOP ({failures.Count} findings)" + Environment.NewLine + string.Join(Environment.NewLine, failures.Take(MaxListedItems).Select(f => "   " + f)));
    }

    /// <summary>Final-period lags of the Weekly / Monthly files against the daily bars, missing files, and tickers that have more than one spelling (dot / hyphen).</summary>
    private void ReportNamingAndLags(IReadOnlyDictionary<string, SymbolSeries> all)
    {
        foreach (TimeFrame frame in AggregatedFrames)
        {
            var lagging = new List<string>();
            var missing = new List<string>();
            foreach (SymbolSeries series in all.Values)
            {
                if (series.D1.Count == 0) continue;
                IReadOnlyList<CandleData> file = series.For(frame);
                if (file.Count == 0)
                {
                    missing.Add(series.Symbol);
                    continue;
                }

                int fileKey = PeriodKey(file[^1].Timestamp, frame);
                int dailyKey = PeriodKey(series.D1[^1].Timestamp, frame);
                if (fileKey < dailyKey)
                {
                    lagging.Add($"{series.Symbol} (file ends {file[^1].Timestamp:yyyy-MM-dd}, daily ends {series.D1[^1].Timestamp:yyyy-MM-dd})");
                }
            }

            _output.WriteLine($"{frame} files that end before the daily bars' last period: {lagging.Count} -> {string.Join("; ", lagging)}");
            _output.WriteLine($"{frame} tickers with a Daily file but no {frame} rows: {missing.Count} {string.Join(", ", missing)}");
        }

        var stemsByFolder = TimeFrames.ToDictionary(frame => frame, frame => _data.StemsOf(frame));
        var spellings = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach ((TimeFrame frame, IReadOnlyList<string> stems) in stemsByFolder)
        {
            foreach (string stem in stems)
            {
                string key = stem.Replace('.', '-');
                if (!spellings.TryGetValue(key, out SortedSet<string>? set)) spellings[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(stem);
            }
        }

        string[] conflicts = spellings.Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key}: " + string.Join(", ", pair.Value.Select(stem => $"{stem} [{string.Join("/", TimeFrames.Where(frame => stemsByFolder[frame].Contains(stem)))}]")))
            .ToArray();
        _output.WriteLine($"tickers that have more than one spelling across the Daily / Weekly / Monthly folders: {conflicts.Length}");
        foreach (string conflict in conflicts) _output.WriteLine("   " + conflict);
        foreach (TimeFrame frame in TimeFrames)
        {
            string[] extra = stemsByFolder[frame].Where(stem => !all.ContainsKey(stem)).ToArray();
            _output.WriteLine($"{frame} files whose name is not a Daily file name: {extra.Length} {string.Join(", ", extra)}");
        }
    }

    // ===== Metric builders (the registration screen's defaults) =====

    private static ScatterMetricDefinition DefaultIndicator(IndicatorType type, string? preferredOutput = null, params (string Key, object Value)[] overrides)
    {
        CoreIndicatorSettings settings = Catalog.GetDefaultSettings(type, IndicatorFactory.Default)
            ?? throw new InvalidOperationException($"No default settings for {type}.");
        IReadOnlyList<string> outputs = Catalog.GetOutputSeriesNames(type, IndicatorFactory.Default);
        string output = preferredOutput is not null && outputs.Contains(preferredOutput) ? preferredOutput : outputs[0];

        // The registration screen copies every readable property of the default parameter object (IndicatorRegistrationViewModel.BuildParameterDictionary).
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal);
        if (settings.ParameterObject is not null)
        {
            foreach (PropertyInfo property in settings.ParameterObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                if (property.GetValue(settings.ParameterObject) is { } value) parameters[property.Name] = value;
            }
        }

        foreach ((string key, object value) in overrides)
        {
            parameters[key] = value;
        }

        return ScatterMetricTestKit.Indicator(type, 0, output, parameters.Select(pair => (pair.Key, pair.Value)).ToArray());
    }

    private static ScatterMetricDefinition ColumnNamed(string name) => new()
    {
        Source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            OutputName = name,
            CategoryType = ScreenerItemCategoryType.Column,
            TimeFrame = TimeFrame.D1,
        },
    };

    /// <summary>Same sizing call as <c>ScatterMetricEvaluator.RequiredCount</c>.</summary>
    private static int RequiredCount(ScatterMetricDefinition metric)
        => ScreenerHistoryRequirements.GetRequiredCandleCount(new ScreenerIndicatorEntry
        {
            LeftHand = metric.Source.Clone(),
            TargetMode = RightHandTargetMode.NumericValue,
            Operator = ComparisonOperator.GreaterThan,
        });

    // ===== Period keys and comparisons (the keys of plan 14.3) =====

    private static int PeriodKey(DateTime date, TimeFrame frame)
        => frame == TimeFrame.W1 ? (ISOWeek.GetYear(date) * 100) + ISOWeek.GetWeekOfYear(date) : (date.Year * 100) + date.Month;

    private static bool SameOhlcv(CandleData a, CandleData b)
        => a.Open == b.Open && a.High == b.High && a.Low == b.Low && a.Close == b.Close && a.Volume == b.Volume;

    private static decimal MaxRelativeDifference(CandleData expected, CandleData actual)
    {
        static decimal Relative(decimal e, decimal a) => e == 0m ? Math.Abs(a) : Math.Abs(e - a) / Math.Abs(e);
        return new[]
        {
            Relative(expected.Open, actual.Open), Relative(expected.High, actual.High), Relative(expected.Low, actual.Low),
            Relative(expected.Close, actual.Close), Relative(expected.Volume, actual.Volume),
        }.Max();
    }

    private static string Summarize(int[] values)
    {
        if (values.Length == 0) return "no tickers";
        int[] sorted = values.OrderBy(v => v).ToArray();
        int p90 = sorted[(int)Math.Ceiling(TailPercentile * sorted.Length) - 1];
        int median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
        return $"min {sorted[0]}, median {median}, p90 {p90}, max {sorted[^1]}, total {sorted.Sum(v => (long)v)}";
    }

    private static decimal MedianOf(List<decimal> values)
    {
        if (values.Count == 0) return 0m;
        decimal[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2m;
    }

    // ===== M-3 audit: the same judgment as ScatterAxisRangeResolverTests (kept here so that file stays unchanged) =====

    private static IReadOnlyList<(IndicatorType Type, decimal Min, decimal Max, IReadOnlyList<string> Outputs)> FixedRangeIndicators()
        => DefaultCoreIndicatorSettings.GetDefault()
            .Where(s => s.TypeEnum.HasValue && s.MinValue.HasValue && s.MaxValue.HasValue && s.MinValue < s.MaxValue)
            .OrderBy(s => s.TypeEnum!.Value.ToString(), StringComparer.Ordinal)
            .Select(s => (s.TypeEnum!.Value, s.MinValue!.Value, s.MaxValue!.Value, Catalog.GetOutputSeriesNames(s.TypeEnum!.Value, IndicatorFactory.Default)))
            .ToList();

    /// <param name="timings">When given, the calculation time of every indicator is added to it.</param>
    /// <param name="isOverBudget">When given and true between two indicators, the remaining indicators are skipped.</param>
    private static SortedDictionary<string, RangeExcursion> OutsideRange(
        IReadOnlyList<(IndicatorType Type, decimal Min, decimal Max, IReadOnlyList<string> Outputs)> indicators,
        IReadOnlyList<CandleData> bars,
        ConcurrentDictionary<IndicatorType, TimeSpan>? timings = null,
        Func<bool>? isOverBudget = null)
    {
        var outside = new SortedDictionary<string, RangeExcursion>(StringComparer.Ordinal);
        if (bars.Count == 0) return outside;

        List<CoreCandleData> candles = bars.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        foreach ((IndicatorType type, decimal min, decimal max, IReadOnlyList<string> outputs) in indicators)
        {
            if (isOverBudget?.Invoke() == true) break;
            long started = Stopwatch.GetTimestamp();
            try
            {
                CollectOutside(type, min, max, outputs, candles, outside);
            }
            finally
            {
                if (timings is not null) { TimeSpan elapsed = Stopwatch.GetElapsedTime(started); timings.AddOrUpdate(type, elapsed, (_, total) => total + elapsed); }
            }
        }

        return outside;
    }

    private static void CollectOutside(
        IndicatorType type, decimal min, decimal max, IReadOnlyList<string> outputs, List<CoreCandleData> candles, SortedDictionary<string, RangeExcursion> outside)
    {
        ICoreIndicator? indicator = IndicatorFactory.Default.Create(type);
        if (indicator is null) return;

        CoreIndicatorSettings settings = indicator.GetDefaultSettings();
        if (settings.ParameterObject != null)
        {
            indicator.Configure(settings.ParameterObject);
        }

        IIndicatorResult? result = indicator.Calculate(candles);
        if (result is null || !result.IsSuccessful) return;

        foreach (string output in outputs)
        {
            if (!IndicatorOutputSeriesResolver.TryResolveSeries(result, type, output, (PriceType?)null, out IReadOnlyList<decimal?> series)) continue;

            decimal? lowest = null;
            decimal? highest = null;
            foreach (decimal? value in series)
            {
                if (value is not decimal v) continue;
                lowest = lowest is null ? v : Math.Min(lowest.Value, v);
                highest = highest is null ? v : Math.Max(highest.Value, v);
            }

            if (lowest is not null && (lowest < min || highest > max))
            {
                outside[$"{type}.{output}"] = new RangeExcursion(lowest.Value, highest!.Value, min, max);
            }
        }
    }

    private void WriteOutside(string label, SortedDictionary<string, RangeExcursion> outside)
    {
        _output.WriteLine($"{label}: {outside.Count} outputs outside their range");
        foreach ((string key, RangeExcursion detail) in outside) _output.WriteLine($"   {key}  {detail}");
    }
}

/// <summary>The lowest and highest value an output took, and the fixed range it was expected to stay in.</summary>
public sealed record RangeExcursion(decimal Lowest, decimal Highest, decimal Min, decimal Max)
{
    public RangeExcursion Merge(RangeExcursion other) => this with { Lowest = Math.Min(Lowest, other.Lowest), Highest = Math.Max(Highest, other.Highest) };

    public override string ToString() => $"observed [{Lowest},{Highest}] outside [{Min},{Max}]";
}

/// <summary>The daily, weekly and monthly bars of one Daily file name, read as the application reads them (all rows).</summary>
public sealed record SymbolSeries(string Symbol, IReadOnlyList<CandleData> D1, IReadOnlyList<CandleData> W1, IReadOnlyList<CandleData> MN1)
{
    public IReadOnlyList<CandleData> For(TimeFrame frame) => frame switch
    {
        TimeFrame.D1 => D1,
        TimeFrame.W1 => W1,
        TimeFrame.MN1 => MN1,
        _ => throw new ArgumentOutOfRangeException(nameof(frame), frame, "Only D1, W1 and MN1 are measured."),
    };
}

/// <summary>
/// The repository's real Data folder, opened read-only through the production <see cref="ParquetDataService"/>. Nothing is read until a fact asks for it,
/// so the skipped facts of a normal run perform no I/O.
/// </summary>
public sealed class ScatterMeasurementData : IDisposable
{
    private const string DataFolderName = "Data";
    private const string ParquetPattern = "*.parquet";

    private DuckDBConnectionManager? _db;
    private ParquetDataService? _service;
    private MarketDataSettings? _settings;
    private IReadOnlyList<string>? _symbols;
    private Task<IReadOnlyDictionary<string, SymbolSeries>>? _series;

    public ParquetDataService Service
    {
        get
        {
            Open();
            return _service!;
        }
    }

    /// <summary>The Daily file names (without extension), ordinal order: the universe of the measurement.</summary>
    public IReadOnlyList<string> Symbols
    {
        get
        {
            Open();
            return _symbols!;
        }
    }

    public IReadOnlyList<string> StemsOf(TimeFrame frame)
    {
        Open();
        return Directory.EnumerateFiles(FolderOf(frame), ParquetPattern)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(stem => stem, StringComparer.Ordinal)
            .ToList();
    }

    public Task<IReadOnlyDictionary<string, SymbolSeries>> GetSeriesAsync() => _series ??= LoadAllAsync();

    public void Dispose() => _db?.Dispose();

    private string FolderOf(TimeFrame frame) => frame switch
    {
        TimeFrame.D1 => _settings!.DailyDataPath!,
        TimeFrame.W1 => _settings!.WeeklyDataPath!,
        TimeFrame.MN1 => _settings!.MonthlyDataPath!,
        _ => throw new ArgumentOutOfRangeException(nameof(frame), frame, "Only D1, W1 and MN1 are measured."),
    };

    private void Open()
    {
        if (_service is not null) return;

        string dataRoot = Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), DataFolderName);

        _settings = new MarketDataSettings
        {
            DailyDataPath = Path.Combine(dataRoot, "Daily"),
            WeeklyDataPath = Path.Combine(dataRoot, "Weekly"),
            MonthlyDataPath = Path.Combine(dataRoot, "Monthly"),
        };
        _db = new DuckDBConnectionManager();
        _service = new ParquetDataService(_db, Options.Create(_settings));
        _symbols = StemsOf(TimeFrame.D1);
    }

    private async Task<IReadOnlyDictionary<string, SymbolSeries>> LoadAllAsync()
    {
        var all = new Dictionary<string, SymbolSeries>(StringComparer.Ordinal);
        foreach (string symbol in Symbols)
        {
            all[symbol] = new SymbolSeries(
                symbol,
                await Service.LoadCandlesAsync(symbol, TimeFrame.D1, 0),
                await Service.LoadCandlesAsync(symbol, TimeFrame.W1, 0),
                await Service.LoadCandlesAsync(symbol, TimeFrame.MN1, 0));
        }

        return all;
    }
}
