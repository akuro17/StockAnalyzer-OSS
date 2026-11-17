using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Indicator Timeframe, Sprint 7 / M4: the values of an indicator with its own Weekly / Monthly frame against an oracle built WITHOUT the production aggregation and alignment:
/// the higher-frame bars are grouped here (ISO week / calendar month), the first group is dropped, the SAME indicator is run on them as an ordinary chart of that frame, and the value
/// at a daily bar is read by hand from the previous closed group. Also: inputs are never changed, every registered type is classified consistently with the policy, and the
/// order of <see cref="TimeFrame"/> that the plan's comparisons rely on is pinned. Real pipeline, indicators and candles only; no mocks.
/// </summary>
public sealed class IndicatorTimeframeDifferentialTests
{
    private const int DailyCount = 398; // 79 complete ISO weeks and a running week of three days, about 18 calendar months
    private static readonly DateTime FirstMonday = new(2023, 1, 2);
    private static readonly AnalysisPipelineService Service = new(pythonService: null, indicatorFactory: IndicatorFactory.Default);

    private static List<CoreCandleData> DailyCandles(int count = DailyCount)
    {
        var list = new List<CoreCandleData>(count);
        var date = FirstMonday;
        for (int i = 0; i < count; i++)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                date = date.AddDays(1);
            }

