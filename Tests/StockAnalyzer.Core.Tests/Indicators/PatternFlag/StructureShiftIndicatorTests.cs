using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Structure;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Tests.PatternEngines;
using Xunit;
using static StockAnalyzer.Core.Tests.PatternEngines.FormationFixtures;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Break of Structure and Change of Character as indicators (plan ChartPatterns56 v2, run V2-5): the wiring of the parameter class to the engine, the output series (Main, Direction and the two visible side series),
/// the minimum history of 20 bars and the parameter class (defaults of integration spec 5.2, ranges). The rules themselves are tested in <c>PatternEngines/StructureShiftEngineTests</c>.
/// </summary>
public class StructureShiftIndicatorTests
{
    private static readonly IndicatorFactory Factory = new();

    private static readonly (int Index, decimal Price)[] Anchors =
    {
        (0, 100m), (10, 120m), (20, 108m), (30, 130m), (40, 118m), (50, 145m), (60, 125m), (66, 120m), (67, 116m), (80, 116m),
    };

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    private static IIndicatorResult Calculate(IndicatorType type, IEnumerable<CandleData> bars, CoreIndicatorParameterBase? parameters = null)
    {
        IIndicatorResult result = Factory.Create(type, parameters)!.Calculate(ToCore(bars));
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return result;
    }

    private static CandleData[] Scenario() => FromAnchors(Anchors);

    [Fact]
    public void Bos_LightsTheBullishSideOnTheBreakBar_AndChoch_TheBearishSideOnItsBar()
    {
        CandleData[] bars = Scenario();
        IIndicatorResult bos = Calculate(IndicatorType.MarketStructureBos, bars);
        IIndicatorResult choch = Calculate(IndicatorType.MarketStructureChoch, bars);

        Assert.Equal(1m, bos.GetSeries(IndicatorResult.MainSeriesName)[45]);
        Assert.Equal(1m, bos.GetSeries(IndicatorSeriesNames.PatternDirection)[45]);
        Assert.Equal(1m, bos.GetSeries(nameof(CoreMarketStructureBosIndicator.BullishBos))[45]);
        Assert.Equal(0m, bos.GetSeries(nameof(CoreMarketStructureBosIndicator.BearishBos))[45]);
        Assert.Equal(1, bos.GetSeries(IndicatorResult.MainSeriesName).Count(v => v == 1m));
        Assert.Equal(0m, choch.GetSeries(IndicatorResult.MainSeriesName)[45]);

        Assert.Equal(1m, choch.GetSeries(IndicatorResult.MainSeriesName)[67]);
        Assert.Equal(-1m, choch.GetSeries(IndicatorSeriesNames.PatternDirection)[67]);
        Assert.Equal(-1m, choch.GetSeries(nameof(CoreMarketStructureChochIndicator.BearishChoch))[67]);
        Assert.Equal(0m, choch.GetSeries(nameof(CoreMarketStructureChochIndicator.BullishChoch))[67]);
        Assert.Equal(1, choch.GetSeries(IndicatorResult.MainSeriesName).Count(v => v == 1m));
        Assert.Equal(0m, bos.GetSeries(IndicatorResult.MainSeriesName)[67]);
    }

    [Fact]
    public void TheMirrorImage_SwapsTheSides()
    {
        CandleData[] bars = Mirror(Scenario());
        IIndicatorResult bos = Calculate(IndicatorType.MarketStructureBos, bars);
        IIndicatorResult choch = Calculate(IndicatorType.MarketStructureChoch, bars);

        Assert.Equal(-1m, bos.GetSeries(nameof(CoreMarketStructureBosIndicator.BearishBos))[45]);
        Assert.Equal(1m, choch.GetSeries(nameof(CoreMarketStructureChochIndicator.BullishChoch))[67]);
        Assert.Equal(-1m, bos.GetSeries(IndicatorSeriesNames.PatternDirection)[45]);
        Assert.Equal(1m, choch.GetSeries(IndicatorSeriesNames.PatternDirection)[67]);
    }

    [Theory]
    [InlineData(IndicatorType.MarketStructureBos)]
    [InlineData(IndicatorType.MarketStructureChoch)]
    public void TheFirstValueIsAtBar20_AndAShorterSeriesIsNullEverywhere(IndicatorType type)
    {
        int nMin = PatternSpecConstants.StructureShiftMinBars;
        Assert.Equal(20, nMin);
        Assert.Equal(nMin, PatternFlagIndicatorCatalog.Get(type).MinBars);
        Assert.Equal(PatternFlagEvaluationModel.FullSeries, PatternFlagIndicatorCatalog.Get(type).Model);

        IIndicatorResult enough = Calculate(type, PatternEngineTestBars.Walk(1, nMin));
        IIndicatorResult tooShort = Calculate(type, PatternEngineTestBars.Walk(1, nMin - 1));

        Assert.All(enough.GetSeries(IndicatorResult.MainSeriesName).Take(nMin - 1), v => Assert.Null(v));
        Assert.NotNull(enough.GetSeries(IndicatorResult.MainSeriesName)[nMin - 1]);
        Assert.All(tooShort.GetSeries(IndicatorResult.MainSeriesName), v => Assert.Null(v));
    }

