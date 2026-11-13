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

/// <summary>Plan 02 T5: AnchoredVWAP date anchor, volume-weighted standard deviation bands, default registration.</summary>
public class CoreAnchoredVwapDateAndBandsTests
{
    private static CoreCandleData Flat(DateTime day, decimal price, long volume) => new(day, price, price, price, price, volume);

    private static List<CoreCandleData> Varied(int count)
    {
        var list = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal close = 50m + (i * 7 % 11) + i * 0.25m;
            list.Add(new CoreCandleData(new DateTime(2026, 3, 2).AddDays(i), close - 0.5m, close + 1.5m, close - 1.25m, close, 100 + (i * 37 % 250)));
        }

        return list;
    }

    private static IIndicatorResult Calculate(IReadOnlyList<CoreCandleData> candles, Action<CoreAnchoredVwapParameter> configure)
    {
        var parameter = new CoreAnchoredVwapParameter();
        configure(parameter);
        var indicator = new CoreAnchoredVWAPIndicator();
        indicator.Configure(parameter);
        return indicator.Calculate(candles);
    }

    // ---- unchanged default ----

    [Fact]
    public void DefaultAnchorIndex_EqualsAnIndependentCalculation_AndReturnsOnlyMain()
    {
        List<CoreCandleData> candles = Varied(30);
        IIndicatorResult result = Calculate(candles, p => p.AnchorIndex = 4);
        Assert.Equal(new[] { "Main" }, result.SeriesNames);

        decimal pv = 0m;
        long v = 0;
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < 4)
            {
                Assert.Null(result.GetSeries("Main")[i]);
                continue;
            }

            decimal typical = (candles[i].High + candles[i].Low + candles[i].Close) / 3;
            pv += typical * candles[i].Volume;
            v += candles[i].Volume;
            Assert.Equal(pv / v, result.GetSeries("Main")[i]);
        }
    }

    [Fact]
    public void EmptyAnchorDate_IsTheSameAsTheIndexOnlyConfiguration()
    {
        List<CoreCandleData> candles = Varied(20);
        IIndicatorResult withDate = Calculate(candles, p => { p.AnchorIndex = 3; p.AnchorDate = ""; });
        IIndicatorResult indexOnly = new CoreAnchoredVWAPIndicator { AnchorIndex = 3 }.Calculate(candles);
        Assert.Equal(indexOnly.GetSeries("Main"), withDate.GetSeries("Main"));
    }

    [Fact]
    public void AnInvalidAnchorIndex_GivesAllNulls_AsBefore()
    {
        List<CoreCandleData> candles = Varied(5);
        Assert.All(Calculate(candles, p => p.AnchorIndex = 9).GetSeries("Main"), v => Assert.Null(v));
    }

    // ---- date anchor ----

    [Fact]
    public void AnchorDate_IsTheFirstBarOnOrAfterTheDate_AHolidayGoesToTheNextTradingDay()
    {
        var candles = new List<CoreCandleData>
        {
            Flat(new DateTime(2026, 9, 14, 15, 0, 0), 10m, 100),
            Flat(new DateTime(2026, 9, 15, 15, 0, 0), 20m, 100),
            Flat(new DateTime(2026, 9, 17, 15, 0, 0), 30m, 100),   // 09-16 is a holiday
            Flat(new DateTime(2026, 9, 18, 15, 0, 0), 50m, 100),
        };

        IReadOnlyList<decimal?> onHoliday = Calculate(candles, p => p.AnchorDate = "2026-09-16").GetSeries("Main");
        Assert.Null(onHoliday[0]);
        Assert.Null(onHoliday[1]);
        Assert.Equal(30m, onHoliday[2]);
        Assert.Equal(40m, onHoliday[3]);

        // The date of a bar with a time of day belongs to that bar.
        IReadOnlyList<decimal?> onBar = Calculate(candles, p => p.AnchorDate = "2026-09-15").GetSeries("Main");
        Assert.Null(onBar[0]);
        Assert.Equal(20m, onBar[1]);

        // Before the first bar: anchored at the first bar.
        Assert.Equal(10m, Calculate(candles, p => p.AnchorDate = "2020-01-01").GetSeries("Main")[0]);
    }

    [Fact]
    public void AnchorDateAfterTheLastBar_GivesAllNulls_BandsIncluded()
    {
        List<CoreCandleData> candles = Varied(10);
        IIndicatorResult result = Calculate(candles, p => { p.AnchorDate = "2030-01-01"; p.ShowBands = true; });

        Assert.All(result.GetSeries("Main"), v => Assert.Null(v));
        Assert.All(result.GetSeries("UpperBand"), v => Assert.Null(v));
        Assert.All(result.GetSeries("LowerBand"), v => Assert.Null(v));
        Assert.Equal(candles.Count, result.GetSeries("UpperBand").Count);
    }

    [Theory]
    [InlineData("2026/09/16")]
    [InlineData("16-09-2026")]
    [InlineData("2026-9-16x")]
    [InlineData("not a date")]
    public void AnInvalidAnchorDateFormat_FailsValidation(string text)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreAnchoredVwapParameter { AnchorDate = text }.Validate());
    }

    [Fact]
    public void ValidParameters_PassValidation()
    {
        new CoreAnchoredVwapParameter().Validate();
        new CoreAnchoredVwapParameter { AnchorDate = "2026-09-16" }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreAnchoredVwapParameter { StdDevMultiplier = 0m }.Validate());
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
        IIndicatorResult result = Calculate(candles, p => { p.ShowBands = true; p.StdDevMultiplier = 2m; });

        Assert.Equal(17.5m, result.GetSeries("Main")[1]);
        AssertNear(17.5m + 2m * 4.330127018922193m, result.GetSeries("UpperBand")[1]);   // 26.1602540378...
        AssertNear(17.5m - 2m * 4.330127018922193m, result.GetSeries("LowerBand")[1]);   // 8.8397459621...
    }

    [Fact]
    public void Bands_MatchTheDefinitionFormulaFromTheAnchor_AndAreNullBeforeIt()
    {
        List<CoreCandleData> candles = Varied(40);
        IIndicatorResult result = Calculate(candles, p => { p.AnchorIndex = 6; p.ShowBands = true; p.StdDevMultiplier = 1.5m; });

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < 6)
            {
                Assert.Null(result.GetSeries("UpperBand")[i]);
                Assert.Null(result.GetSeries("LowerBand")[i]);
                continue;
            }

            double sumV = 0, sumPV = 0;
            for (int j = 6; j <= i; j++)
            {
                double tp = (double)((candles[j].High + candles[j].Low + candles[j].Close) / 3m);
                sumPV += tp * candles[j].Volume;
                sumV += candles[j].Volume;
            }

            double vwap = sumPV / sumV;
            double dev = 0;
            for (int j = 6; j <= i; j++)
            {
                double tp = (double)((candles[j].High + candles[j].Low + candles[j].Close) / 3m);
                dev += candles[j].Volume * (tp - vwap) * (tp - vwap);
            }

            double sigma = Math.Sqrt(Math.Max(0, dev / sumV));
            Assert.True(Math.Abs(vwap + 1.5 * sigma - (double)result.GetSeries("UpperBand")[i]!.Value) / Math.Max(1.0, vwap) < 1e-12, $"upper at {i}");
            Assert.True(Math.Abs(vwap - 1.5 * sigma - (double)result.GetSeries("LowerBand")[i]!.Value) / Math.Max(1.0, vwap) < 1e-12, $"lower at {i}");
        }
    }

    [Fact]
    public void ShowBandsOff_ReturnsOnlyMain_On_ReturnsThreeSeries_ZeroVolumeGivesNull()
    {
        List<CoreCandleData> candles = Varied(8);
        Assert.Equal(new[] { "Main" }, Calculate(candles, p => { }).SeriesNames);
        Assert.Equal(new[] { "Main", "UpperBand", "LowerBand" }, Calculate(candles, p => p.ShowBands = true).SeriesNames);

        var noVolume = new List<CoreCandleData> { Flat(new DateTime(2026, 3, 2), 10m, 0), Flat(new DateTime(2026, 3, 3), 11m, 0) };
        IIndicatorResult result = Calculate(noVolume, p => p.ShowBands = true);
        Assert.All(result.GetSeries("Main"), v => Assert.Null(v));
        Assert.All(result.GetSeries("UpperBand"), v => Assert.Null(v));
    }

    // ---- registration ----

    [Fact]
    public void DisplayName_ShowsNothingWithoutAnAnchor_TheDateWhenSet_AndTheIndexWhenNonZero()
    {
        // No anchor specified (default): neither the name nor the short name carries "(Idx: 0)".
        Assert.Equal("Anchored VWAP", new CoreAnchoredVwapParameter().GetDisplayName("Anchored VWAP"));
        Assert.Equal("AnchoredVWAP", new CoreAnchoredVwapParameter().GetDisplayName("AnchoredVWAP").Replace(" (", "("));
        Assert.Equal("Anchored VWAP (Idx: 5)", new CoreAnchoredVwapParameter { AnchorIndex = 5 }.GetDisplayName("Anchored VWAP"));
        Assert.Equal("Anchored VWAP (2026-09-16)", new CoreAnchoredVwapParameter { AnchorDate = "2026-09-16" }.GetDisplayName("Anchored VWAP"));
    }

    [Fact]
    public void GetDefaultSettings_AttachesTheParameterObject_AndTheDefaultEntryMatchesIt()
    {
        CoreIndicatorSettings fromIndicator = IndicatorFactory.CreateStatic(IndicatorType.AnchoredVWAP)!.GetDefaultSettings();
        Assert.IsType<CoreAnchoredVwapParameter>(fromIndicator.ParameterObject);
        Assert.Equal(IndicatorType.AnchoredVWAP, fromIndicator.TypeEnum);

        CoreIndicatorSettings entry = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.AnchoredVWAP);
        Assert.IsType<CoreAnchoredVwapParameter>(entry.ParameterObject);
        // The static entry and the indicator's own default settings agree (this is what the registration dialogs' category line relies on).
        Assert.Equal(fromIndicator.Category, entry.Category);
        Assert.Equal(CoreIndicatorCategory.Volume, entry.Category);
        Assert.Equal(fromIndicator.IsOverlay, entry.IsOverlay);
        Assert.Equal(fromIndicator.Color, entry.Color);
        Assert.Equal(fromIndicator.Thickness, entry.Thickness);
        Assert.Equal(fromIndicator.Style, entry.Style);
        Assert.True(entry.IsOverlay);
        Assert.False(entry.IsEnabled);
        Assert.Equal(IndicatorDefaultConstants.Cyan, entry.SeriesColors.Single(c => c.TargetSeries.Contains("Main")).Color);
        var bandColor = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.BB).SeriesColors.Single(c => c.TargetSeries.Contains("Upper")).Color;
        Assert.Equal(bandColor, entry.SeriesColors.Single(c => c.TargetSeries.Contains("UpperBand")).Color);
        Assert.Equal(bandColor, entry.SeriesColors.Single(c => c.TargetSeries.Contains("LowerBand")).Color);
    }

    [Fact]
    public void OutputNames_StayOnlyTheMainOutput()
    {
        Assert.Equal(new[] { "AnchoredVWAP" }, IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.AnchoredVWAP));
    }

    private static void AssertNear(decimal expected, decimal? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual!.Value) < 1e-9m, $"expected {expected} but was {actual}");
    }
}
