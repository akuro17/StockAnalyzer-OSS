#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// G-1 / G1-T1: how <see cref="CoreRatiocatorIndicator"/> pairs the primary and the comparison symbol's bars.
/// Part 1 pins the values the chart and the MCP tool get today (the comparison bars arrive ALIGNED to the primary timeline by <see cref="ComparisonDataAligner"/>, so
/// pairing by position and pairing by date are the same pairing): they are written against the formula (indicators_formulas 0522-0524), with tolerance 0, and must hold before
/// and after the change of the pairing. Part 2 covers RAW comparison bars of another calendar (what the Screener hands over): the pairing must be by date, never by position.
/// </summary>
public class CoreRatiocatorAlignmentTests
{
    private const int PrimaryBars = 150;
    private const int LookbackPeriod = 5;
    private const int SmoothingPeriod = 3;
    private const int PrimarySeed = 11;
    private const int SecondarySeed = 23;
    private const decimal Scale = 100m;

    // Indexes of the primary calendar the comparison symbol does not trade (its holidays), all single days: forward fill closes them in the aligned form.
    private static readonly int[] SecondaryHolidays = { 20, 21, 60, 99, 120 };

    private static List<CandleData> Primary() => ScatterMetricTestKit.Bars(PrimaryBars, PrimarySeed);

    /// <summary>The comparison symbol's own stored bars: another random walk on the same calendar without its holidays.</summary>
    private static List<CandleData> SecondaryOwnCalendar()
    {
        List<CandleData> same = ScatterMetricTestKit.Bars(PrimaryBars, SecondarySeed);
        return same.Where((_, index) => !SecondaryHolidays.Contains(index)).ToList();
    }

    private static List<CoreCandleData> Core(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToList();

    private static List<CoreCandleData?> CoreNullable(IEnumerable<CandleData?> bars)
        => bars.Select(b => b is { } bar ? new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume) : null).ToList();

