using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volume;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>Plan 02 T4: VWAP reset period, volume-weighted standard deviation bands, and the null-price rule (U-05=C).</summary>
public class CoreVwapResetAndBandsTests
{
    private static CoreCandleData Flat(DateTime day, decimal price, long volume) => new(day, price, price, price, price, volume);

    private static IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles, VwapResetPeriod reset = VwapResetPeriod.Cumulative, bool bands = false, decimal multiplier = 2m)
    {
        var indicator = new CoreVwapIndicator();
        indicator.Configure(new CoreVwapParameter { ResetPeriod = reset, ShowBands = bands, StdDevMultiplier = multiplier });
        return indicator.Calculate(candles);
    }

    private static List<CoreCandleData> Varied(int count)
    {
        var list = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal close = 50m + (i * 7 % 11) + i * 0.25m;
            list.Add(new CoreCandleData(new DateTime(2025, 11, 20).AddDays(i), close - 0.5m, close + 1.5m, close - 1.25m, close, 100 + (i * 37 % 250)));
        }

        return list;
    }

    // ---- default output unchanged ----

    [Fact]
    public void DefaultCumulative_EqualsAnIndependentCumulativeCalculation_AndReturnsOnlyMain()
    {
        List<CoreCandleData> candles = Varied(60);
        IIndicatorResult result = new CoreVwapIndicator().Calculate(candles);
        Assert.Equal(new[] { "Main" }, result.SeriesNames);

        decimal pv = 0m, v = 0m;
        for (int i = 0; i < candles.Count; i++)
        {
            decimal typical = (candles[i].High + candles[i].Low + candles[i].Close) / 3m;
            pv += typical * candles[i].Volume;
            v += candles[i].Volume;
            Assert.Equal(pv / v, result.GetSeries("Main")[i]);
        }
    }

    // ---- null price (U-05=C) ----

    [Fact]
    public void NullPriceBars_AddToNeitherNumeratorNorDenominator()
    {
        List<CoreCandleData> candles = Enumerable.Range(0, 5).Select(i => Flat(new DateTime(2026, 3, 2).AddDays(i), 10m, 100)).ToList();
        var prices = new List<decimal?> { null, null, null, 10m, 12m };

        IIndicatorResult result = new CoreVwapIndicator().CalculateWithPrices(candles, prices);
        IReadOnlyList<decimal?> main = result.GetSeries("Main");

        Assert.Null(main[0]);
        Assert.Null(main[1]);
        Assert.Null(main[2]);
        Assert.Equal(10m, main[3]);  // before the fix the three volume-only bars dragged this below 10
        Assert.Equal(11m, main[4]);  // (10*100 + 12*100) / 200
    }

    [Fact]
    public void ABarWithoutPriceKeepsTheValueOfTheSumsSoFar()
    {
        List<CoreCandleData> candles = Enumerable.Range(0, 3).Select(i => Flat(new DateTime(2026, 3, 2).AddDays(i), 10m, 100)).ToList();
        IIndicatorResult result = new CoreVwapIndicator().CalculateWithPrices(candles, new List<decimal?> { 10m, null, 20m });

        Assert.Equal(10m, result.GetSeries("Main")[0]);
        Assert.Equal(10m, result.GetSeries("Main")[1]);
        Assert.Equal(15m, result.GetSeries("Main")[2]);
    }

    // ---- reset ----

    [Fact]
    public void Monthly_FirstBarOfAMonthEqualsItsOwnPrice_AndTheSumsRestart()
    {
        var candles = new List<CoreCandleData>
        {
            Flat(new DateTime(2026, 1, 30), 10m, 100),
            Flat(new DateTime(2026, 1, 31), 20m, 100),
            Flat(new DateTime(2026, 2, 2), 40m, 50),
            Flat(new DateTime(2026, 2, 3), 20m, 50),
        };
        IReadOnlyList<decimal?> main = Calculate(candles, VwapResetPeriod.Monthly).GetSeries("Main");

        Assert.Equal(10m, main[0]);
        Assert.Equal(15m, main[1]);
        Assert.Equal(40m, main[2]);
        Assert.Equal(30m, main[3]);
    }

    [Theory]
    [InlineData(VwapResetPeriod.Weekly, "2026-03-06", "2026-03-09", "2026-03-10")]     // Fri -> Mon resets, Tue does not
    [InlineData(VwapResetPeriod.Monthly, "2026-03-31", "2026-04-01", "2026-04-02")]
    [InlineData(VwapResetPeriod.Quarterly, "2026-03-31", "2026-04-01", "2026-05-15")]
    [InlineData(VwapResetPeriod.Quarterly, "2026-06-30", "2026-07-01", "2026-09-30")]
    [InlineData(VwapResetPeriod.Quarterly, "2026-09-30", "2026-10-01", "2026-12-31")]
    [InlineData(VwapResetPeriod.Yearly, "2025-12-31", "2026-01-02", "2026-12-31")]
    public void ResetHappensAtThePeriodBoundaryOnly(VwapResetPeriod period, string last, string firstOfNext, string laterInSame)
    {
        var candles = new List<CoreCandleData>
        {
            Flat(DateTime.Parse(last), 10m, 100),
            Flat(DateTime.Parse(firstOfNext), 30m, 100),
            Flat(DateTime.Parse(laterInSame), 50m, 100),
        };
        IReadOnlyList<decimal?> main = Calculate(candles, period).GetSeries("Main");

        Assert.Equal(10m, main[0]);
        Assert.Equal(30m, main[1]);  // restarted: only its own price
        Assert.Equal(40m, main[2]);  // same period as bar 1: (30 + 50) / 2
    }

    [Fact]
    public void Quarterly_MonthsInTheSameQuarterDoNotReset()
    {
        var candles = new List<CoreCandleData>
        {
            Flat(new DateTime(2026, 1, 15), 10m, 100),
            Flat(new DateTime(2026, 2, 15), 20m, 100),
            Flat(new DateTime(2026, 3, 15), 30m, 100),
        };
        IReadOnlyList<decimal?> main = Calculate(candles, VwapResetPeriod.Quarterly).GetSeries("Main");
        Assert.Equal(20m, main[2]);
    }

    // ---- bands ----

    [Fact]
    public void Bands_HandCalculatedExample()
    {
        var candles = new List<CoreCandleData>
        {
            Flat(new DateTime(2026, 3, 2), 10m, 1),
            Flat(new DateTime(2026, 3, 3), 20m, 3),
        };
        IIndicatorResult result = Calculate(candles, bands: true, multiplier: 2m);

        Assert.Equal(17.5m, result.GetSeries("Main")[1]);
        // variance = (10*1 + ... ) : sum(V*(P-17.5)^2)/sum(V) = (1*56.25 + 3*6.25)/4 = 18.75, sigma = sqrt(18.75) = 4.3301270189...
        AssertNear(17.5m + 2m * 4.330127018922193m, result.GetSeries("UpperBand")[1]);  // 26.1602540378...
        AssertNear(17.5m - 2m * 4.330127018922193m, result.GetSeries("LowerBand")[1]);  // 8.8397459621...
        // One bar: sigma 0, so the bands sit on the VWAP.
        Assert.Equal(10m, result.GetSeries("UpperBand")[0]);
        Assert.Equal(10m, result.GetSeries("LowerBand")[0]);
    }

    [Fact]
    public void Bands_MatchTheDefinitionFormulaWithinTheSamePeriod()
    {
        List<CoreCandleData> candles = Varied(80);
        IIndicatorResult result = Calculate(candles, VwapResetPeriod.Monthly, bands: true, multiplier: 1.5m);

        for (int i = 0; i < candles.Count; i++)
        {
            int key = TimeFrameAggregator.GetPeriodKey(candles[i].Timestamp, TimeFrame.MN1);
            int start = i;
            while (start > 0 && TimeFrameAggregator.GetPeriodKey(candles[start - 1].Timestamp, TimeFrame.MN1) == key) start--;

            double sumV = 0, sumPV = 0;
            for (int j = start; j <= i; j++)
            {
                double p = (double)((candles[j].High + candles[j].Low + candles[j].Close) / 3m);
                sumPV += p * candles[j].Volume;
                sumV += candles[j].Volume;
            }

            double vwap = sumPV / sumV;
            double dev = 0;
            for (int j = start; j <= i; j++)
            {
                double p = (double)((candles[j].High + candles[j].Low + candles[j].Close) / 3m);
                dev += candles[j].Volume * (p - vwap) * (p - vwap);
            }

            double sigma = Math.Sqrt(Math.Max(0, dev / sumV));
            double expectedUpper = vwap + 1.5 * sigma;
            double expectedLower = vwap - 1.5 * sigma;
            Assert.True(RelativeError(expectedUpper, (double)result.GetSeries("UpperBand")[i]!.Value) < 1e-12, $"upper at {i}");
            Assert.True(RelativeError(expectedLower, (double)result.GetSeries("LowerBand")[i]!.Value) < 1e-12, $"lower at {i}");
        }
    }

    [Fact]
    public void ShowBandsOff_ReturnsMainOnly_AndOn_ReturnsThreeSeries()
    {
        List<CoreCandleData> candles = Varied(10);
        Assert.Equal(new[] { "Main" }, Calculate(candles).SeriesNames);
        Assert.Equal(new[] { "Main", "UpperBand", "LowerBand" }, Calculate(candles, bands: true).SeriesNames);
        Assert.Equal(candles.Count, Calculate(candles, bands: true).GetSeries("UpperBand").Count);
    }

    [Fact]
    public void ZeroVolumeOnly_GivesNullMainAndNullBands()
    {
        var candles = new List<CoreCandleData> { Flat(new DateTime(2026, 3, 2), 10m, 0), Flat(new DateTime(2026, 3, 3), 11m, 0) };
        IIndicatorResult result = Calculate(candles, bands: true);

        Assert.All(result.GetSeries("Main"), v => Assert.Null(v));
        Assert.All(result.GetSeries("UpperBand"), v => Assert.Null(v));
        Assert.All(result.GetSeries("LowerBand"), v => Assert.Null(v));
    }

    [Fact]
    public void AnOverflowInTheSquaredSum_NullsTheBandsUntilTheNextReset_ButNotTheMain()
    {
        // price^2 * volume exceeds the decimal range, price * volume does not.
        var candles = new List<CoreCandleData>
        {
            Flat(new DateTime(2026, 3, 30), 100_000_000_000_000m, 100),
            Flat(new DateTime(2026, 3, 31), 100_000_000_000_000m, 100),
            Flat(new DateTime(2026, 4, 1), 10m, 100),
        };
        IIndicatorResult result = Calculate(candles, VwapResetPeriod.Monthly, bands: true);

        Assert.NotNull(result.GetSeries("Main")[0]);
        Assert.NotNull(result.GetSeries("Main")[1]);
        Assert.Null(result.GetSeries("UpperBand")[0]);
        Assert.Null(result.GetSeries("UpperBand")[1]);
        Assert.Equal(10m, result.GetSeries("Main")[2]);
        Assert.Equal(10m, result.GetSeries("UpperBand")[2]);   // new period: bands are back
    }

    [Fact]
    public void BandMath_SqrtOfAVarianceAndOfNonsense()
    {
        AssertNear(4.330127018922193m, VwapBandMath.Sqrt(18.75m));
        Assert.Equal(0m, VwapBandMath.Sqrt(0m));
        Assert.Null(VwapBandMath.Sqrt(-1m));
    }

    // ---- parameter, defaults, output names ----

    [Fact]
    public void Parameter_DefaultsDisplayNameAndValidation()
    {
        var p = new CoreVwapParameter();
        Assert.Equal(VwapResetPeriod.Cumulative, p.ResetPeriod);
        Assert.False(p.ShowBands);
        Assert.Equal(2.0m, p.StdDevMultiplier);
        p.Validate();
        Assert.Equal("VWAP", p.GetDisplayName("VWAP"));
        Assert.Equal("VWAP (Monthly)", new CoreVwapParameter { ResetPeriod = VwapResetPeriod.Monthly }.GetDisplayName("VWAP"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVwapParameter { StdDevMultiplier = 0.05m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVwapParameter { StdDevMultiplier = 10.5m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVwapParameter { ResetPeriod = (VwapResetPeriod)99 }.Validate());
    }

    [Fact]
    public void DefaultSettingsEntry_ColoursAndParameter()
    {
        CoreIndicatorSettings entry = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.VWAP);
        Assert.IsType<CoreVwapParameter>(entry.ParameterObject);
        Assert.Equal(PriceType.Typical, entry.PriceSource);
        Assert.Equal(IndicatorDefaultConstants.Cyan, entry.Color);
        Assert.Equal(IndicatorDefaultConstants.Cyan, entry.SeriesColors.Single(c => c.TargetSeries.Contains("Main")).Color);
        CoreIndicatorSettings bollinger = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.BB);
        var bandColor =bollinger.SeriesColors.Single(c => c.TargetSeries.Contains("Upper")).Color;
        Assert.Equal(bandColor, entry.SeriesColors.Single(c => c.TargetSeries.Contains("UpperBand")).Color);
        Assert.Equal(bandColor, entry.SeriesColors.Single(c => c.TargetSeries.Contains("LowerBand")).Color);
    }

    [Fact]
    public void OutputNames_StayOnlyVwap_BecauseTheBandsExistOnlyWhileShowBandsIsOn()
    {
        // The output catalog (screener, backtest, registration dialogs) must list only what a default VWAP emits.
        Assert.Equal(new[] { "VWAP" }, IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.VWAP));
        Assert.Equal("Main", IndicatorOutputSeriesResolver.Normalize(IndicatorType.VWAP, "VWAP"));
    }

    private static void AssertNear(decimal expected, decimal? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual!.Value) < 1e-9m, $"expected {expected} but was {actual}");
    }

    private static double RelativeError(double expected, double actual) => Math.Abs(expected - actual) / Math.Max(1.0, Math.Abs(expected));
}
