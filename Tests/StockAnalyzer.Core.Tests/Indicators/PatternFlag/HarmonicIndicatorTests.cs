using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Harmonics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Tests.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Indicator-level tests for Harmonic Gartley, Butterfly, and Bat (plan ChartPatterns56 v2, run V2-6; ledger rows <c>HI_*</c>):
/// null before <c>MinBars = 40</c>, named side series, type-specific defaults, parameter metadata and <c>Validate()</c>, and constants.
/// </summary>
public class HarmonicIndicatorTests
{
    private static readonly IndicatorFactory Factory = new();

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    private static IIndicatorResult Calculate(IndicatorType type, IEnumerable<CandleData> bars, CoreIndicatorParameterBase? parameters = null)
    {
        IIndicatorResult result = Factory.Create(type, parameters)!.Calculate(ToCore(bars));
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return result;
    }

    [Theory]
    [InlineData(IndicatorType.HarmonicGartley)]
    [InlineData(IndicatorType.HarmonicButterfly)]
    [InlineData(IndicatorType.HarmonicBat)]
    public void HI_NullBeforeNmin(IndicatorType type)
    {
        int nMin = PatternSpecConstants.HarmonicMinBars;
        Assert.Equal(40, nMin);
        Assert.Equal(nMin, PatternFlagIndicatorCatalog.Get(type).MinBars);
        Assert.Equal(PatternFlagEvaluationModel.FullSeries, PatternFlagIndicatorCatalog.Get(type).Model);

        IIndicatorResult enough = Calculate(type, PatternEngineTestBars.Walk(1, nMin));
        IIndicatorResult tooShort = Calculate(type, PatternEngineTestBars.Walk(1, nMin - 1));

        Assert.All(enough.GetSeries(IndicatorResult.MainSeriesName).Take(nMin - 1), v => Assert.Null(v));
        Assert.NotNull(enough.GetSeries(IndicatorResult.MainSeriesName)[nMin - 1]);
        Assert.All(tooShort.GetSeries(IndicatorResult.MainSeriesName), v => Assert.Null(v));
    }

