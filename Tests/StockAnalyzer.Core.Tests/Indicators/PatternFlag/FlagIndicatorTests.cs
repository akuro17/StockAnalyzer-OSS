using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Formations;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Tests.PatternEngines;
using Xunit;
using static StockAnalyzer.Core.Tests.PatternEngines.FlagFixtures;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// The Flag as an indicator (plan ChartPatterns56 v2, run V2-4): the wiring of the parameter class to the engine, the output series, the minimum history of 35 bars and the parameter class (defaults of integration
/// spec 5.1, ranges, cross-validation). The rules themselves are tested in <c>PatternEngines/FlagEngineTests</c>.
/// </summary>
public class FlagIndicatorTests
{
    private static readonly IndicatorFactory Factory = new();

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    private static IIndicatorResult Calculate(IEnumerable<CandleData> bars, CoreIndicatorParameterBase? parameters = null)
    {
        IIndicatorResult result = Factory.Create(IndicatorType.PatternFlag, parameters)!.Calculate(ToCore(bars));
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return result;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARealFlag_LightsMainDirectionAndTheMatchingSideSeries(bool bullish)
    {
        Built built = Build(new FlagSpec { Bullish = bullish });
        IIndicatorResult result = Calculate(built.Bars);
        var main = result.GetSeries(IndicatorResult.MainSeriesName);
        var direction = result.GetSeries(IndicatorSeriesNames.PatternDirection);
        var bull = result.GetSeries(nameof(CorePatternFlagIndicator.BullishFlag));
        var bear = result.GetSeries(nameof(CorePatternFlagIndicator.BearishFlag));

        int hold = new CoreFlagPatternParameter().SignalPersistenceBars;
        for (int t = 0; t < built.Bars.Length; t++)
        {
            if (t + 1 < PatternSpecConstants.FlagMinBars)
            {
                Assert.Null(main[t]);
                Assert.Null(direction[t]);
                Assert.Null(bull[t]);
                Assert.Null(bear[t]);
                continue;
            }

            bool lit = t >= built.BreakBar && t <= built.BreakBar + hold;
            Assert.Equal(lit ? 1m : 0m, main[t]);
            Assert.Equal(lit ? (bullish ? 1m : -1m) : 0m, direction[t]);
            Assert.Equal(lit && bullish ? 1m : 0m, bull[t]);
            Assert.Equal(lit && !bullish ? -1m : 0m, bear[t]);
        }
    }

    [Fact]
    public void TheFirstValueIsAtBar35_AndAShorterSeriesIsNullEverywhere()
    {
        int nMin = PatternSpecConstants.FlagMinBars;
        Assert.Equal(nMin, PatternFlagIndicatorCatalog.Get(IndicatorType.PatternFlag).MinBars);
        Assert.Equal(PatternFlagEvaluationModel.FullSeries, PatternFlagIndicatorCatalog.Get(IndicatorType.PatternFlag).Model);

        IIndicatorResult enough = Calculate(PatternEngineTestBars.Walk(1, nMin));
        IIndicatorResult tooShort = Calculate(PatternEngineTestBars.Walk(1, nMin - 1));

        Assert.All(enough.GetSeries(IndicatorResult.MainSeriesName).Take(nMin - 1), v => Assert.Null(v));
        Assert.NotNull(enough.GetSeries(IndicatorResult.MainSeriesName)[nMin - 1]);
        Assert.All(tooShort.GetSeries(IndicatorResult.MainSeriesName), v => Assert.Null(v));
    }

    [Fact]
    public void TheParameterObject_ReachesTheEngine()
    {
        Built built = Build(new FlagSpec());

        Assert.Equal(1, Calculate(built.Bars, new CoreFlagPatternParameter { SignalPersistenceBars = 0 }).GetSeries(IndicatorResult.MainSeriesName).Count(v => v == 1m));
        Assert.DoesNotContain(1m, Calculate(built.Bars, new CoreFlagPatternParameter { DirectionMode = PatternDirectionMode.BearishOnly }).GetSeries(IndicatorResult.MainSeriesName));
        Assert.Contains(1m, Calculate(built.Bars).GetSeries(IndicatorResult.MainSeriesName));
        Assert.IsType<CoreFlagPatternParameter>(DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.PatternFlag).ParameterObject);
    }

