using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

public class EquityAxisTicksTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // A fixed-pitch stand-in for text measurement: the tick selection only needs a width per label.
    private static double Measure(string label) => 7d * label.Length;

    [Fact]
    public void YTicks_UseRoundStepsAndKeepTheCurrencyUnit()
    {
        ImmutableArray<EquityYTick> ticks = EquityAxisTicks.YTicks(0m, 100m, 6);

        Assert.Equal(new[] { 0m, 20m, 40m, 60m, 80m, 100m }, ticks.Select(t => t.Value));
        Assert.Equal(new[] { "0", "20", "40", "60", "80", "100" }, ticks.Select(t => t.Label));
        Assert.Equal(0d, ticks[0].Fraction);
        Assert.Equal(1d, ticks[^1].Fraction);
        Assert.Equal(0.4, ticks[2].Fraction, precision: 12);
    }

    [Fact]
    public void YTicks_LabelDecimalsFollowTheStep()
    {
        ImmutableArray<EquityYTick> halves = EquityAxisTicks.YTicks(0m, 1m, 5);
        Assert.Equal(new[] { "0.0", "0.5", "1.0" }, halves.Select(t => t.Label));

        // The constant-series +/-1 band around 100000.
        ImmutableArray<EquityYTick> band = EquityAxisTicks.YTicks(99_999m, 100_001m, 5);
        Assert.Equal(new[] { "99999.0", "99999.5", "100000.0", "100000.5", "100001.0" }, band.Select(t => t.Label));
    }

    [Fact]
    public void YTicks_StartAtTheFirstRoundValueInsideTheRangeAndNeverShowNegativeZero()
    {
        ImmutableArray<EquityYTick> ticks = EquityAxisTicks.YTicks(-250m, 250m, 4);

        Assert.Equal(new[] { -200m, 0m, 200m }, ticks.Select(t => t.Value));
        Assert.Equal("0", ticks[1].Label);
        Assert.Equal(0.1, ticks[0].Fraction, precision: 12);
        Assert.Equal(0.5, ticks[1].Fraction, precision: 12);
    }

    [Fact]
    public void YTicks_AreWrittenInvariantWhateverTheCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            ImmutableArray<EquityYTick> ticks = EquityAxisTicks.YTicks(0m, 1m, 5);

            Assert.Equal(new[] { "0.0", "0.5", "1.0" }, ticks.Select(t => t.Label));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void XTicks_AreWrittenInGregorianInvariantWhateverTheCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // The Thai calendar would print 2567 for 2024 with a culture-sensitive "yyyy".
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            long start = new DateTime(2023, 10, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;
            long end = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;

            ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

            Assert.Equal(new[] { "Nov", "Dec", "2024", "Feb", "Mar" }, ticks.Select(t => t.Label));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(100, 100, 5)]
    [InlineData(200, 100, 5)]
    public void YTicks_AreEmptyForAnUnusableRequest(int yMin, int yMax, int maxCount)
    {
        Assert.Empty(EquityAxisTicks.YTicks(yMin, yMax, maxCount));
    }

    [Theory]
    [InlineData(0.0003, 0.0009, 2)]
    [InlineData(-1_234_567.89, 987_654.32, 3)]
    [InlineData(0, 1, 10)]
    [InlineData(99_999, 100_001, 2)]
    [InlineData(1, 1_000_000_000, 7)]
    public void YTicks_NeverExceedTheRequestedCountAndStayInsideTheRange(double min, double max, int maxCount)
    {
        decimal yMin = (decimal)min;
        decimal yMax = (decimal)max;

        ImmutableArray<EquityYTick> ticks = EquityAxisTicks.YTicks(yMin, yMax, maxCount);

        Assert.NotEmpty(ticks);
        Assert.True(ticks.Length <= maxCount, $"{ticks.Length} ticks for max {maxCount}");
        Assert.All(ticks, t =>
        {
            Assert.InRange(t.Value, yMin, yMax);
            Assert.InRange(t.Fraction, 0d, 1d);
        });
    }

    [Fact]
    public void YTicks_ARangeBetweenTwoRoundValues_StillGetsATick()
    {
        // 0.0003..0.0009 holds no multiple of the first step that fits (0.001); the next smaller round step puts one tick inside.
        ImmutableArray<EquityYTick> ticks = EquityAxisTicks.YTicks(0.0003m, 0.0009m, 2);

        Assert.Single(ticks);
        Assert.Equal(0.0005m, ticks[0].Value);
        Assert.Equal("0.0005", ticks[0].Label);
    }

    [Fact]
    public void YTicks_NearTheDecimalLimit_DoNotThrow()
    {
        ImmutableArray<EquityYTick> ticks = EquityAxisTicks.YTicks(decimal.MaxValue - 4m, decimal.MaxValue, 5);

        Assert.True(ticks.Length <= 5);
    }

    [Fact]
    public void XTicks_OverAMonth_AreMidnightDaysWithDateLabels()
    {
        long start = new DateTime(2024, 1, 3, 5, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2024, 2, 2, 5, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

        Assert.True(ticks.Length >= 2);
        AssertOrderedInsideAndNotOverlapping(ticks, start, end, 600d);
        Assert.All(ticks, t => Assert.Equal(TimeSpan.Zero, new DateTime(t.Ticks, DateTimeKind.Utc).TimeOfDay));
        Assert.Matches(@"^\d\d/\d\d$", ticks[0].Label);
    }

    [Fact]
    public void XTicks_OverAFewMonths_AreMonthStartsLabelledLikeTheMainChart()
    {
        long start = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2024, 5, 20, 0, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

        Assert.Equal(new[] { "Feb", "Mar", "Apr", "May" }, ticks.Select(t => t.Label));
        Assert.All(ticks, t => Assert.Equal(1, new DateTime(t.Ticks, DateTimeKind.Utc).Day));
    }

    [Fact]
    public void XTicks_AcrossAYearChange_ShowTheYearAtJanuary()
    {
        long start = new DateTime(2023, 10, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

        Assert.Equal(new[] { "Nov", "Dec", "2024", "Feb", "Mar" }, ticks.Select(t => t.Label));
    }

    [Fact]
    public void XTicks_MonthStepsGrowWithTheSpanInsteadOfFallingBackToDays()
    {
        long start = new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2022, 1, 15, 0, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 300d, Measure, 8d);

        Assert.True(ticks.Length >= 2);
        Assert.All(ticks, t =>
        {
            DateTime instant = new(t.Ticks, DateTimeKind.Utc);
            Assert.Equal(1, instant.Day);
            Assert.Matches(@"^(\d{4}|[A-Z][a-z]{2})$", t.Label);
        });
    }

    [Fact]
    public void XTicks_ARangeWithFewerThanTwoMonthStarts_FallsBackToDays()
    {
        // 2024-01-20 .. 2024-02-10 holds only one month start (Feb 1): a lone tick is no scale, so days are used.
        long start = new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2024, 2, 10, 0, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

        Assert.True(ticks.Length >= 2);
        Assert.All(ticks, t => Assert.Matches(@"^\d\d/\d\d$", t.Label));
    }

    [Fact]
    public void XTicks_OverThreeYears_UseMonthSteps_YearAtJanuaryMonthNameOtherwise()
    {
        long start = new DateTime(2021, 3, 10, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

        AssertOrderedInsideAndNotOverlapping(ticks, start, end, 600d);
        Assert.All(ticks, t => Assert.Equal(1, new DateTime(t.Ticks, DateTimeKind.Utc).Day));
        EquityXTick january = ticks.First(t => new DateTime(t.Ticks, DateTimeKind.Utc).Month == 1);
        Assert.Equal(new DateTime(january.Ticks, DateTimeKind.Utc).ToString("yyyy", Invariant), january.Label);
        EquityXTick other = ticks.First(t => new DateTime(t.Ticks, DateTimeKind.Utc).Month != 1);
        Assert.Equal(new DateTime(other.Ticks, DateTimeKind.Utc).ToString("MMM", Invariant), other.Label);
    }

    [Fact]
    public void XTicks_OverDecades_AreYearsOnJanuaryFirst()
    {
        long start = new DateTime(1995, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

        AssertOrderedInsideAndNotOverlapping(ticks, start, end, 600d);
        Assert.All(ticks, t =>
        {
            DateTime instant = new(t.Ticks, DateTimeKind.Utc);
            Assert.Equal((1, 1), (instant.Month, instant.Day));
            Assert.Matches(@"^\d{4}$", t.Label);
        });
    }

    [Fact]
    public void XTicks_WithinOneDay_UseClockLabels_AndShowTheDateAtMidnight()
    {
        long start = new DateTime(2024, 5, 6, 22, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2024, 5, 7, 6, 0, 0, DateTimeKind.Utc).Ticks;

        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(start, end, 600d, Measure, 8d);

        AssertOrderedInsideAndNotOverlapping(ticks, start, end, 600d);
        Assert.Contains(ticks, t => t.Label == "05/07");
        Assert.Contains(ticks, t => t.Label.Length == 5 && t.Label[2] == ':');
    }

    [Fact]
    public void XTicks_NarrowerPlotGetsFewerTicks()
    {
        long start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long end = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc).Ticks;

        int wide = EquityAxisTicks.XTicks(start, end, 900d, Measure, 8d).Length;
        int narrow = EquityAxisTicks.XTicks(start, end, 150d, Measure, 8d).Length;

        Assert.True(narrow < wide, $"narrow {narrow}, wide {wide}");
        Assert.True(narrow >= 1);
    }

    [Theory]
    [InlineData(100, 100, 600d)]
    [InlineData(200, 100, 600d)]
    [InlineData(100, 200, 0d)]
    [InlineData(100, 200, double.NaN)]
    public void XTicks_AreEmptyForAnEmptyRangeOrWidth(long start, long end, double width)
    {
        Assert.Empty(EquityAxisTicks.XTicks(start, end, width, Measure, 8d));
    }

    private static void AssertOrderedInsideAndNotOverlapping(
        ImmutableArray<EquityXTick> ticks, long start, long end, double plotWidth)
    {
        Assert.NotEmpty(ticks);
        double previousRight = double.NegativeInfinity;
        long previousTicks = long.MinValue;
        foreach (EquityXTick tick in ticks)
        {
            Assert.InRange(tick.Ticks, start, end);
            Assert.True(tick.Ticks > previousTicks);
            previousTicks = tick.Ticks;

            double center = (tick.Ticks - start) / (double)(end - start) * plotWidth;
            double width = Measure(tick.Label);
            Assert.True(center - (width / 2d) >= previousRight, $"label '{tick.Label}' overlaps its neighbour");
            previousRight = center + (width / 2d);
        }
    }
}
