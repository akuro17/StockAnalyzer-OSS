#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Analysis;

public class ScatterAxisRangeResolverTests
{
    private const int AuditBarCount = 600;
    private const int AuditSeed = 20261021;
    private const int LongAuditBarCount = 12000;
    private const decimal StepBasePrice = 100m;
    private const decimal StepSize = 10m;
    private const decimal StepNoise = 0.01m;
    private const int StepStartBar = 500;

    private readonly ITestOutputHelper _output;
    private readonly ScreenerCatalogProvider _catalog = new();

    public ScatterAxisRangeResolverTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Rsi_IsFixedToZeroHundred()
    {
        bool fixedRange = ScatterAxisRangeResolver.TryResolve(ScatterMetricTestKit.Indicator(IndicatorType.RSI), _catalog, IndicatorFactory.Default, out AxisRange range);

        Assert.True(fixedRange);
        Assert.Equal(new AxisRange(0.0, 100.0), range);
    }

    [Fact]
    public void ChangingTheParametersDoesNotChangeTheRange()
    {
        var metric = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 50));

        Assert.True(ScatterAxisRangeResolver.TryResolve(metric, _catalog, IndicatorFactory.Default, out AxisRange range));
        Assert.Equal(new AxisRange(0.0, 100.0), range);
    }

    [Fact]
    public void AnIndicatorWithoutARegisteredRange_IsNotFixed()
    {
        Assert.False(ScatterAxisRangeResolver.TryResolve(ScatterMetricTestKit.Indicator(IndicatorType.SMA), _catalog, IndicatorFactory.Default, out AxisRange range));
        Assert.Equal(default, range);
    }

    [Fact]
    public void AColumnAndAPriceSource_AreNeverFixed()
    {
        var column = ScatterMetricTestKit.Column(ScatterMetricTestKit.FirstNumericColumn());
        var price = ScatterMetricTestKit.Indicator(IndicatorType.Price, output: "Close");

        Assert.False(ScatterAxisRangeResolver.TryResolve(column, _catalog, IndicatorFactory.Default, out _));
        Assert.False(ScatterAxisRangeResolver.TryResolve(price, _catalog, IndicatorFactory.Default, out _));
        Assert.False(ScatterAxisRangeResolver.TryResolve(null, _catalog, IndicatorFactory.Default, out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void OnlyOneBoundOrNone_IsNotFixed(bool hasMin, bool hasMax)
    {
        var provider = new FixedSettingsCatalog(hasMin ? 0m : null, hasMax ? 100m : null);

        Assert.False(ScatterAxisRangeResolver.TryResolve(ScatterMetricTestKit.Indicator(IndicatorType.RSI), provider, IndicatorFactory.Default, out _));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(100, 0)]
    public void MinNotBelowMax_IsNotFixed(int min, int max)
    {
        var provider = new FixedSettingsCatalog(min, max);

        Assert.False(ScatterAxisRangeResolver.TryResolve(ScatterMetricTestKit.Indicator(IndicatorType.RSI), provider, IndicatorFactory.Default, out _));
    }

    private static string[] ExcludedOutputNames()
        => ScatterAxisRangeResolver.OutputsOutsideTheirRange.Select(pair => $"{pair.Type}.{pair.Output}").OrderBy(v => v, StringComparer.Ordinal).ToArray();

    [Fact]
    public void TheOutputsOutsideTheirRange_GetAnAutomaticAxis()
    {
        Assert.Equal(12, ScatterAxisRangeResolver.OutputsOutsideTheirRange.Count);
        foreach ((IndicatorType type, string output) in ScatterAxisRangeResolver.OutputsOutsideTheirRange)
        {
            var metric = ScatterMetricTestKit.Indicator(type, 0, output);

            Assert.False(ScatterAxisRangeResolver.TryResolve(metric, _catalog, IndicatorFactory.Default, out AxisRange range), $"{type}.{output} must not be fixed");
            Assert.Equal(default, range);
        }
    }

    [Fact]
    public void AnotherOutputOfTheSameIndicator_KeepsItsFixedRange()
    {
        var kept = new List<string>();
        foreach (IndicatorType type in ScatterAxisRangeResolver.OutputsOutsideTheirRange.Select(pair => pair.Type).Distinct())
        {
            foreach (string output in _catalog.GetOutputSeriesNames(type, IndicatorFactory.Default))
            {
                bool excluded = ScatterAxisRangeResolver.OutputsOutsideTheirRange.Any(pair => pair.Type == type && pair.Output == output);
                bool isFixed = ScatterAxisRangeResolver.TryResolve(ScatterMetricTestKit.Indicator(type, 0, output), _catalog, IndicatorFactory.Default, out _);
                // Every indicator of the table carries a MinValue and a MaxValue, so only the table decides here.
                Assert.Equal(!excluded, isFixed);
                if (isFixed) kept.Add($"{type}.{output}");
            }
        }

        Assert.NotEmpty(kept);
    }

    private static List<CoreCandleData> ToCore(IEnumerable<CandleData> bars)
        => bars.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

    /// <summary>The daily series of the first audit (owner decision F-27 = A): 600 bars, every indicator with a fixed range.</summary>
    private static List<CoreCandleData> DailyAuditSeries() => ToCore(ScatterMetricTestKit.Bars(AuditBarCount, AuditSeed));

    /// <summary>The same walk aggregated to weeks (about 1,700 bars): <c>IFFTInstantaneousPhase.PhaseStability</c> leaves [-1,1] on it, not on the daily series.</summary>
    private static List<CoreCandleData> WeeklyAuditSeries() => ToCore(TimeFrameAggregator.AggregateDailyToWeekly(ScatterMetricTestKit.Bars(LongAuditBarCount, AuditSeed)));

    /// <summary>
    /// A flat price with a tiny deterministic noise that steps up by <see cref="StepSize"/> for the last bars: the residual of <c>SSAAnomaly</c> is then a few
    /// times its own spread (z-score above 5; a lone spike bar is absorbed by the reconstruction and does not show it).
    /// </summary>
    private static List<CoreCandleData> StepAuditSeries()
    {
        var random = new Random(AuditSeed);
        DateTime day = new(2024, 1, 1);
        var candles = new List<CoreCandleData>(AuditBarCount);
        for (int i = 0; i < AuditBarCount; i++)
        {
            decimal close = StepBasePrice + (decimal)((random.NextDouble() - 0.5) * (double)StepNoise) + (i >= StepStartBar ? StepSize : 0m);
            candles.Add(new CoreCandleData(day.AddDays(i), close, close + StepNoise, close - StepNoise, close, 1_000_000L));
        }

        return candles;
    }

    /// <summary>
    /// Every output series of every indicator with a fixed range (Min and Max both set, Min &lt; Max) that leaves its range on <paramref name="candles"/>,
    /// as "Type.Output" -> the observation; <paramref name="onlyTypes"/> restricts the audit to those indicators (null = all).
    /// </summary>
    private SortedDictionary<string, string> OutputsOutsideRangeOn(List<CoreCandleData> candles, ISet<IndicatorType>? onlyTypes, StringBuilder? table = null)
    {
        var violations = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (CoreIndicatorSettings defaults in DefaultCoreIndicatorSettings.GetDefault()
                     .Where(s => s.TypeEnum.HasValue && s.MinValue.HasValue && s.MaxValue.HasValue && s.MinValue < s.MaxValue)
                     .OrderBy(s => s.TypeEnum!.Value.ToString(), StringComparer.Ordinal))
        {
            IndicatorType type = defaults.TypeEnum!.Value;
            if (onlyTypes is not null && !onlyTypes.Contains(type)) continue;

            decimal min = defaults.MinValue!.Value;
            decimal max = defaults.MaxValue!.Value;

            ICoreIndicator? indicator = IndicatorFactory.Default.Create(type);
            if (indicator is null)
            {
                table?.AppendLine($"{type,-28} [{min},{max}]  not registered");
                continue;
            }

            CoreIndicatorSettings settings = indicator.GetDefaultSettings();
            if (settings.ParameterObject != null)
            {
                indicator.Configure(settings.ParameterObject);
            }

            IIndicatorResult? result = indicator.Calculate(candles);
            if (result is null || !result.IsSuccessful)
            {
                table?.AppendLine($"{type,-28} [{min},{max}]  not computed on this series (no value to judge)");
                continue;
            }

            foreach (string output in _catalog.GetOutputSeriesNames(type, IndicatorFactory.Default))
            {
                if (!IndicatorOutputSeriesResolver.TryResolveSeries(result, type, output, (PriceType?)null, out IReadOnlyList<decimal?> series))
                {
                    table?.AppendLine($"{type,-28} {output,-18} [{min},{max}]  series not found");
                    continue;
                }

                decimal? lowest = null;
                decimal? highest = null;
                foreach (decimal? value in series)
                {
                    if (value is not decimal v) continue;
                    lowest = lowest is null ? v : Math.Min(lowest.Value, v);
                    highest = highest is null ? v : Math.Max(highest.Value, v);
                }

                bool inside = lowest is null || (lowest >= min && highest <= max);
                table?.AppendLine($"{type,-28} {output,-18} [{min},{max}]  observed [{lowest},{highest}]  {(inside ? "inside" : "OUTSIDE")}");
                if (!inside)
                {
                    violations[$"{type}.{output}"] = $"observed [{lowest},{highest}] outside [{min},{max}]";
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Specification audit (plan S8.4), the "no exception is missing" direction. "Fixed" is defined by the default settings' MinValue and MaxValue. This
    /// runs every output series of every such indicator over the deterministic daily series and prints the table. Every output found outside its range
    /// must be in <see cref="ScatterAxisRangeResolver.OutputsOutsideTheirRange"/> (owner decision F-27 = A: those outputs get an automatic axis).
    /// </summary>
    [Fact]
    public void FixedRangeIndicators_OutputsOutsideTheirRange_AreAllInTheTable_OnADeterministicSeries()
    {
        var table = new StringBuilder();
        SortedDictionary<string, string> violations = OutputsOutsideRangeOn(DailyAuditSeries(), null, table);
        int fixedCount = DefaultCoreIndicatorSettings.GetDefault()
            .Count(s => s.TypeEnum.HasValue && s.MinValue.HasValue && s.MaxValue.HasValue && s.MinValue < s.MaxValue);

        _output.WriteLine($"Indicators with a fixed range (Min and Max both set): {fixedCount}");
        _output.WriteLine(table.ToString());

        Assert.True(fixedCount > 0, "No indicator carries a fixed range; the audit has nothing to judge.");
        string[] missing = violations.Keys.Except(ExcludedOutputNames(), StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            "An output leaves its fixed range but has no exception in OutputsOutsideTheirRange: " + string.Join(", ", missing)
            + ". Details: " + string.Join("; ", missing.Select(key => $"{key}: {violations[key]}")));
    }

    /// <summary>
    /// The "no exception is unfounded" direction. One daily series cannot show every output that leaves its range (a weekly bar size, or a sustained step, is
    /// needed for two of them; the real data shows both on every time frame, measured 2026-10-22), so the audit series together must cover the table exactly:
    /// every pair leaves its range on at least one series, and no series shows an output that is not in the table.
    /// </summary>
    [Fact]
    public void TheTable_IsExactlyWhatTheAuditSeriesTogetherFind()
    {
        string[] table = ExcludedOutputNames();
        var firstSeen = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var unexpected = new List<string>();

        void Audit(string series, List<CoreCandleData> candles, ISet<IndicatorType>? onlyTypes)
        {
            foreach ((string key, string detail) in OutputsOutsideRangeOn(candles, onlyTypes))
            {
                _output.WriteLine($"{series}: {key} {detail}");
                if (!table.Contains(key, StringComparer.Ordinal)) unexpected.Add($"{series}: {key} {detail}");
                firstSeen.TryAdd(key, series);
            }
        }

        Audit("daily 600 bars", DailyAuditSeries(), null);
        Audit("weekly (aggregated 12,000 days)", WeeklyAuditSeries(), new HashSet<IndicatorType> { IndicatorType.IFFTInstantaneousPhase });
        Audit("flat 600 bars with a step up at bar 500", StepAuditSeries(), new HashSet<IndicatorType> { IndicatorType.SSAAnomaly });

        string[] unfounded = table.Except(firstSeen.Keys, StringComparer.Ordinal).ToArray();
        Assert.True(unexpected.Count == 0, "An audit series shows an output outside its range that is not in the table: " + string.Join("; ", unexpected));
        Assert.True(unfounded.Length == 0, "A pair of the table leaves its range on none of the audit series: " + string.Join(", ", unfounded));
    }

    /// <summary>
    /// One table serves every time frame (the resolver has no time-frame input): on the Week and the Month series built from the same walk, no fixed-range
    /// indicator leaves its range except through a pair of the table.
    /// </summary>
    [Fact]
    public void OnWeeklyAndMonthlySeries_EveryOutputOutsideItsRange_IsInTheTable()
    {
        string[] table = ExcludedOutputNames();
        List<CandleData> days = ScatterMetricTestKit.Bars(LongAuditBarCount, AuditSeed);
        var series = new (string Name, List<CoreCandleData> Candles)[]
        {
            ("weekly (aggregated 12,000 days)", ToCore(TimeFrameAggregator.AggregateDailyToWeekly(days))),
            ("monthly (aggregated 12,000 days)", ToCore(TimeFrameAggregator.AggregateDailyToMonthly(days))),
        };

        foreach ((string name, List<CoreCandleData> candles) in series)
        {
            SortedDictionary<string, string> violations = OutputsOutsideRangeOn(candles, null);
            foreach ((string key, string detail) in violations)
            {
                _output.WriteLine($"{name}: {key} {detail}");
            }

            string[] missing = violations.Keys.Except(table, StringComparer.Ordinal).ToArray();
            Assert.True(missing.Length == 0, $"{name}: an output leaves its fixed range but is not in the table: {string.Join(", ", missing)}");
        }
    }

    /// <summary>A catalog whose default settings carry the given bounds; every other member is not used by the resolver.</summary>
    private sealed class FixedSettingsCatalog : IScreenerCatalogProvider
    {
        private readonly decimal? _min;
        private readonly decimal? _max;

        public FixedSettingsCatalog(decimal? min, decimal? max)
        {
            _min = min;
            _max = max;
        }

        public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null)
            => new() { TypeEnum = type, MinValue = _min, MaxValue = _max };

        public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null) => throw new NotSupportedException();
        public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => throw new NotSupportedException();
    }
}
