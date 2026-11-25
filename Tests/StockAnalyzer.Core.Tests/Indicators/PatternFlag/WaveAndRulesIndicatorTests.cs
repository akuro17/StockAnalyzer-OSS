using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Rules;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Waves;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Indicator, parameter, catalog, color, suppression, and constant contracts for V2-7 indicators
/// (<see cref="IndicatorType.ElliottWaveImpulse"/>, <see cref="IndicatorType.ElliottWaveCorrective"/>,
/// <see cref="IndicatorType.GranvilleBuySignal"/>, <see cref="IndicatorType.GranvilleSellSignal"/>;
/// ledger <c>sa_pattern_literal_ledger_V2-7_ElliottGranville.md</c>, rows <c>WI_*</c>).
/// </summary>
public class WaveAndRulesIndicatorTests
{
    private static readonly IndicatorFactory Factory = new();

    private static CoreCandleData[] MakeCoreWalk(int seed, int count)
        => PatternEngines.PatternEngineTestBars.Walk(seed, count)
            .Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume))
            .ToArray();

    [Theory]
    [InlineData(IndicatorType.ElliottWaveImpulse)]
    [InlineData(IndicatorType.ElliottWaveCorrective)]
    public void WI_Elliott_NullBeforeNmin(IndicatorType type)
    {
        CoreCandleData[] bars = MakeCoreWalk(2, 60);
        IIndicatorResult result = Factory.Create(type)!.Calculate(bars);
        Assert.True(result.IsSuccessful, result.ErrorMessage);

        IReadOnlyList<decimal?> main = result.GetSeries(IndicatorResult.MainSeriesName);
        for (int t = 0; t < PatternSpecConstants.ElliottMinBars - 1; t++)
        {
            Assert.Null(main[t]);
        }

        Assert.NotNull(main[PatternSpecConstants.ElliottMinBars - 1]);
    }

    [Fact]
    public void WI_Granville_NullBeforeNmin_206_And_Dynamic()
    {
        CoreCandleData[] bars = MakeCoreWalk(3, 250);

        // Default MaPeriod=200 + SlopePeriod=5 + 1 = 206 -> indices 0..204 must be null, 205 non-null.
        foreach (IndicatorType type in new[] { IndicatorType.GranvilleBuySignal, IndicatorType.GranvilleSellSignal })
        {
            IIndicatorResult defRes = Factory.Create(type)!.Calculate(bars);
            Assert.True(defRes.IsSuccessful, defRes.ErrorMessage);

            IReadOnlyList<decimal?> main = defRes.GetSeries(IndicatorResult.MainSeriesName);
            IReadOnlyList<decimal?> ruleCode = defRes.GetSeries(CoreGranvilleIndicatorBase.RuleCodeSeriesName);
            for (int t = 0; t < 205; t++)
            {
                Assert.Null(main[t]);
                Assert.Null(ruleCode[t]);
            }

            Assert.NotNull(main[205]);
            Assert.NotNull(ruleCode[205]);

            // Custom MaPeriod=20, SlopePeriod=3 -> MinBars=24 -> indices 0..22 null, index 23 non-null!
            var customParam = new CoreGranvilleSignalParameter { MaPeriod = 20, SlopePeriod = 3 };
            IIndicatorResult dynRes = Factory.Create(type, customParam)!.Calculate(bars);
            Assert.True(dynRes.IsSuccessful, dynRes.ErrorMessage);

            IReadOnlyList<decimal?> dynMain = dynRes.GetSeries(IndicatorResult.MainSeriesName);
            for (int t = 0; t < 23; t++)
            {
                Assert.Null(dynMain[t]);
            }

            Assert.NotNull(dynMain[23]);
        }
    }

    [Fact]
    public void WI_SeriesColorsAndSuppression()
    {
        CoreIndicatorSettings buySettings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.GranvilleBuySignal);
        CoreIndicatorSettings sellSettings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.GranvilleSellSignal);

        // Integration spec 4.2 exact ARGB colors:
        // Buy1..Buy4: #FF26A69A, #FF42A5F5, #FF66BB6A, #FFFFA726
        // Sell1..Sell4: #FFEF5350, #FFAB47BC, #FFFF7043, #FFEC407A
        Assert.Equal(
            new[] { "#FF26A69A", "#FF42A5F5", "#FF66BB6A", "#FFFFA726" },
            buySettings.SeriesColors.Select(c => $"#{c.Color.A:X2}{c.Color.R:X2}{c.Color.G:X2}{c.Color.B:X2}").ToArray());
        Assert.Equal(
            new[] { "Buy1", "Buy2", "Buy3", "Buy4" },
            buySettings.SeriesColors.Select(c => c.TargetSeries.Single()).ToArray());

        Assert.Equal(
            new[] { "#FFEF5350", "#FFAB47BC", "#FFFF7043", "#FFEC407A" },
            sellSettings.SeriesColors.Select(c => $"#{c.Color.A:X2}{c.Color.R:X2}{c.Color.G:X2}{c.Color.B:X2}").ToArray());
        Assert.Equal(
            new[] { "Sell1", "Sell2", "Sell3", "Sell4" },
            sellSettings.SeriesColors.Select(c => c.TargetSeries.Single()).ToArray());

        // Main and RuleCode hidden on chart and DataWindow; Buy1..4 / Sell1..4 visible.
        foreach (IndicatorType type in new[] { IndicatorType.GranvilleBuySignal, IndicatorType.GranvilleSellSignal })
        {
            Assert.True(IndicatorChartSuppression.IsSuppressed(type, IndicatorResult.MainSeriesName));
            Assert.True(IndicatorChartSuppression.IsSuppressed(type, CoreGranvilleIndicatorBase.RuleCodeSeriesName));
            Assert.True(IndicatorChartSuppression.IsDataWindowSuppressed(type, IndicatorResult.MainSeriesName));
            Assert.True(IndicatorChartSuppression.IsDataWindowSuppressed(type, CoreGranvilleIndicatorBase.RuleCodeSeriesName));
        }

        // Elliott Wave colors stay at WaveTheoryFlagColor.
        foreach (IndicatorType type in new[] { IndicatorType.ElliottWaveImpulse, IndicatorType.ElliottWaveCorrective })
        {
            PatternFlagSpec spec = PatternFlagIndicatorCatalog.Get(type);
            Assert.Equal(CoreIndicatorCategory.WaveTheory, spec.Category);
            Assert.Equal(IndicatorDefaultConstants.WaveTheoryFlagColor, spec.Color);
        }
    }

    [Fact]
    public void WI_IndicatorWiring_ElliottImpulseAndCorrective()
    {
        CoreCandleData[] bars = MakeCoreWalk(1, 500);

        var imp = (CoreElliottWaveImpulseIndicator)Factory.Create(IndicatorType.ElliottWaveImpulse, PatternEngines.FormationFixtures.RelaxedElliottImpulse())!;
        IIndicatorResult impRes = imp.Calculate(bars);
        Assert.True(impRes.IsSuccessful, impRes.ErrorMessage);
        Assert.Equal(impRes.GetSeries("BullishImpulse").ToArray(), imp.BullishImpulse.ToArray());
        Assert.Equal(impRes.GetSeries("BearishImpulse").ToArray(), imp.BearishImpulse.ToArray());
        Assert.Equal(impRes.GetSeries(IndicatorResult.MainSeriesName).ToArray(), imp.Main.ToArray());
        Assert.Equal(impRes.GetSeries(IndicatorSeriesNames.PatternDirection).ToArray(), imp.Direction.ToArray());

        var corr = (CoreElliottWaveCorrectiveIndicator)Factory.Create(IndicatorType.ElliottWaveCorrective, PatternEngines.FormationFixtures.RelaxedElliottCorrective())!;
        IIndicatorResult corrRes = corr.Calculate(bars);
        Assert.True(corrRes.IsSuccessful, corrRes.ErrorMessage);
        Assert.Equal(corrRes.GetSeries("BullishCorrective").ToArray(), corr.BullishCorrective.ToArray());
        Assert.Equal(corrRes.GetSeries("BearishCorrective").ToArray(), corr.BearishCorrective.ToArray());
        Assert.Equal(corrRes.GetSeries(IndicatorResult.MainSeriesName).ToArray(), corr.Main.ToArray());
        Assert.Equal(corrRes.GetSeries(IndicatorSeriesNames.PatternDirection).ToArray(), corr.Direction.ToArray());
    }

    [Fact]
    public void WI_IndicatorWiring_GranvilleBuyAndSell()
    {
        CoreCandleData[] bars = MakeCoreWalk(2, 500);
        var p = new CoreGranvilleSignalParameter { MaPeriod = 20, SlopePeriod = 3, BounceTolerance = 2.0m, DeviationThreshold = 3.0m };

        var buy = (CoreGranvilleBuySignalIndicator)Factory.Create(IndicatorType.GranvilleBuySignal, p)!;
        IIndicatorResult buyRes = buy.Calculate(bars);
        Assert.True(buyRes.IsSuccessful, buyRes.ErrorMessage);
        Assert.Equal(buyRes.GetSeries(nameof(CoreGranvilleBuySignalIndicator.Buy1)).ToArray(), buy.Buy1.ToArray());
        Assert.Equal(buyRes.GetSeries(nameof(CoreGranvilleBuySignalIndicator.Buy2)).ToArray(), buy.Buy2.ToArray());
        Assert.Equal(buyRes.GetSeries(nameof(CoreGranvilleBuySignalIndicator.Buy3)).ToArray(), buy.Buy3.ToArray());
        Assert.Equal(buyRes.GetSeries(nameof(CoreGranvilleBuySignalIndicator.Buy4)).ToArray(), buy.Buy4.ToArray());
        Assert.Equal(buyRes.GetSeries(CoreGranvilleIndicatorBase.RuleCodeSeriesName).ToArray(), buy.RuleCode.ToArray());
        Assert.Contains(buy.RuleCode, v => v is 1m or 2m or 3m or 4m);

        var sell = (CoreGranvilleSellSignalIndicator)Factory.Create(IndicatorType.GranvilleSellSignal, p)!;
        IIndicatorResult sellRes = sell.Calculate(bars);
        Assert.True(sellRes.IsSuccessful, sellRes.ErrorMessage);
        Assert.Equal(sellRes.GetSeries(nameof(CoreGranvilleSellSignalIndicator.Sell1)).ToArray(), sell.Sell1.ToArray());
        Assert.Equal(sellRes.GetSeries(nameof(CoreGranvilleSellSignalIndicator.Sell2)).ToArray(), sell.Sell2.ToArray());
        Assert.Equal(sellRes.GetSeries(nameof(CoreGranvilleSellSignalIndicator.Sell3)).ToArray(), sell.Sell3.ToArray());
        Assert.Equal(sellRes.GetSeries(nameof(CoreGranvilleSellSignalIndicator.Sell4)).ToArray(), sell.Sell4.ToArray());
        Assert.Equal(sellRes.GetSeries(CoreGranvilleIndicatorBase.RuleCodeSeriesName).ToArray(), sell.RuleCode.ToArray());
        Assert.Contains(sell.RuleCode, v => v is -1m or -2m or -3m or -4m);
    }

    [Fact]
    public void WI_Parameters_DefaultsRangesValidationAndWarmup()
    {
        var imp = new CoreElliottImpulseParameter();
        Assert.Equal(PatternDirectionMode.Both, imp.DirectionMode);
        Assert.Equal(5.0m, imp.ZigZagThresholdPercent);
        Assert.Equal(1.5m, imp.MinSwingAtrMultiplier);
        Assert.True(imp.RequireOriginPriceZone);
        Assert.Equal(15, imp.MinSpanBars);
        Assert.Equal(200, imp.MaxSpanBars);
        Assert.Equal(6.0m, imp.MaxTimeSymmetryRatio);
        Assert.Equal(0.05m, imp.OverlapTolerance);
        Assert.False(imp.AllowTruncation);
        Assert.Equal(0.20m, imp.FibonacciTolerance);
        Assert.Equal(0.35m, imp.MinConfidenceScore);
        Assert.Equal(5, imp.MaxBarsSinceConfirmation);
        Assert.Equal(500, imp.GetRequiredWarmupBars());
        imp.Validate();

        var corr = new CoreElliottCorrectiveParameter();
        Assert.Equal(9, corr.MinSpanBars);
        Assert.Equal(120, corr.MaxSpanBars);
        Assert.True(corr.RequireWaveCExceedWaveA);
        Assert.Equal(500, corr.GetRequiredWarmupBars());
        corr.Validate();

        var invImp = new CoreElliottImpulseParameter { MinSpanBars = 40, MaxSpanBars = 30 };
        Assert.Throws<ArgumentOutOfRangeException>(() => invImp.Validate());
        var invOv = new CoreElliottImpulseParameter { OverlapTolerance = 0.30m };
        Assert.Throws<ArgumentOutOfRangeException>(() => invOv.Validate());

        var gran = new CoreGranvilleSignalParameter();
        Assert.Equal(200, gran.MaPeriod);
        Assert.Equal(5, gran.SlopePeriod);
        Assert.Equal(10.0m, gran.DeviationThreshold);
        Assert.Equal(0.5m, gran.BounceTolerance);
        Assert.Equal(0.05m, gran.FlatThreshold);
        Assert.True(gran.EnableRule1 && gran.EnableRule2 && gran.EnableRule3 && gran.EnableRule4);
        Assert.True(gran.RequirePrevBarOnMaSideForRule3);
        Assert.False(gran.Rule4FirstEntryOrReversalOnly);
        Assert.False(gran.RequireVolumeOnRule1);
        Assert.Equal(500, gran.GetRequiredWarmupBars()); // Max(206, 500)
        gran.Validate();

        // Warmup expands dynamically when MaPeriod + SlopePeriod + 1 exceeds 500.
        var longGran = new CoreGranvilleSignalParameter { MaPeriod = 500, SlopePeriod = 50 };
        Assert.Equal(551, longGran.GetRequiredWarmupBars());
        longGran.Validate();

        var badGran = new CoreGranvilleSignalParameter { MaPeriod = 4 };
        Assert.Throws<ArgumentOutOfRangeException>(() => badGran.Validate());
    }

    [Fact]
    public void WI_Constants_MatchSpecifications()
    {
        Assert.Equal(40, PatternSpecConstants.ElliottMinBars);
        Assert.Equal(0, PatternSpecConstants.ElliottFractalBars);
        Assert.Equal(40, PatternSpecConstants.ElliottZoneBars);
        Assert.Equal(0.25m, PatternSpecConstants.ElliottOriginZoneThreshold);
        Assert.Equal(3.0m, PatternSpecConstants.ElliottTimeSymmetryFloorBars);
        Assert.Equal(2, PatternSpecConstants.ElliottImpulseWaveBarsMin);
        Assert.Equal(60, PatternSpecConstants.ElliottImpulseWaveBarsMax);
        Assert.Equal(2, PatternSpecConstants.ElliottCorrectiveWaveBarsMin);
        Assert.Equal(50, PatternSpecConstants.ElliottCorrectiveWaveBarsMax);
        Assert.Equal(1, PatternSpecConstants.ElliottImpulseMaxZeroRatioScores);
        Assert.Equal(5, PatternSpecConstants.ElliottRocLookbackBars);

        Assert.Equal(1.0m, PatternSpecConstants.ElliottImpulseWeightW2W1 + PatternSpecConstants.ElliottImpulseWeightW3W1 + PatternSpecConstants.ElliottImpulseWeightW4W3 + PatternSpecConstants.ElliottImpulseWeightW5W1);
        Assert.Equal(1.0m, PatternSpecConstants.ElliottCorrectiveWeightWBWA + PatternSpecConstants.ElliottCorrectiveWeightWCWA);

        Assert.Equal(206, PatternSpecConstants.GranvilleDefaultMinBars);
        Assert.Equal(1.20m, PatternSpecConstants.GranvilleRule1VolumeMultiplier);
    }
}