    /// <summary>The comparison bars as the chart and the MCP tool get them: aligned to the primary timeline by <see cref="ComparisonDataAligner"/>.</summary>
    private static async Task<(List<CandleData> Primary, List<CoreCandleData?> Aligned)> ChartForm()
    {
        List<CandleData> primary = Primary();
        var data = new FakeDataService(new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["PRIM"] = primary,
            ["BENCH"] = SecondaryOwnCalendar(),
        });
        ComparisonAlignedData aligned = await new ComparisonDataAligner(data).AlignAsync("PRIM", new[] { "BENCH" }, TimeFrame.D1, PrimaryBars);
        return (primary, CoreNullable(aligned.Series["BENCH"]));
    }

    private static CoreRatiocatorIndicator Indicator(RatiocatorCalculationMode mode) => new()
    {
        ComparisonSymbol = "BENCH",
        CalculationMode = mode,
        LookbackPeriod = LookbackPeriod,
        SmoothingPeriod = SmoothingPeriod,
    };

    /// <summary>The close the comparison symbol has on a primary date: its own bar, otherwise the last close carried forward (the aligner's rule); null before its first bar.</summary>
    private static List<decimal?> ExpectedBenchCloseForwardFilled(IReadOnlyList<CandleData> primary, IReadOnlyList<CandleData> secondary)
    {
        var byDate = secondary.ToDictionary(b => b.Timestamp.Date, b => b.Close);
        var result = new List<decimal?>(primary.Count);
        decimal? last = null;
        foreach (CandleData bar in primary)
        {
            if (byDate.TryGetValue(bar.Timestamp.Date, out decimal close))
            {
                last = close;
            }

            result.Add(last);
        }

        return result;
    }

    /// <summary>The close the comparison symbol has on exactly the primary date; null when it has no bar that day (no fill).</summary>
    private static List<decimal?> ExpectedBenchCloseByDate(IReadOnlyList<CandleData> primary, IReadOnlyList<CandleData> secondary)
    {
        var byDate = secondary.ToDictionary(b => b.Timestamp.Date, b => b.Close);
        return primary.Select(bar => byDate.TryGetValue(bar.Timestamp.Date, out decimal close) ? (decimal?)close : null).ToList();
    }

    /// <summary>The formula of 0522-0524 written out independently of the indicator (same operations in the same order, so decimals compare exactly).</summary>
    private static List<decimal?> ExpectedMain(IReadOnlyList<CandleData> primary, IReadOnlyList<decimal?> bench, RatiocatorCalculationMode mode)
    {
        var raw = new List<decimal?>(primary.Count);
        for (int i = 0; i < primary.Count; i++)
        {
            decimal a = primary[i].Close;
            decimal? b = bench[i];
            raw.Add(b is null || b.Value <= 0m || a < 0m ? null : a / b.Value * Scale);
        }

        switch (mode)
        {
            case RatiocatorCalculationMode.RawRatio:
                return raw;
            case RatiocatorCalculationMode.RollingRatio:
                var rolling = new List<decimal?>(raw.Count);
                for (int i = 0; i < raw.Count; i++)
                {
                    rolling.Add(i < LookbackPeriod || raw[i] is null || raw[i - LookbackPeriod] is null || raw[i - LookbackPeriod]!.Value <= 0m
                        ? null
                        : raw[i]!.Value / raw[i - LookbackPeriod]!.Value * Scale);
                }

                return rolling;
            default:
                decimal? first = raw.FirstOrDefault(r => r.HasValue);
                return raw.Select(r => r.HasValue && first.HasValue && first.Value != 0m ? (decimal?)(r.Value / first.Value * Scale) : null).ToList();
        }
    }

    private static void AssertMainAndSignal(CoreRatiocatorIndicator indicator, IReadOnlyList<decimal?> expectedMain)
    {
        Assert.Equal(expectedMain.Count, indicator.Values.Count);
        for (int i = 0; i < expectedMain.Count; i++)
        {
            Assert.True(expectedMain[i] == indicator.Values[i], $"Main[{i}]: expected {expectedMain[i]}, got {indicator.Values[i]}");
        }

        IReadOnlyList<decimal?> expectedSignal = IndicatorCalculationHelper.CalculateEmaWithNulls(expectedMain, SmoothingPeriod);
        for (int i = 0; i < expectedSignal.Count; i++)
        {
            Assert.True(expectedSignal[i] == indicator.SignalValues[i], $"Signal[{i}]: expected {expectedSignal[i]}, got {indicator.SignalValues[i]}");
        }
    }

    public static IEnumerable<object[]> Modes()
    {
        foreach (RatiocatorCalculationMode mode in Enum.GetValues<RatiocatorCalculationMode>())
        {
            yield return new object[] { mode };
        }
    }

    // ===== Part 1: the chart and MCP form (aligned comparison bars) - written against the unchanged indicator, must stay green =====

    [Fact]
    public async Task TheAlignedForm_HasTheCountAndTheDatesOfThePrimary_AndFillsTheHolidays()
    {
        (List<CandleData> primary, List<CoreCandleData?> aligned) = await ChartForm();

        Assert.Equal(primary.Count, aligned.Count);
        for (int i = 0; i < primary.Count; i++)
        {
            Assert.NotNull(aligned[i]);
            Assert.Equal(primary[i].Timestamp, aligned[i]!.Timestamp);
        }

        // The fixture really has holidays that the aligner closed by carrying the close forward.
        foreach (int holiday in SecondaryHolidays)
        {
            Assert.Equal(aligned[holiday - 1]!.Close, aligned[holiday]!.Close);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task OnTheChartForm_MainAndSignal_EqualTheFormulaExactly(RatiocatorCalculationMode mode)
    {
        (List<CandleData> primary, List<CoreCandleData?> aligned) = await ChartForm();
        CoreRatiocatorIndicator indicator = Indicator(mode);
        indicator.SetSecondaryCandles(aligned);

        indicator.Calculate(Core(primary));

        AssertMainAndSignal(indicator, ExpectedMain(primary, ExpectedBenchCloseForwardFilled(primary, SecondaryOwnCalendar()), mode));
        Assert.Contains(indicator.Values, v => v.HasValue);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task OnTheChartForm_AnEarlierStartOfTheComparisonSymbol_StillPairsTheSameDates(RatiocatorCalculationMode mode)
    {
        // The comparison symbol is listed later: the aligner leaves null bars at the head, the count still equals the primary's.
        (List<CandleData> primary, _) = await ChartForm();
        List<CandleData> lateListing = SecondaryOwnCalendar().Where(b => b.Timestamp >= primary[10].Timestamp).ToList();
        var data = new FakeDataService(new Dictionary<string, IReadOnlyList<CandleData>> { ["PRIM"] = primary, ["BENCH"] = lateListing });
        ComparisonAlignedData aligned = await new ComparisonDataAligner(data).AlignAsync("PRIM", new[] { "BENCH" }, TimeFrame.D1, PrimaryBars);
        CoreRatiocatorIndicator indicator = Indicator(mode);
        indicator.SetSecondaryCandles(CoreNullable(aligned.Series["BENCH"]));

        indicator.Calculate(Core(primary));

        AssertMainAndSignal(indicator, ExpectedMain(primary, ExpectedBenchCloseForwardFilled(primary, lateListing), mode));
    }

    [Fact]
    public void TheExplicitSeriesForm_IsUnaffected()
    {
        List<CandleData> primary = Primary();
        List<decimal?> explicitB = ExpectedBenchCloseForwardFilled(primary, SecondaryOwnCalendar());
        var indicator = new CoreRatiocatorIndicator(LookbackPeriod, SmoothingPeriod, explicitB) { CalculationMode = RatiocatorCalculationMode.RawRatio };

        indicator.Calculate(Core(primary));

        AssertMainAndSignal(indicator, ExpectedMain(primary, explicitB, RatiocatorCalculationMode.RawRatio));
    }

    // ===== Part 2: RAW comparison bars of another calendar (the Screener hands over the stored bars): paired by DATE =====

    [Theory]
    [MemberData(nameof(Modes))]
    public void RawBarsWithHolidays_ArePairedByDate_NotByPosition_AndAHolidayGivesNoValue(RatiocatorCalculationMode mode)
    {
        List<CandleData> primary = Primary();
        List<CandleData> raw = SecondaryOwnCalendar();
        Assert.True(raw.Count < primary.Count, "the fixture needs a different count");
        CoreRatiocatorIndicator indicator = Indicator(mode);
        indicator.SetSecondaryCandles(CoreNullable(raw.Select(b => (CandleData?)b)));

        indicator.Calculate(Core(primary));

        AssertMainAndSignal(indicator, ExpectedMain(primary, ExpectedBenchCloseByDate(primary, raw), mode));
    }

    [Fact]
    public void RawBarsThatRunPastThePrimaryEnd_AreCutAtThePrimaryDates_NotShiftedByTheExtraBars()
    {
        List<CandleData> primary = Primary();
        List<CandleData> raw = ScatterMetricTestKit.Bars(PrimaryBars + 7, SecondarySeed);
        CoreRatiocatorIndicator indicator = Indicator(RatiocatorCalculationMode.RawRatio);
        indicator.SetSecondaryCandles(CoreNullable(raw.Select(b => (CandleData?)b)));

        indicator.Calculate(Core(primary));

        AssertMainAndSignal(indicator, ExpectedMain(primary, ExpectedBenchCloseByDate(primary, raw), RatiocatorCalculationMode.RawRatio));
    }

    // ----- Head and no-overlap: told apart from position pairing by the values themselves (R-2) -----

    private static List<CoreCandleData> FourDays(DateTime start, params decimal[] closes)
        => closes.Select((close, i) => new CoreCandleData(start.AddDays(i), close, close + 1, close - 1, close, 1000)).ToList();

    [Fact]
    public void ASecondaryThatHasOnlyTheFirstDates_GivesValuesAtTheHead_NotAtTheTail()
    {
        List<CoreCandleData> primary = FourDays(new DateTime(2020, 1, 1), 100m, 120m, 140m, 160m);
        List<CoreCandleData?> secondary = FourDays(new DateTime(2020, 1, 1), 50m, 60m).Cast<CoreCandleData?>().ToList();
        CoreRatiocatorIndicator indicator = Indicator(RatiocatorCalculationMode.RawRatio);
        indicator.SetSecondaryCandles(secondary);

        indicator.Calculate(primary);

        // By date: 100/50 and 120/60 on the first two days. By position (end-aligned) the same two bars would have paired with 140 and 160 (280 and 266.67).
        Assert.Equal(new decimal?[] { 200m, 200m, null, null }, indicator.Values.ToArray());
    }

    [Fact]
    public void ASecondaryThatHasOnlyTheLastDates_GivesValuesAtTheTail()
    {
        List<CoreCandleData> primary = FourDays(new DateTime(2020, 1, 1), 100m, 120m, 140m, 160m);
        List<CoreCandleData?> secondary = FourDays(new DateTime(2020, 1, 3), 70m, 80m).Cast<CoreCandleData?>().ToList();
        CoreRatiocatorIndicator indicator = Indicator(RatiocatorCalculationMode.RawRatio);
        indicator.SetSecondaryCandles(secondary);

        indicator.Calculate(primary);

        Assert.Equal(new decimal?[] { null, null, 200m, 200m }, indicator.Values.ToArray());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ASecondaryWithNoDateInCommon_GivesNoValueAtAll_InEveryMode(RatiocatorCalculationMode mode)
    {
        List<CoreCandleData> primary = FourDays(new DateTime(2020, 1, 1), 100m, 120m, 140m, 160m);
        // Same count, same shape, a year later: by position every bar would pair; by date none does.
        List<CoreCandleData?> secondary = FourDays(new DateTime(2021, 1, 1), 50m, 60m, 70m, 80m).Cast<CoreCandleData?>().ToList();
        CoreRatiocatorIndicator indicator = Indicator(mode);
        indicator.SetSecondaryCandles(secondary);

        indicator.Calculate(primary);

        Assert.Equal(primary.Count, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
        Assert.All(indicator.SignalValues, v => Assert.Null(v));
    }

    [Fact]
    public void RawBarsThatStartLater_AreNullAtTheHead_AndPairedByDateAfterwards()
    {
        List<CandleData> primary = Primary();
        List<CandleData> raw = ScatterMetricTestKit.Bars(PrimaryBars, SecondarySeed).Skip(10).ToList();
        CoreRatiocatorIndicator indicator = Indicator(RatiocatorCalculationMode.RawRatio);
        indicator.SetSecondaryCandles(CoreNullable(raw.Select(b => (CandleData?)b)));

        indicator.Calculate(Core(primary));

        AssertMainAndSignal(indicator, ExpectedMain(primary, ExpectedBenchCloseByDate(primary, raw), RatiocatorCalculationMode.RawRatio));
        Assert.All(indicator.Values.Take(10), v => Assert.Null(v));
    }
}