    [Fact]
    public void TheDefaults_AreTheOnesOfTheIntegrationSpec()
    {
        var p = new CoreFlagPatternParameter();

        Assert.Equal(
            (PatternDirectionMode.Both, 3, 15, 8.0m, 3.5m, 0.60m, 5, 25, 0.70m, 0.15m, 0.10m, 0.50m, 0.35m, 2, true, 5),
            (p.DirectionMode, p.MinPoleBars, p.MaxPoleBars, p.MinPoleMovePercent, p.MinPoleAtrMultiplier, p.MinPoleBodyRatio, p.MinFlagBars, p.MaxFlagBars, p.MinRSquared, p.MaxSlopeDiffAtr, p.MinRetraceRatio,
             p.MaxRetraceRatio, p.MaxChannelWidthRatio, p.MinTouchCount, p.RequireVolumeConfirmation, p.SignalPersistenceBars));
        p.Validate();
    }

    public static IEnumerable<object[]> RangedProperties() => typeof(CoreFlagPatternParameter).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(pr => pr.GetCustomAttribute<CoreParameterRangeAttribute>() != null).Select(pr => new object[] { pr.Name });

    [Theory]
    [MemberData(nameof(RangedProperties))]
    public void EveryRangedProperty_AcceptsItsBounds_AndRejectsOutsideThem(string name)
    {
        PropertyInfo property = typeof(CoreFlagPatternParameter).GetProperty(name)!;
        var range = property.GetCustomAttribute<CoreParameterRangeAttribute>()!;
        bool isInt = property.PropertyType == typeof(int);
        object min = isInt ? (object)Convert.ToInt32(range.Minimum) : Convert.ToDecimal(range.Minimum);
        object max = isInt ? (object)Convert.ToInt32(range.Maximum) : Convert.ToDecimal(range.Maximum);
        object below = isInt ? (object)((int)min - 1) : (decimal)min - 0.0001m;
        object above = isInt ? (object)((int)max + 1) : (decimal)max + 0.0001m;

        foreach (object ok in new[] { min, max })
        {
            var p = new CoreFlagPatternParameter();
            property.SetValue(p, ok);
            if (CrossRulesAllow(p, name))
            {
                p.Validate();
            }
        }

        foreach (object bad in new[] { below, above })
        {
            var p = new CoreFlagPatternParameter();
            property.SetValue(p, bad);
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
        }
    }

    /// <summary>A bound of one setting can contradict the default of its partner (a smallest above the largest): then only the cross rule is under test, not the range.</summary>
    private static bool CrossRulesAllow(CoreFlagPatternParameter p, string name)
        => p.MinPoleBars <= p.MaxPoleBars && p.MinFlagBars <= p.MaxFlagBars && p.MinRetraceRatio <= p.MaxRetraceRatio;

    [Fact]
    public void CrossValidation_EveryMinIsAtMostItsMax()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFlagPatternParameter { MinPoleBars = 10, MaxPoleBars = 9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFlagPatternParameter { MinFlagBars = 15, MaxFlagBars = 14 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFlagPatternParameter { MinRetraceRatio = 0.3m, MaxRetraceRatio = 0.29m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFlagPatternParameter { DirectionMode = (PatternDirectionMode)9 }.Validate());
        new CoreFlagPatternParameter { MinPoleBars = 10, MaxPoleBars = 10, MinFlagBars = 15, MaxFlagBars = 15, MinRetraceRatio = 0.3m, MaxRetraceRatio = 0.3m }.Validate();
    }

    [Fact]
    public void TheLargestFlag_IsTheFitLimit_SoNoSettingCanSilentlyNeverFire()
    {
        Assert.Equal(StockAnalyzer.Core.MathUtils.RobustRegressionMath.MaxWindowLength, PatternFlagParameterLimits.FlagMaxFlagBarsMax);
        new CoreFlagPatternParameter { MaxFlagBars = PatternFlagParameterLimits.FlagMaxFlagBarsMax }.Validate();
    }

    [Fact]
    public void EveryPublicSettingHasADisplayNameADescriptionAndACategory()
    {
        foreach (PropertyInfo property in typeof(CoreFlagPatternParameter).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName), $"{property.Name} has no DisplayName");
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{property.Name} has no Description");
            Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<CategoryAttribute>()?.Category), $"{property.Name} has no Category");
        }
    }
}