            // Distinct, non-monotonic prices with a daily range and a varying volume, so that no indicator is degenerate.
            decimal close = 100m + (i * 7 % 23) + (i * 0.1m) + ((i % 5) * 0.37m);
            list.Add(new CoreCandleData(date, close - 0.5m, close + 1m + (i % 3), close - 1m - (i % 4), close, 1000 + (i * 13 % 97)));
            date = date.AddDays(1);
        }

        return list;
    }

    private static CoreIndicatorSettings FromDefault(string id, IndicatorType type, IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart)
    {
        var setting = IndicatorFactory.CreateStatic(type)!.GetDefaultSettings();
        setting.Id = id;
        setting.TypeEnum = type;
        setting.IsEnabled = true;
        setting.Timeframe = timeframe;
        return setting;
    }

    private static CoreIndicatorSettings WithPeriod(string id, IndicatorType type, int period, IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart)
    {
        var setting = new CoreIndicatorSettings { Id = id, TypeEnum = type, IsEnabled = true };
        setting.ParameterObject = type == IndicatorType.SMA ? new CoreSmaParameter { Period = period } : new CoreEmaParameter { Period = period };
        setting.Timeframe = timeframe;
        return setting;
    }

    private static Task<Dictionary<string, IIndicatorResult>> RunAsync(IReadOnlyList<CoreCandleData> candles, TimeFrame chartFrame, CoreIndicatorSettings settings)
        => Service.CalculateIndicatorsAsync(candles, new[] { settings }, null, chartFrame, CancellationToken.None);

    // ---- the oracle (independent of TimeFrameAggregator and TimeframeAlignment) -------------------------------------------------------

    /// <summary>The group index of every daily bar (0 = the first period of the data) and the aggregated bars of every group.</summary>
    private static (int[] GroupOfBar, List<CoreCandleData> Groups) Group(IReadOnlyList<CoreCandleData> daily, bool weekly)
    {
        static (int, int) KeyOf(DateTime d, bool weekly) => weekly ? (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d)) : (d.Year, d.Month);

        var groupOfBar = new int[daily.Count];
        var groups = new List<CoreCandleData>();
        (int, int)? current = null;
        for (int i = 0; i < daily.Count; i++)
        {
            var key = KeyOf(daily[i].Timestamp, weekly);
            var c = daily[i];
            if (current != key)
            {
                groups.Add(c);
                current = key;
            }
            else
            {
                var g = groups[^1];
                groups[^1] = g with { High = Math.Max(g.High, c.High), Low = Math.Min(g.Low, c.Low), Close = c.Close, Volume = g.Volume + c.Volume };
            }

            groupOfBar[i] = groups.Count - 1;
        }

        return (groupOfBar, groups);
    }

    /// <summary>The expected series on the daily bars: at a bar of group g the value of the higher-frame series at the bar of group g-1, whose index is g-2 once group 0 is dropped.</summary>
    private static async Task<Dictionary<string, decimal?[]>> OracleAsync(IReadOnlyList<CoreCandleData> daily, bool weekly, Func<CoreIndicatorSettings> make)
    {
        var (groupOfBar, groups) = Group(daily, weekly);
        var closed = groups.Skip(1).ToList(); // the leading period may be incomplete: it is never used
        var reference = (await RunAsync(closed, weekly ? TimeFrame.W1 : TimeFrame.MN1, make()))["x"];
        Assert.True(reference.IsSuccessful, reference.ErrorMessage);

        var expected = new Dictionary<string, decimal?[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in reference.SeriesNamesList)
        {
            var series = reference.GetSeries(name);
            var values = new decimal?[daily.Count];
            for (int i = 0; i < daily.Count; i++)
            {
                int k = groupOfBar[i] - 2;
                values[i] = k >= 0 && k < closed.Count && k < series.Count ? series[k] : null;
            }

            expected[name] = values;
        }

        return expected;
    }

    private static void AssertEqualsOracle(IIndicatorResult actual, Dictionary<string, decimal?[]> expected)
    {
        Assert.True(actual.IsSuccessful, actual.ErrorMessage);
        Assert.Equal(expected.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(), actual.SeriesNamesList.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
        foreach (var (name, values) in expected)
        {
            var series = actual.GetSeries(name);
            Assert.Equal(values.Length, series.Count);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.True(values[i] == series[i], $"{name}[{i}]: expected {values[i]} but was {series[i]}");
            }

            Assert.Contains(values, v => v.HasValue); // not vacuous: the oracle carries values
        }
    }

    // ---- values ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(IndicatorType.EMA)]
    [InlineData(IndicatorType.RSI)]
    [InlineData(IndicatorType.MACD)]
    [InlineData(IndicatorType.BB)]
    [InlineData(IndicatorType.Ichimoku)]
    [InlineData(IndicatorType.SMA)]
    public async Task WeeklyIndicator_EqualsTheHandBuiltOracle_ForEveryNamedSeries(IndicatorType type)
    {
        var daily = DailyCandles();
        var expected = await OracleAsync(daily, weekly: true, () => FromDefault("x", type));

        var result = (await RunAsync(daily, TimeFrame.D1, FromDefault("x", type, IndicatorTimeframe.Weekly)))["x"];

        AssertEqualsOracle(result, expected);
    }

    [Theory]
    [InlineData(IndicatorType.SMA, 3)]
    [InlineData(IndicatorType.EMA, 3)]
    public async Task MonthlyIndicator_EqualsTheHandBuiltOracle_OnCalendarMonths(IndicatorType type, int period)
    {
        var daily = DailyCandles();
        var expected = await OracleAsync(daily, weekly: false, () => WithPeriod("x", type, period));

        var result = (await RunAsync(daily, TimeFrame.D1, WithPeriod("x", type, period, IndicatorTimeframe.Monthly)))["x"];

        AssertEqualsOracle(result, expected);
    }

    [Fact]
    public async Task MonthlySma3_AtAnExactBar_IsTheMeanOfTheThreePreviousClosedCalendarMonthCloses()
    {
        var daily = DailyCandles();
        var (groupOfBar, groups) = Group(daily, weekly: false);
        var result = (await RunAsync(daily, TimeFrame.D1, WithPeriod("x", IndicatorType.SMA, 3, IndicatorTimeframe.Monthly)))["x"].MainValues;

        // The bar index of the first day of the 7th month (group 6): the previous closed months are groups 3, 4, 5 (group 0 is dropped, so groups 1.. are the usable ones).
        int bar = Array.IndexOf(groupOfBar, 6);
        decimal expected = (groups[3].Close + groups[4].Close + groups[5].Close) / 3m;

        Assert.Equal(expected, result[bar]);
        Assert.Null(result[Array.IndexOf(groupOfBar, 3)]); // three closed usable months do not exist yet
        Assert.Equal(DateTime.Parse("2023-07-03", CultureInfo.InvariantCulture), daily[bar].Timestamp); // the pinned calendar: group 6 starts on the first trading day of July 2023
    }

    // ---- inputs, classification, order ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRegisteredType_WithAWeeklyTimeframe_NeverChangesItsInput_AndNeverThrows()
    {
        var daily = DailyCandles();
        var snapshot = daily.ToArray();
        int calculated = 0;
        int total = 0;
        var offenders = new List<string>();

        foreach (var type in IndicatorFactory.Default.GetRegisteredTypes().Distinct().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            var indicator = IndicatorFactory.CreateStatic(type);
            if (indicator is null)
            {
                continue;
            }

            total++;
            var setting = FromDefault("x", type, IndicatorTimeframe.Weekly);
            try
            {
                var result = (await RunAsync(daily, TimeFrame.D1, setting))["x"];
                if (result.IsSuccessful)
                {
                    calculated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                offenders.Add($"{type}: {ex.GetType().Name}: {ex.Message}");
            }

            if (!daily.SequenceEqual(snapshot))
            {
                offenders.Add($"{type}: the input candles were changed");
                daily = snapshot.ToList();
            }
        }

        Assert.Empty(offenders);
        // Most types must calculate on this data, or "no exception, input unchanged" would be checked on failures only (the share, not a count, so a new type needs no edit here).
        Assert.True(calculated * 2 >= total, $"only {calculated} of {total} types calculated: the check would be mostly vacuous");
    }

    [Fact]
    public void EveryRegisteredType_IsClassifiedConsistentlyWithThePolicy()
    {
        var none = new HashSet<string>();
        int supported = 0, notApplicable = 0, selfHandled = 0, total = 0;

        foreach (var type in IndicatorFactory.Default.GetRegisteredTypes().Distinct())
        {
            total++;
            var setting = FromDefault("x", type, IndicatorTimeframe.Weekly);
            var plan = IndicatorTimeframePlan.Classify(setting, TimeFrame.D1, none);

            switch (IndicatorTimeframePolicy.Resolve(type, setting.ParameterObject))
            {
                case IndicatorTimeframeSupport.Supported:
                    supported++;
                    Assert.True(plan.Mode == IndicatorTimeframeMode.Aligned && plan.EffectiveFrame == TimeFrame.W1, $"{type}: {plan.Mode}");
                    break;
                case IndicatorTimeframeSupport.NotApplicable:
                    notApplicable++;
                    Assert.True(plan.Mode == IndicatorTimeframeMode.NotApplicable, $"{type}: {plan.Mode}");
                    break;
                case IndicatorTimeframeSupport.SelfHandled:
                    selfHandled++;
                    Assert.True(plan.Mode == IndicatorTimeframeMode.None, $"{type}: {plan.Mode}"); // a Pivot on a daily chart with its default source period
                    break;
            }

            // Same as Chart is never planned as anything but the chart's own calculation (a Pivot may still refuse its own finer source period, which is not a Timeframe setting).
            var same = FromDefault("x", type);
            Assert.False(IndicatorTimeframePlan.NeedsPlan(same) && IndicatorTimeframePlan.Classify(same, TimeFrame.D1, none).Mode == IndicatorTimeframeMode.Aligned, $"{type}");
        }

        Assert.Equal(total, supported + notApplicable + selfHandled); // every registered type fell into exactly one class
        Assert.True(supported > 0 && notApplicable > 0, $"supported {supported}, not applicable {notApplicable}: both classes must be exercised");
        Assert.Equal(2, selfHandled); // the two Pivot Points
    }

    [Fact]
    public void TimeFrame_OrderAndValuesAreFixed_BecauseThePlanComparesFramesWithLessThan()
    {
        TimeFrame[] ascending = { TimeFrame.M1, TimeFrame.M5, TimeFrame.M15, TimeFrame.M30, TimeFrame.H1, TimeFrame.H4, TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 };

        Assert.Equal(ascending, ascending.OrderBy(f => (int)f).ToArray());
        Assert.Equal(Enumerable.Range(0, 9), ascending.Select(f => (int)f));
        Assert.True(TimeFrame.D1 < TimeFrame.W1 && TimeFrame.W1 < TimeFrame.MN1);
        Assert.Equal(ascending, Enum.GetValues<TimeFrame>());
    }
}
