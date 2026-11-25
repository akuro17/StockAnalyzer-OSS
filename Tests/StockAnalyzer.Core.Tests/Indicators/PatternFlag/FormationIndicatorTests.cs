using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Formations;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Tests.PatternEngines;
using Xunit;
using static StockAnalyzer.Core.Tests.PatternEngines.FormationFixtures;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Head and Shoulders and Double Top / Bottom as indicators (plan ChartPatterns56 v2, run V2-3): the wiring of the parameter classes to the engines, the output series of a real pattern, the minimum history of
/// 60 bars, and the parameter classes (defaults of integration spec 5.1, ranges, cross-validation). The rules themselves are tested in <c>PatternEngines/HeadAndShouldersEngineTests</c> and
/// <c>DoubleTopBottomEngineTests</c>.
/// </summary>
public class FormationIndicatorTests
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

    // ---------------------------------------------------------------- output of a real pattern

    [Theory]
    [InlineData(IndicatorType.PatternDoubleTopBottom, true)]
    [InlineData(IndicatorType.PatternDoubleTopBottom, false)]
    [InlineData(IndicatorType.PatternHeadAndShoulders, true)]
    [InlineData(IndicatorType.PatternHeadAndShoulders, false)]
    public void ARealPattern_LightsMainDirectionAndTheMatchingSideSeries(IndicatorType type, bool top)
    {
        Built built = type == IndicatorType.PatternDoubleTopBottom ? Double(new DoubleSpec { Top = top }) : HeadAndShoulders(new HeadSpec { Top = top });
        PatternFlagSpec row = PatternFlagIndicatorCatalog.Get(type);

        IIndicatorResult result = Calculate(type, built.Bars);
        var main = result.GetSeries(IndicatorResult.MainSeriesName);
        var direction = result.GetSeries(IndicatorSeriesNames.PatternDirection);
        var bull = result.GetSeries(row.BullishSeries!);
        var bear = result.GetSeries(row.BearishSeries!);

        int hold = new CoreDoubleTopBottomParameter().SignalPersistenceBars;
        for (int t = 0; t < built.Bars.Length; t++)
        {
            if (t + 1 < PatternSpecConstants.FormationMinBars)
            {
                Assert.Null(main[t]);
                Assert.Null(direction[t]);
                Assert.Null(bull[t]);
                Assert.Null(bear[t]);
                continue;
            }

            bool lit = t >= built.BreakBar && t <= built.BreakBar + hold;
            Assert.Equal(lit ? 1m : 0m, main[t]);
            Assert.Equal(lit ? (top ? -1m : 1m) : 0m, direction[t]);
            Assert.Equal(lit && !top ? 1m : 0m, bull[t]);
            Assert.Equal(lit && top ? -1m : 0m, bear[t]);
        }
    }

    [Fact]
    public void BothSidesCanBeLitOnOneBar_AndMainIsOneWithTheTieBrokenDirection()
    {
        // The side flags are independent (integration spec 4.2): a canned engine answer with both sides on one bar shows both series lit and the bullish direction (equal confirmation: bullish).
        var indicator = new BothSidesIndicator();
        CoreCandleData[] bars = ToCore(PatternEngineTestBars.Walk(1, BothSidesIndicator.Bars));

        IIndicatorResult result = indicator.Calculate(bars);

        int t = BothSidesIndicator.LitBar;
        Assert.Equal(1m, result.GetSeries(IndicatorResult.MainSeriesName)[t]);
        Assert.Equal(1m, result.GetSeries(IndicatorSeriesNames.PatternDirection)[t]);
        Assert.Equal(1m, result.GetSeries(nameof(CorePatternHeadAndShouldersIndicator.InverseBottom))[t]);
        Assert.Equal(-1m, result.GetSeries(nameof(CorePatternHeadAndShouldersIndicator.Top))[t]);
        Assert.Null(result.GetSeries(IndicatorResult.MainSeriesName)[PatternSpecConstants.FormationMinBars - 2]);
        Assert.Equal(0m, result.GetSeries(IndicatorResult.MainSeriesName)[PatternSpecConstants.FormationMinBars - 1]);
    }

    private sealed class BothSidesIndicator : CorePatternEngineIndicatorBase
    {
        public const int Bars = 100;
        public const int LitBar = 80;

        public BothSidesIndicator()
            : base(PatternFlagIndicatorCatalog.Get(IndicatorType.PatternHeadAndShoulders))
        {
        }

        protected override PatternBarResult[] RunEngine(PatternSeriesContext context, CancellationToken cancellationToken)
        {
            var results = new PatternBarResult[context.Count];
            results[LitBar] = new PatternBarResult(true, true, 1, null, 70, 0);
            results[10] = new PatternBarResult(true, false, 1, null, 5, 0); // before the minimum history: the base writes null
            return results;
        }
    }

    // ---------------------------------------------------------------- minimum history and the parameter objects

    [Theory]
    [InlineData(IndicatorType.PatternHeadAndShoulders)]
    [InlineData(IndicatorType.PatternDoubleTopBottom)]
    public void TheFirstValueIsAtBar60_AndAShorterSeriesIsNullEverywhere(IndicatorType type)
    {
        int nMin = PatternSpecConstants.FormationMinBars;
        Assert.Equal(nMin, PatternFlagIndicatorCatalog.Get(type).MinBars);

        IIndicatorResult enough = Calculate(type, PatternEngineTestBars.Walk(1, nMin));
        IIndicatorResult tooShort = Calculate(type, PatternEngineTestBars.Walk(1, nMin - 1));

        Assert.All(enough.GetSeries(IndicatorResult.MainSeriesName).Take(nMin - 1), v => Assert.Null(v));
        Assert.NotNull(enough.GetSeries(IndicatorResult.MainSeriesName)[nMin - 1]);
        Assert.All(tooShort.GetSeries(IndicatorResult.MainSeriesName), v => Assert.Null(v));
    }

    [Theory]
    [InlineData(IndicatorType.PatternHeadAndShoulders)]
    [InlineData(IndicatorType.PatternDoubleTopBottom)]
    public void TheParameterObject_ReachesTheEngine(IndicatorType type)
    {
        Built built = type == IndicatorType.PatternDoubleTopBottom ? Double(new DoubleSpec()) : HeadAndShoulders(new HeadSpec());
        CoreIndicatorParameterBase persistence0 = type == IndicatorType.PatternDoubleTopBottom
            ? new CoreDoubleTopBottomParameter { SignalPersistenceBars = 0 }
            : new CoreHeadAndShouldersParameter { SignalPersistenceBars = 0 };
        CoreIndicatorParameterBase bullishOnly = type == IndicatorType.PatternDoubleTopBottom
            ? new CoreDoubleTopBottomParameter { DirectionMode = PatternDirectionMode.BullishOnly }
            : new CoreHeadAndShouldersParameter { DirectionMode = PatternDirectionMode.BullishOnly };

        Assert.Equal(1, Calculate(type, built.Bars, persistence0).GetSeries(IndicatorResult.MainSeriesName).Count(v => v == 1m));
        Assert.Equal(1 + new CoreDoubleTopBottomParameter().SignalPersistenceBars, Calculate(type, built.Bars).GetSeries(IndicatorResult.MainSeriesName).Count(v => v == 1m));
        Assert.DoesNotContain(1m, Calculate(type, built.Bars, bullishOnly).GetSeries(IndicatorResult.MainSeriesName));
    }

    [Fact]
    public void ADefaultParameterObjectOfTheCatalog_IsTheClassOfTheRow()
    {
        Assert.IsType<CoreHeadAndShouldersParameter>(DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.PatternHeadAndShoulders).ParameterObject);
        Assert.IsType<CoreDoubleTopBottomParameter>(DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.PatternDoubleTopBottom).ParameterObject);
    }

    // ---------------------------------------------------------------- the defaults of integration spec 5.1

    [Fact]
    public void HeadAndShouldersDefaults_AreTheOnesOfTheIntegrationSpec()
    {
        var p = new CoreHeadAndShouldersParameter();

        Assert.Equal(
            (PatternDirectionMode.Both, 3.0m, 1.5m, 3, true, 30, 15.0m, 60, 0.85m, 20, 180, 5, 60, 30, 5.0m, 0.15m, 5.0m, 0.35m, 0.5m, true, 1.25m, 5),
            (p.DirectionMode, p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, p.FractalWindowBars, p.RequirePriorTrend, p.PriorTrendLookbackBars, p.PriorTrendMinPercent, p.PriceZoneLookbackBars, p.PriceZoneThreshold,
             p.MinTotalBars, p.MaxTotalBars, p.MinPeakIntervalBars, p.MaxPeakIntervalBars, p.MaxBreakoutWaitBars, p.ShoulderTolerancePercent, p.MinHeadProminenceRatio, p.MinNecklineDepthPercent, p.MaxNecklineSlopeRatio,
             p.BreakoutMarginPercent, p.RequireVolumeConfirmation, p.BreakoutVolumeMultiplier, p.SignalPersistenceBars));
    }

    [Fact]
    public void DoubleTopBottomDefaults_AreTheOnesOfTheIntegrationSpec()
    {
        var p = new CoreDoubleTopBottomParameter();

        Assert.Equal(
            (PatternDirectionMode.Both, 3.0m, 1.5m, 3, true, 30, 10.0m, 60, 0.85m, 0.80m, 10, 65, 25, 3.0m, 6.0m, 0.65m, 0.5m, true, 1.20m, 5),
            (p.DirectionMode, p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, p.FractalWindowBars, p.RequirePriorTrend, p.PriorTrendLookbackBars, p.PriorTrendMinPercent, p.PriceZoneLookbackBars, p.Peak1ZoneThreshold,
             p.Peak2ZoneThreshold, p.MinPeakIntervalBars, p.MaxPeakIntervalBars, p.MaxBreakoutWaitBars, p.PeakTolerancePercent, p.MinNecklineDepthPercent, p.MaxNecklineRetraceRatio, p.BreakoutMarginPercent,
             p.RequireVolumeConfirmation, p.BreakoutVolumeMultiplier, p.SignalPersistenceBars));
    }

    [Fact]
    public void BothDefaultObjects_Validate()
    {
        new CoreHeadAndShouldersParameter().Validate();
        new CoreDoubleTopBottomParameter().Validate();
    }

    // ---------------------------------------------------------------- ranges (every attribute range is the range Validate enforces) and cross validation

    public static IEnumerable<object[]> RangedProperties()
    {
        foreach (Type type in new[] { typeof(CoreHeadAndShouldersParameter), typeof(CoreDoubleTopBottomParameter) })
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(pr => pr.GetCustomAttribute<CoreParameterRangeAttribute>() != null))
            {
                yield return new object[] { type, property.Name };
            }
        }
    }

    private static object Convert(PropertyInfo property, object bound)
        => property.PropertyType == typeof(int) ? System.Convert.ToInt32(bound) : (object)System.Convert.ToDecimal(bound);

    private static CoreIndicatorParameterBase Fresh(Type type)
    {
        // The fractal of 1 bar and the widest cross-rule room let every single bound be tested alone (the wait must not be shorter than the fractal).
        var p = (CoreIndicatorParameterBase)Activator.CreateInstance(type)!;
        ((CoreFormationPatternParameterBase)p).FractalWindowBars = 1;
        return p;
    }

    [Theory]
    [MemberData(nameof(RangedProperties))]
    public void EveryRangedProperty_AcceptsItsBounds_AndRejectsOutsideThem(Type type, string name)
    {
        PropertyInfo property = type.GetProperty(name)!;
        var range = property.GetCustomAttribute<CoreParameterRangeAttribute>()!;
        object min = Convert(property, range.Minimum);
        object max = Convert(property, range.Maximum);
        object below = property.PropertyType == typeof(int) ? (object)((int)min - 1) : (decimal)min - 0.0001m;
        object above = property.PropertyType == typeof(int) ? (object)((int)max + 1) : (decimal)max + 0.0001m;

        foreach (object ok in new[] { min, max })
        {
            CoreIndicatorParameterBase p = Fresh(type);
            property.SetValue(p, ok);
            p.Validate();
        }

        foreach (object bad in new[] { below, above })
        {
            CoreIndicatorParameterBase p = Fresh(type);
            property.SetValue(p, bad);
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
        }
    }

    [Fact]
    public void CrossValidation_TheBreakoutWaitCannotBeShorterThanTheFractal_AndEveryMinIsAtMostItsMax()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { FractalWindowBars = 5, MaxBreakoutWaitBars = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreDoubleTopBottomParameter { FractalWindowBars = 10, MaxBreakoutWaitBars = 9 }.Validate());
        new CoreHeadAndShouldersParameter { FractalWindowBars = 5, MaxBreakoutWaitBars = 5 }.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { MinTotalBars = 100, MaxTotalBars = 99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { MinPeakIntervalBars = 30, MaxPeakIntervalBars = 29 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { DirectionMode = (PatternDirectionMode)9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreDoubleTopBottomParameter { MinPeakIntervalBars = 40, MaxPeakIntervalBars = 39 }.Validate());
        new CoreHeadAndShouldersParameter { MinTotalBars = 100, MaxTotalBars = 100 }.Validate();
        new CoreDoubleTopBottomParameter { MinPeakIntervalBars = 40, MaxPeakIntervalBars = 40 }.Validate();
    }

    [Fact]
    public void EveryPublicSettingHasADisplayNameADescriptionAndACategory()
    {
        foreach (Type type in new[] { typeof(CoreHeadAndShouldersParameter), typeof(CoreDoubleTopBottomParameter) })
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Concat(typeof(CoreFormationPatternParameterBase).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
            {
                Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName), $"{type.Name}.{property.Name} has no DisplayName");
                Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{type.Name}.{property.Name} has no Description");
                Assert.False(string.IsNullOrWhiteSpace(property.GetCustomAttribute<CategoryAttribute>()?.Category), $"{type.Name}.{property.Name} has no Category");
            }
        }
    }

    [Fact]
    public void TheTwoDescriptionsOfTheMarginAndTheRetraceStateTheirRules()
    {
        string margin = typeof(CoreFormationPatternParameterBase).GetProperty(nameof(CoreFormationPatternParameterBase.BreakoutMarginPercent))!.GetCustomAttribute<DescriptionAttribute>()!.Description;
        string retrace = typeof(CoreDoubleTopBottomParameter).GetProperty(nameof(CoreDoubleTopBottomParameter.MaxNecklineRetraceRatio))!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains("0 = no margin", margin);
        Assert.Contains("strict", margin);
        Assert.Contains("Require Prior Trend", retrace);
    }
}