    [Fact]
    public void TheParameterObject_ReachesTheEngine()
    {
        CandleData[] bars = Scenario();

        Assert.Equal(4, Calculate(IndicatorType.MarketStructureBos, bars, new CoreStructureShiftParameter { SignalPersistenceBars = 3 }).GetSeries(IndicatorResult.MainSeriesName).Count(v => v == 1m));
        Assert.DoesNotContain(1m, Calculate(IndicatorType.MarketStructureChoch, bars, new CoreStructureShiftParameter { DirectionMode = PatternDirectionMode.BullishOnly }).GetSeries(IndicatorResult.MainSeriesName));
        Assert.Contains(1m, Calculate(IndicatorType.MarketStructureChoch, bars).GetSeries(IndicatorResult.MainSeriesName));
        Assert.Contains(1m, Calculate(IndicatorType.MarketStructureChoch, bars, new CoreStructureShiftParameter { ZigZagThresholdPercent = 5m }).GetSeries(IndicatorResult.MainSeriesName));
        Assert.DoesNotContain(1m, Calculate(IndicatorType.MarketStructureChoch, bars, new CoreStructureShiftParameter { BreakoutMarginPercent = 3m }).GetSeries(IndicatorResult.MainSeriesName));
        Assert.IsType<CoreStructureShiftParameter>(DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.MarketStructureBos).ParameterObject);
    }

    [Fact]
    public void TheDefaults_AreTheOnesOfTheIntegrationSpec()
    {
        var p = new CoreStructureShiftParameter();

        Assert.Equal(
            (PatternDirectionMode.Both, 5.0m, 1.5m, 100, 0.0m, 0, false, 1.20m),
            (p.DirectionMode, p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, p.MaxPivotAgeBars, p.BreakoutMarginPercent, p.SignalPersistenceBars, p.RequireVolumeConfirmation, p.BreakoutVolumeMultiplier));
        p.Validate();
    }

    public static IEnumerable<object[]> RangedProperties() => typeof(CoreStructureShiftParameter).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(pr => pr.GetCustomAttribute<CoreParameterRangeAttribute>() != null).Select(pr => new object[] { pr.Name });

    [Theory]
    [MemberData(nameof(RangedProperties))]
    public void EveryRangedProperty_AcceptsItsBounds_AndRejectsOutsideThem(string name)
    {
        PropertyInfo property = typeof(CoreStructureShiftParameter).GetProperty(name)!;
        var range = property.GetCustomAttribute<CoreParameterRangeAttribute>()!;
        bool isInt = property.PropertyType == typeof(int);
        object min = isInt ? (object)Convert.ToInt32(range.Minimum) : Convert.ToDecimal(range.Minimum);
        object max = isInt ? (object)Convert.ToInt32(range.Maximum) : Convert.ToDecimal(range.Maximum);
        object below = isInt ? (object)((int)min - 1) : (decimal)min - 0.0001m;
        object above = isInt ? (object)((int)max + 1) : (decimal)max + 0.0001m;

        foreach (object ok in new[] { min, max })
        {
            var p = new CoreStructureShiftParameter();
            property.SetValue(p, ok);
            p.Validate();
        }

        foreach (object bad in new[] { below, above })
        {
            var p = new CoreStructureShiftParameter();
            property.SetValue(p, bad);
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
        }
    }

    [Fact]
    public void TheRanges_AreTheOnesOfTheIntegrationSpec()
    {
        (double Min, double Max) Range(string name)
        {
            var a = typeof(CoreStructureShiftParameter).GetProperty(name)!.GetCustomAttribute<CoreParameterRangeAttribute>()!;
            return (Convert.ToDouble(a.Minimum), Convert.ToDouble(a.Maximum));
        }

        Assert.Equal((0.5, 30.0), Range(nameof(CoreStructureShiftParameter.ZigZagThresholdPercent)));
        Assert.Equal((0.0, 5.0), Range(nameof(CoreStructureShiftParameter.MinSwingAtrMultiplier)));
        Assert.Equal((10.0, 500.0), Range(nameof(CoreStructureShiftParameter.MaxPivotAgeBars)));
        Assert.Equal((0.0, 3.0), Range(nameof(CoreStructureShiftParameter.BreakoutMarginPercent)));
        Assert.Equal((0.0, 50.0), Range(nameof(CoreStructureShiftParameter.SignalPersistenceBars)));
        Assert.Equal((1.0, 5.0), Range(nameof(CoreStructureShiftParameter.BreakoutVolumeMultiplier)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureShiftParameter { DirectionMode = (PatternDirectionMode)9 }.Validate());
    }

    [Fact]
    public void TheAgeLimitDescription_SaysThatItIsForTheBreakOfStructureOnly_R24()
    {
        string description = typeof(CoreStructureShiftParameter).GetProperty(nameof(CoreStructureShiftParameter.MaxPivotAgeBars))!.GetCustomAttribute<DescriptionAttribute>()!.Description;
        string margin = typeof(CoreStructureShiftParameter).GetProperty(nameof(CoreStructureShiftParameter.BreakoutMarginPercent))!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains("Break of Structure only", description);
        Assert.Contains("never expires", description);
        Assert.Contains("0 = no margin at all", margin);
        Assert.Contains("strict inequality", margin);
    }

    [Fact]
    public void EveryPublicSettingHasADisplayNameADescriptionAndACategory()
    {
        foreach (PropertyInfo property in typeof(CoreStructureShiftParameter).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName), $"{property.Name} has no DisplayName");
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{property.Name} has no Description");
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<CategoryAttribute>()?.Category), $"{property.Name} has no Category");
        }
    }

    [Fact]
    public void TheWarmupRequest_IsTheSharedFlagWarmup()
    {
        Assert.Equal(PatternFlagParameterLimits.WarmupBars, new CoreStructureShiftParameter().GetRequiredWarmupBars());
        Assert.True(PatternFlagParameterLimits.WarmupBars >= PatternSpecConstants.StructureShiftMinBars);
    }
}