    [Theory]
    [InlineData(IndicatorType.HarmonicGartley, true, nameof(CoreHarmonicGartleyIndicator.BullishGartley), nameof(CoreHarmonicGartleyIndicator.BearishGartley))]
    [InlineData(IndicatorType.HarmonicGartley, false, nameof(CoreHarmonicGartleyIndicator.BullishGartley), nameof(CoreHarmonicGartleyIndicator.BearishGartley))]
    [InlineData(IndicatorType.HarmonicButterfly, true, nameof(CoreHarmonicButterflyIndicator.BullishButterfly), nameof(CoreHarmonicButterflyIndicator.BearishButterfly))]
    [InlineData(IndicatorType.HarmonicButterfly, false, nameof(CoreHarmonicButterflyIndicator.BullishButterfly), nameof(CoreHarmonicButterflyIndicator.BearishButterfly))]
    [InlineData(IndicatorType.HarmonicBat, true, nameof(CoreHarmonicBatIndicator.BullishBat), nameof(CoreHarmonicBatIndicator.BearishBat))]
    [InlineData(IndicatorType.HarmonicBat, false, nameof(CoreHarmonicBatIndicator.BullishBat), nameof(CoreHarmonicBatIndicator.BearishBat))]
    public void HI_NamedSeries_LightOnExpectedSide(IndicatorType type, bool bullish, string bullSeries, string bearSeries)
    {
        HarmonicPatternKind kind = type switch
        {
            IndicatorType.HarmonicGartley => HarmonicPatternKind.Gartley,
            IndicatorType.HarmonicButterfly => HarmonicPatternKind.Butterfly,
            IndicatorType.HarmonicBat => HarmonicPatternKind.Bat,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        (CandleData[] bars, int confirmBar) = HarmonicPatternEngineTests.BuildIdealFixture(kind, bullish);
        IIndicatorResult result = Calculate(type, bars);

        IReadOnlyList<decimal?> main = result.GetSeries(IndicatorResult.MainSeriesName);
        IReadOnlyList<decimal?> dir = result.GetSeries(IndicatorSeriesNames.PatternDirection);
        IReadOnlyList<decimal?> bull = result.GetSeries(bullSeries);
        IReadOnlyList<decimal?> bear = result.GetSeries(bearSeries);

        Assert.Equal(1m, main[confirmBar]);
        Assert.Equal(bullish ? 1m : -1m, dir[confirmBar]);
        Assert.Equal(bullish ? 1m : 0m, bull[confirmBar]);
        Assert.Equal(bullish ? 0m : -1m, bear[confirmBar]);
    }

    [Fact]
    public void HI_TypeSpecificDefaults_MatchIntegrationSpec()
    {
        CoreHarmonicPatternParameter g = Assert.IsType<CoreHarmonicPatternParameter>(DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.HarmonicGartley).ParameterObject);
        CoreHarmonicPatternParameter bf = Assert.IsType<CoreHarmonicPatternParameter>(DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.HarmonicButterfly).ParameterObject);
        CoreHarmonicPatternParameter bt = Assert.IsType<CoreHarmonicPatternParameter>(DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.HarmonicBat).ParameterObject);

        Assert.Equal((0.55m, 0.68m, 0.33m, 0.93m, 1.15m, 1.75m, 0.74m, 0.84m), (g.AbXaMin, g.AbXaMax, g.BcAbMin, g.BcAbMax, g.CdBcMin, g.CdBcMax, g.AdXaMin, g.AdXaMax));
        Assert.Equal((0.72m, 0.85m, 0.33m, 0.93m, 1.50m, 2.75m, 1.20m, 1.68m), (bf.AbXaMin, bf.AbXaMax, bf.BcAbMin, bf.BcAbMax, bf.CdBcMin, bf.CdBcMax, bf.AdXaMin, bf.AdXaMax));
        Assert.Equal((0.33m, 0.55m, 0.33m, 0.93m, 1.50m, 2.75m, 0.84m, 0.95m), (bt.AbXaMin, bt.AbXaMax, bt.BcAbMin, bt.BcAbMax, bt.CdBcMin, bt.CdBcMax, bt.AdXaMin, bt.AdXaMax));

        foreach (CoreHarmonicPatternParameter p in new[] { g, bf, bt })
        {
            Assert.Equal((PatternDirectionMode.Both, 2.0m, 0.50m, 12, 120, 2, 45, 6.0m, 0.50m, true, 5, false),
                (p.DirectionMode, p.MinLegSwingPercent, p.MinLegAtrMultiplier, p.MinSpanBars, p.MaxSpanBars, p.MinLegBars, p.MaxLegBars, p.MaxTimeSymmetryRatio, p.MinConfidenceScore, p.RequireIntermediateCleanliness, p.MaxBarsSinceConfirmation, p.RequireReversalCandle));
            Assert.Equal(PatternFlagParameterLimits.WarmupBars, p.GetRequiredWarmupBars());
            p.Validate();
        }
    }

    public static IEnumerable<object[]> RangedProperties() => typeof(CoreHarmonicPatternParameter).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(pr => pr.GetCustomAttribute<CoreParameterRangeAttribute>() != null).Select(pr => new object[] { pr.Name });

    [Theory]
    [MemberData(nameof(RangedProperties))]
    public void HI_EveryRangedProperty_AcceptsItsBounds_AndRejectsOutsideThem(string name)
    {
        PropertyInfo property = typeof(CoreHarmonicPatternParameter).GetProperty(name)!;
        var range = property.GetCustomAttribute<CoreParameterRangeAttribute>()!;
        bool isInt = property.PropertyType == typeof(int);
        object min = isInt ? (object)Convert.ToInt32(range.Minimum) : Convert.ToDecimal(range.Minimum);
        object max = isInt ? (object)Convert.ToInt32(range.Maximum) : Convert.ToDecimal(range.Maximum);
        object below = isInt ? (object)((int)min - 1) : (decimal)min - 0.0001m;
        object above = isInt ? (object)((int)max + 1) : (decimal)max + 0.0001m;

        // Avoid failing Min <= Max cross checks while probing individual upper/lower bounds.
        static void AlignPair(CoreHarmonicPatternParameter p, string propName, object value)
        {
            if (propName == nameof(CoreHarmonicPatternParameter.MinSpanBars)) p.MaxSpanBars = Math.Max(p.MaxSpanBars, (int)value);
            if (propName == nameof(CoreHarmonicPatternParameter.MaxSpanBars)) p.MinSpanBars = Math.Min(p.MinSpanBars, (int)value);
            if (propName == nameof(CoreHarmonicPatternParameter.MinLegBars)) p.MaxLegBars = Math.Max(p.MaxLegBars, (int)value);
            if (propName == nameof(CoreHarmonicPatternParameter.MaxLegBars)) p.MinLegBars = Math.Min(p.MinLegBars, (int)value);
            if (propName == nameof(CoreHarmonicPatternParameter.AbXaMin)) p.AbXaMax = Math.Max(p.AbXaMax, (decimal)value);
            if (propName == nameof(CoreHarmonicPatternParameter.AbXaMax)) p.AbXaMin = Math.Min(p.AbXaMin, (decimal)value);
            if (propName == nameof(CoreHarmonicPatternParameter.BcAbMin)) p.BcAbMax = Math.Max(p.BcAbMax, (decimal)value);
            if (propName == nameof(CoreHarmonicPatternParameter.BcAbMax)) p.BcAbMin = Math.Min(p.BcAbMin, (decimal)value);
            if (propName == nameof(CoreHarmonicPatternParameter.CdBcMin)) p.CdBcMax = Math.Max(p.CdBcMax, (decimal)value);
            if (propName == nameof(CoreHarmonicPatternParameter.CdBcMax)) p.CdBcMin = Math.Min(p.CdBcMin, (decimal)value);
            if (propName == nameof(CoreHarmonicPatternParameter.AdXaMin)) p.AdXaMax = Math.Max(p.AdXaMax, (decimal)value);
            if (propName == nameof(CoreHarmonicPatternParameter.AdXaMax)) p.AdXaMin = Math.Min(p.AdXaMin, (decimal)value);
        }

        foreach (object ok in new[] { min, max })
        {
            var p = new CoreHarmonicPatternParameter();
            AlignPair(p, name, ok);
            property.SetValue(p, ok);
            p.Validate();
        }

        foreach (object bad in new[] { below, above })
        {
            var p = new CoreHarmonicPatternParameter();
            property.SetValue(p, bad);
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
        }
    }

    [Fact]
    public void HI_CrossValidation_RejectsInvertedMinMaxPairs_AndInvalidEnum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarmonicPatternParameter { MinSpanBars = 60, MaxSpanBars = 50 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarmonicPatternParameter { MinLegBars = 15, MaxLegBars = 10 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarmonicPatternParameter { AbXaMin = 0.70m, AbXaMax = 0.60m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarmonicPatternParameter { BcAbMin = 0.70m, BcAbMax = 0.60m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarmonicPatternParameter { CdBcMin = 2.00m, CdBcMax = 1.50m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarmonicPatternParameter { AdXaMin = 1.00m, AdXaMax = 0.90m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHarmonicPatternParameter { DirectionMode = (PatternDirectionMode)9 }.Validate());
    }

    [Fact]
    public void HI_EveryPublicSetting_HasMetadata()
    {
        foreach (PropertyInfo property in typeof(CoreHarmonicPatternParameter).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName), $"{property.Name} has no DisplayName");
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{property.Name} has no Description");
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<CategoryAttribute>()?.Category), $"{property.Name} has no Category");
        }
    }

    [Fact]
    public void HI_Constants_AreSpecValues()
    {
        Assert.Equal(40, PatternSpecConstants.HarmonicMinBars);
        Assert.Equal(40, PatternSpecConstants.HarmonicZoneBars);
        Assert.Equal(0.25m, PatternSpecConstants.HarmonicPointXZoneThreshold);
        Assert.Equal(0.15m, PatternSpecConstants.HarmonicButterflyPointDZoneThreshold);
        Assert.Equal(0.03m, PatternSpecConstants.HarmonicInitialLegMinFraction);
        Assert.Equal(2.5m, PatternSpecConstants.HarmonicInitialLegAtrMultiplier);
        Assert.Equal(new[] { 2, 3, 5, 8 }, PatternSpecConstants.HarmonicWindows);
        Assert.Equal(3.0m, PatternSpecConstants.HarmonicTimeSymmetryFloorBars);
        Assert.Equal(0.33m, PatternSpecConstants.HarmonicGartleyAbCdSymmetryMin);
        Assert.Equal(3.0m, PatternSpecConstants.HarmonicGartleyAbCdSymmetryMax);
        Assert.Equal(0.30m, PatternSpecConstants.HarmonicWeightAbXa);
        Assert.Equal(0.15m, PatternSpecConstants.HarmonicWeightBcAb);
        Assert.Equal(0.15m, PatternSpecConstants.HarmonicWeightCdBc);
        Assert.Equal(0.40m, PatternSpecConstants.HarmonicWeightAdXa);
        Assert.Equal(1.75m, PatternSpecConstants.HarmonicButterflyInvalidationExtensionMax);
    }
}
