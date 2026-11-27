using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The price a range tool works on (settings expansion Runs 3 and 4): the shared range price series, the GARCH cone price source and non-positive prices, the Robust period and price source.</summary>
public sealed class RangePriceSourceTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    /// <summary>Bars with a distinct open, high, low and close (so all 15 price types differ), generated without a clock seed.</summary>
    private static List<CoreCandleData> Bars(int count)
    {
        var list = new List<CoreCandleData>(count);
        double state = 4711.0;
        decimal close = 100m;
        for (int i = 0; i < count; i++)
        {
            state = ((state * 1103515245.0) + 12345.0) % 2147483648.0;
            double u1 = state / 2147483648.0;
            state = ((state * 1103515245.0) + 12345.0) % 2147483648.0;
            double u2 = state / 2147483648.0;
            decimal open = close;
            close = Math.Round(close * (decimal)Math.Exp((u1 - 0.5) * 0.04), 4);
            decimal high = Math.Max(open, close) + Math.Round((decimal)u2 * 1.5m, 4) + 0.1m;
            decimal low = Math.Min(open, close) - Math.Round((decimal)(1.0 - u2) * 1.5m, 4) - 0.1m;
            list.Add(new CoreCandleData(Start.AddDays(i), open, high, low, close, 1000L + i));
        }

        return list;
    }

    private static List<CoreCandleData> WithCloses(IEnumerable<decimal> closes)
    {
        var list = new List<CoreCandleData>();
        int i = 0;
        foreach (decimal close in closes)
        {
            list.Add(new CoreCandleData(Start.AddDays(i++), close, close, close, close, 1000L));
        }

        return list;
    }

    private static double Mean(decimal[] values) => values.Select(v => (double)v).Average();

    private static double Median(decimal[] values)
    {
        double[] sorted = values.Select(v => (double)v).OrderBy(v => v).ToArray();
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }

    public static TheoryData<PriceType> AllPriceTypes()
    {
        var data = new TheoryData<PriceType>();
        foreach (PriceType type in Enum.GetValues<PriceType>())
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void AllFifteenPriceTypesAreCovered()
    {
        Assert.Equal(15, Enum.GetValues<PriceType>().Length);
    }

    [Theory]
    [MemberData(nameof(AllPriceTypes))]
    public void Series_HasTheSameMeanMedianAndSpreadAsTheDistributionRange(PriceType type)
    {
        List<CoreCandleData> candles = Bars(120);
        const int first = 30;
        const int last = 89;

        decimal[] series = DrawingRangePriceSeries.Extract(candles, first, last, type);
        DistributionRangeResult reference = DistributionRangeCalculator.Calculate(
            candles,
            candles[first].Timestamp,
            candles[last].Timestamp,
            new DistributionRangeParameters { DataMode = DistributionDataMode.Price, PriceSource = type });

        Assert.Equal(last - first + 1, series.Length);
        Assert.Equal(series.Length, reference.SampleCount);
        Assert.True(Math.Abs(Mean(series) - (double)reference.Mean!.Value) < 1e-5, $"{type} mean");
        Assert.True(Math.Abs(Median(series) - (double)reference.Median!.Value) < 1e-5, $"{type} median");
        double mean = Mean(series);
        double sampleStdDev = Math.Sqrt(series.Sum(v => Math.Pow((double)v - mean, 2)) / (series.Length - 1));
        double populationStdDev = Math.Sqrt(series.Sum(v => Math.Pow((double)v - mean, 2)) / series.Length);
        double actual = (double)reference.StdDev!.Value;
        Assert.True(Math.Abs(actual - sampleStdDev) < 1e-6 || Math.Abs(actual - populationStdDev) < 1e-6, $"{type}: {actual} vs {sampleStdDev} / {populationStdDev}");
    }

    [Theory]
    [MemberData(nameof(AllPriceTypes))]
    public void Series_NeverReadsTheBarBeforeTheRange(PriceType type)
    {
        List<CoreCandleData> candles = Bars(120);
        decimal[] before = DrawingRangePriceSeries.Extract(candles, 30, 89, type);

        candles[29] = new CoreCandleData(candles[29].Timestamp, 9999m, 99999m, 0.01m, 12345m, 1L);
        decimal[] after = DrawingRangePriceSeries.Extract(candles, 30, 89, type);

        Assert.Equal(before, after);
    }

    [Fact]
    public void Series_FirstBarHasNoPreviousClose_ForTheTrueRangePrices()
    {
        List<CoreCandleData> candles = Bars(10);
        decimal[] high = DrawingRangePriceSeries.Extract(candles, 3, 6, PriceType.TrueHigh);
        decimal[] low = DrawingRangePriceSeries.Extract(candles, 3, 6, PriceType.TrueLow);

        Assert.Equal(candles[3].High, high[0]);
        Assert.Equal(candles[3].Low, low[0]);
        Assert.Equal(Math.Max(candles[4].High, candles[3].Close), high[1]);
        Assert.Equal(Math.Min(candles[4].Low, candles[3].Close), low[1]);
    }

    [Fact]
    public void Series_Close_IsTheCloses_AndAnInvalidRangeIsEmpty()
    {
        List<CoreCandleData> candles = Bars(10);
        Assert.Equal(candles.Skip(2).Take(5).Select(c => c.Close).ToArray(), DrawingRangePriceSeries.Extract(candles, 2, 6, PriceType.Close));
        Assert.Empty(DrawingRangePriceSeries.Extract(candles, 6, 2, PriceType.Close));
        Assert.Empty(DrawingRangePriceSeries.Extract(candles, -1, 2, PriceType.Close));
        Assert.Empty(DrawingRangePriceSeries.Extract(candles, 2, 10, PriceType.Close));
    }

    [Fact]
    public void Garch_DefaultPriceIsTheClose_AndTheCentreIsTheSelectedPriceOfTheLastBar()
    {
        List<CoreCandleData> candles = Bars(200);
        GarchVolatilityCone viaDefault = GarchVolatilityConeValues.Calculate(candles, 20, 150, 20);
        GarchVolatilityCone viaClose = GarchVolatilityConeValues.Calculate(candles, 20, 150, 20, PriceType.Close);
        Assert.True(viaDefault.IsOk);
        Assert.Equal(candles[150].Close, viaDefault.CenterPrice);
        Assert.Equal(viaDefault.Upper2, viaClose.Upper2);
        Assert.Equal(viaDefault.Lower1, viaClose.Lower1);

        GarchVolatilityCone median = GarchVolatilityConeValues.Calculate(candles, 20, 150, 20, PriceType.Median);
        Assert.True(median.IsOk);
        Assert.Equal((candles[150].High + candles[150].Low) / 2m, median.CenterPrice);
        Assert.NotEqual(viaDefault.Upper2[19], median.Upper2[19]);
    }

    [Theory]
    [MemberData(nameof(AllPriceTypes))]
    public void Garch_EveryPriceTypeGivesAConeWithFiniteBands(PriceType type)
    {
        List<CoreCandleData> candles = Bars(200);
        GarchVolatilityCone cone = GarchVolatilityConeValues.Calculate(candles, 20, 150, 20, type);
        Assert.True(cone.IsOk, type.ToString());
        Assert.All(cone.Upper2, v => Assert.True(v.HasValue));
        Assert.All(cone.Lower2, v => Assert.True(v.HasValue));
    }

    [Fact]
    public void Garch_TheBarBeforeTheRangeIsNeverRead_ForAHeikinAshiPrice()
    {
        List<CoreCandleData> candles = Bars(200);
        GarchVolatilityCone before = GarchVolatilityConeValues.Calculate(candles, 40, 150, 20, PriceType.HeikinAshiClose);
        candles[39] = new CoreCandleData(candles[39].Timestamp, 5m, 9000m, 0.5m, 6000m, 1L);
        GarchVolatilityCone after = GarchVolatilityConeValues.Calculate(candles, 40, 150, 20, PriceType.HeikinAshiClose);

        Assert.True(before.IsOk);
        Assert.Equal(before.Upper2, after.Upper2);
        Assert.Equal(before.CenterPrice, after.CenterPrice);
    }

    [Theory]
    [InlineData(-5.0, 0)]    // the first bar of the range
    [InlineData(-5.0, 30)]   // somewhere in the middle
    [InlineData(0.0, 30)]    // zero
    [InlineData(-5.0, 99)]   // the last bar only
    public void Garch_APriceThatIsNotAboveZero_GivesAStatus_NotAnExceptionOrANaN(double price, int index)
    {
        List<CoreCandleData> candles = Bars(100);
        decimal value = (decimal)price;
        candles[index] = new CoreCandleData(candles[index].Timestamp, value, value, value, value, 1L);

        GarchVolatilityCone cone = GarchVolatilityConeValues.Calculate(candles, 0, 99, 10);

        Assert.Equal(GarchConeStatus.NonPositivePrice, cone.Status);
        Assert.False(cone.IsOk);
        Assert.Empty(cone.Upper2);
        Assert.Equal("Prices must be above zero", GarchVolatilityConeValues.StatusText(cone.Status));
        IReadOnlyList<DrawingCalculatedValue> rows = GarchVolatilityConeValues.Calculate(cone, IndicatorColor.Gray);
        Assert.Equal("State", rows[0].Key);
        Assert.Equal("Prices must be above zero", rows[0].FormattedText);
        Assert.DoesNotContain(rows, r => r.NumericValue.HasValue && r.Key.StartsWith("Garch", StringComparison.Ordinal));
    }

    [Fact]
    public void Garch_ANegativeSelectedPriceInTheWindowOnly_StillCountsAndANegativeBarOutsideTheWindowDoesNot()
    {
        int total = GarchVolatilityConeValues.MaxBars + 100;
        int last = total - 1;
        List<CoreCandleData> candles = Bars(total);
        candles[10] = new CoreCandleData(candles[10].Timestamp, -9m, -9m, -9m, -9m, 1L);

        // The range is longer than the model takes: only the newest bars are fitted, so a bar outside that window is not read.
        GarchVolatilityCone outside = GarchVolatilityConeValues.Calculate(candles, 0, last, 10);
        Assert.True(outside.IsTruncated, $"{outside.Status} {outside.RangeBars}/{outside.EffectiveBars}");
        Assert.True(outside.IsOk, outside.Status.ToString());

        candles[last - 10] = new CoreCandleData(candles[last - 10].Timestamp, -9m, -9m, -9m, -9m, 1L);
        Assert.Equal(GarchConeStatus.NonPositivePrice, GarchVolatilityConeValues.Calculate(candles, 0, last, 10).Status);
    }

    [Fact]
    public void Garch_AMedianThatIsAboveZeroIsFine_EvenWhenALowIsNot()
    {
        List<CoreCandleData> candles = Bars(100);
        CoreCandleData c = candles[50];
        candles[50] = new CoreCandleData(c.Timestamp, c.Open, c.High + 30m, -1m, c.Close, c.Volume);

        Assert.True(GarchVolatilityConeValues.Calculate(candles, 0, 99, 10, PriceType.Median).IsOk);
        Assert.Equal(GarchConeStatus.NonPositivePrice, GarchVolatilityConeValues.Calculate(candles, 0, 99, 10, PriceType.Low).Status);
    }

    [Fact]
    public void Garch_AnchorOverload_PassesThePriceSource()
    {
        List<CoreCandleData> candles = Bars(200);
        var a = new DrawingAnchor(candles[20].Timestamp, 100m);
        var b = new DrawingAnchor(candles[150].Timestamp, 100m);

        IReadOnlyList<DrawingCalculatedValue> viaAnchors = GarchVolatilityConeValues.Calculate(a, b, candles, 20, IndicatorColor.Gray, PriceType.Median);
        IReadOnlyList<DrawingCalculatedValue> direct = GarchVolatilityConeValues.Calculate(GarchVolatilityConeValues.Calculate(candles, 20, 150, 20, PriceType.Median), IndicatorColor.Gray);
        Assert.Equal(direct, viaAnchors);
        Assert.NotEqual(GarchVolatilityConeValues.Calculate(a, b, candles, 20, IndicatorColor.Gray), viaAnchors);
    }

    [Fact]
    public void Options_GarchPriceSource_DefaultsToCloseAndIsReadFromTheStoredKey_WithoutChangingTheOtherTools()
    {
        Assert.Equal(PriceType.Close, new DrawingValueOptions().GarchPriceSource);
        Assert.Equal(PriceType.Typical, new DrawingValueOptions().PriceSource);
        Assert.Equal(PriceType.Median, new DrawingValueOptions(GarchPriceSource: PriceType.Median).GarchPriceSource);

        var stored = new Dictionary<string, object?>
        {
            [DrawingStoredOptions.PriceSourceKey] = System.Text.Json.JsonDocument.Parse("\"HeikinAshiClose\"").RootElement.Clone(),
        };
        DrawingValueOptions read = DrawingStoredOptions.Read(stored);
        Assert.Equal(PriceType.HeikinAshiClose, read.GarchPriceSource);
        Assert.Equal(PriceType.HeikinAshiClose, read.PriceSource);
        Assert.Equal(PriceType.HeikinAshiClose, read.DistributionPriceSource);

        DrawingValueOptions empty = DrawingStoredOptions.Read(new Dictionary<string, object?>());
        Assert.Equal(PriceType.Close, empty.GarchPriceSource);
        Assert.Equal(PriceType.Typical, empty.PriceSource);
        Assert.Equal(PriceType.Close, empty.DistributionPriceSource);
    }

    // ----- Robust Regression Trend -----

    [Fact]
    public void Robust_DefaultPeriodAndPriceReproduceTheOldFit()
    {
        List<CoreCandleData> candles = Bars(80);
        RangeLineFit old = RobustRegressionTrendValues.Calculate(candles, 10, 70, false, 2m, false);
        RangeLineFit explicitDefaults = RobustRegressionTrendValues.Calculate(candles, 10, 70, false, 2m, false, RobustRegressionTrendValues.DefaultPeriod, PriceType.Close);

        Assert.Equal(RobustRegressionMath.MaxWindowLength, RobustRegressionTrendValues.DefaultPeriod);   // the default stays the old fixed 30 although the maximum is larger (DR-1)
        Assert.Equal(30, old.EffectiveBars);
        Assert.Equal(old.Slope, explicitDefaults.Slope);
        Assert.Equal(old.Intercept, explicitDefaults.Intercept);
        Assert.Equal(old.Sigma, explicitDefaults.Sigma);
        Assert.Equal(old.EffectiveBars, explicitDefaults.EffectiveBars);
    }

    [Fact]
    public void Robust_PeriodIsTheNumberOfNewestBarsFitted()
    {
        List<CoreCandleData> candles = Bars(80);
        RangeLineFit period12 = RobustRegressionTrendValues.Calculate(candles, 10, 70, false, 2m, false, 12);
        RangeLineFit lastTwelve = RobustRegressionTrendValues.Calculate(candles, 59, 70, false, 2m, false);

        Assert.Equal(12, period12.EffectiveBars);
        Assert.Equal(61, period12.RangeBars);
        Assert.True(period12.IsTruncated);
        Assert.Equal(lastTwelve.Slope, period12.Slope);
        Assert.Equal(lastTwelve.Intercept, period12.Intercept);
        Assert.Equal(lastTwelve.Sigma, period12.Sigma);
    }

    [Fact]
    public void Robust_AShortRange_IsNotCutByALargerPeriod()
    {
        List<CoreCandleData> candles = Bars(80);
        RangeLineFit fit = RobustRegressionTrendValues.Calculate(candles, 20, 30, false, 2m, false, 25);
        Assert.Equal(11, fit.RangeBars);
        Assert.Equal(11, fit.EffectiveBars);
        Assert.False(fit.IsTruncated);
    }

    [Theory]
    [InlineData(-4, RobustRegressionMath.MinWindowLength)]
    [InlineData(0, RobustRegressionMath.MinWindowLength)]
    [InlineData(1, RobustRegressionMath.MinWindowLength)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(30, 30)]
    [InlineData(31, 31)]
    [InlineData(250, 250)]
    [InlineData(RobustRegressionMath.HardMaxWindowLength, RobustRegressionMath.HardMaxWindowLength)]
    [InlineData(RobustRegressionMath.HardMaxWindowLength + 1, RobustRegressionMath.HardMaxWindowLength)]
    [InlineData(5000, RobustRegressionMath.HardMaxWindowLength)]
    public void Robust_PeriodIsClampedToTheToolLimits(int period, int expectedBars)
    {
        List<CoreCandleData> candles = Bars(RobustRegressionMath.HardMaxWindowLength + 200);
        RangeLineFit fit = RobustRegressionTrendValues.Calculate(candles, 0, candles.Count - 1, false, 2m, false, period);
        Assert.True(fit.IsOk);
        Assert.Equal(expectedBars, fit.EffectiveBars);
    }

    [Fact]
    public void Robust_LimitsAreTheKernelConstants_NotCopies()
    {
        Assert.Equal(RobustRegressionMath.MinWindowLength, RobustRegressionTrendValues.MinBars);
        Assert.Equal(RobustRegressionMath.HardMaxWindowLength, RobustRegressionTrendValues.MaxBars);
        Assert.Equal(500, RobustRegressionTrendValues.MaxBars);   // the measured owner limit (step log, 2026-11-27)
        Assert.True(RobustRegressionTrendValues.DefaultPeriod <= RobustRegressionTrendValues.MaxBars);
    }

    [Theory]
    [InlineData(false, 31)]
    [InlineData(true, 31)]
    [InlineData(false, 250)]
    [InlineData(true, 250)]
    [InlineData(false, RobustRegressionMath.HardMaxWindowLength)]
    [InlineData(true, RobustRegressionMath.HardMaxWindowLength)]
    public void Robust_ALongWindow_IsTheSameLineAsTheKernelFitsDirectly(bool siegel, int period)
    {
        List<CoreCandleData> candles = Bars(RobustRegressionMath.HardMaxWindowLength + 50);
        int last = candles.Count - 1;
        RangeLineFit fit = RobustRegressionTrendValues.Calculate(candles, 0, last, siegel, 2m, false, period);

        decimal[] window = candles.Skip(last - period + 1).Select(c => c.Close).ToArray();
        decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(period, siegel)];
        Assert.True(RobustRegressionMath.TryFit(window, siegel, scratch, RobustRegressionMath.HardMaxWindowLength, out RobustLine line));

        Assert.True(fit.IsOk);
        Assert.Equal(period, fit.EffectiveBars);
        Assert.Equal(line.Slope, fit.Slope);
        Assert.Equal(line.Intercept, fit.Intercept);
        Assert.Equal(line.FittedLast, fit.FittedLast);
        Assert.Equal(line.ResidualSigma, fit.Sigma);
    }

    [Fact]
    public void Robust_ALongWindow_ReturnsItsBorrowedScratch_SoTheNextCallIsNotAffectedByDirtyMemory()
    {
        List<CoreCandleData> candles = Bars(RobustRegressionMath.HardMaxWindowLength + 50);
        int last = candles.Count - 1;
        RangeLineFit first = RobustRegressionTrendValues.Calculate(candles, 0, last, false, 2m, false, 300);

        // Dirty the shared pool with garbage of the same size class, then calculate again: the result must not depend on what the borrowed array held.
        int length = RobustRegressionMath.ScratchLength(300, false);
        decimal[] junk = System.Buffers.ArrayPool<decimal>.Shared.Rent(length);
        Array.Fill(junk, 123456789m);
        System.Buffers.ArrayPool<decimal>.Shared.Return(junk);

        RangeLineFit second = RobustRegressionTrendValues.Calculate(candles, 0, last, false, 2m, false, 300);
        Assert.Equal(first.Slope, second.Slope);
        Assert.Equal(first.Intercept, second.Intercept);
        Assert.Equal(first.Sigma, second.Sigma);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Robust_PeriodTwoAndThree_AreFitsWithoutAnError(bool siegel)
    {
        List<CoreCandleData> candles = Bars(80);
        foreach (int period in new[] { 2, 3 })
        {
            RangeLineFit fit = RobustRegressionTrendValues.Calculate(candles, 0, 79, siegel, 2m, true, period);
            Assert.True(fit.IsOk, $"period {period}");
            Assert.True(fit.Curve.BandHalfWidth >= 0m);   // a very narrow (possibly zero) band is allowed
        }
    }

    [Fact]
    public void Robust_PriceSource_FitsTheSelectedPrice()
    {
        List<CoreCandleData> candles = Bars(80);
        List<CoreCandleData> medians = candles
            .Select(c => new CoreCandleData(c.Timestamp, (c.High + c.Low) / 2m, (c.High + c.Low) / 2m, (c.High + c.Low) / 2m, (c.High + c.Low) / 2m, c.Volume))
            .ToList();

        RangeLineFit viaSource = RobustRegressionTrendValues.Calculate(candles, 0, 79, false, 2m, false, 30, PriceType.Median);
        RangeLineFit direct = RobustRegressionTrendValues.Calculate(medians, 0, 79, false, 2m, false, 30);
        RangeLineFit close = RobustRegressionTrendValues.Calculate(candles, 0, 79, false, 2m, false, 30);

        Assert.Equal(direct.Slope, viaSource.Slope);
        Assert.Equal(direct.Intercept, viaSource.Intercept);
        Assert.Equal(direct.Sigma, viaSource.Sigma);
        Assert.NotEqual(close.Intercept, viaSource.Intercept);
    }

    [Theory]
    [MemberData(nameof(AllPriceTypes))]
    public void Robust_EveryPriceTypeFits(PriceType type)
    {
        List<CoreCandleData> candles = Bars(80);
        Assert.True(RobustRegressionTrendValues.Calculate(candles, 0, 79, false, 2m, false, 30, type).IsOk, type.ToString());
    }

    [Fact]
    public void Robust_FlatPrices_StillGiveAZeroBand_NotAnError()
    {
        RangeLineFit fit = RobustRegressionTrendValues.Calculate(WithCloses(Enumerable.Repeat(50m, 40)), 0, 39, false, 2m, false, 10);
        Assert.True(fit.IsOk);
        Assert.Equal(0m, fit.Curve.BandHalfWidth);
    }

    [Theory]
    [MemberData(nameof(AllPriceTypes))]
    public void TheWalker_IsTheOneDefinitionOfTheRangePrices_AndNeverReadsTheBarBeforeTheRange(PriceType type)
    {
        List<CoreCandleData> candles = Bars(60);
        const int first = 20;
        const int last = 45;
        decimal[] series = DrawingRangePriceSeries.Extract(candles, first, last, type);

        var walker = new StockAnalyzer.Core.Models.Indicators.RangePriceWalker(type);
        for (int i = 0; i < series.Length; i++)
        {
            CoreCandleData candle = candles[first + i];
            Assert.Equal(series[i], walker.Next(in candle));
        }

        // Changing every bar before the range must not change a single price of the range.
        List<CoreCandleData> altered = candles
            .Select((c, i) => i < first ? new CoreCandleData(c.Timestamp, c.Open + 7m, c.High + 9m, c.Low + 5m, c.Close + 8m, c.Volume) : c)
            .ToList();
        Assert.Equal(series, DrawingRangePriceSeries.Extract(altered, first, last, type));
    }

    [Fact]
    public void TheDefaultWalker_IsTheCloseWalker()
    {
        List<CoreCandleData> candles = Bars(10);
        var viaDefault = default(StockAnalyzer.Core.Models.Indicators.RangePriceWalker);
        var viaClose = new StockAnalyzer.Core.Models.Indicators.RangePriceWalker(PriceType.Close);
        foreach (CoreCandleData candle in candles)
        {
            CoreCandleData bar = candle;
            Assert.Equal(viaClose.Next(in bar), viaDefault.Next(in bar));
        }
    }
}
