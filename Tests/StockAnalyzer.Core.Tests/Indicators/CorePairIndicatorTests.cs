#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// Pair Ratio (A / B) and Pair Spread (A - B): A is the chart symbol, B the comparison symbol, one value per chart bar.
/// Expected values are the exact decimals of the specification (docs/sa_ai_context_PairRatioSpread, section 1.1 / 1.2).
/// </summary>
public class CorePairIndicatorTests
{
    private static readonly DateTime Start = new(2024, 1, 1);

    private const string Symbol = "SPY";

    private static CoreCandleData Bar(int dayOffset, decimal close)
        => new(Start.AddDays(dayOffset), close, close, close, close, 1000);

    private static List<CoreCandleData> Bars(params decimal[] closes)
        => closes.Select((close, i) => Bar(i, close)).ToList();

    private static List<CoreCandleData?> SecondaryBars(params decimal[] closes)
        => closes.Select((close, i) => (CoreCandleData?)Bar(i, close)).ToList();

    private static IReadOnlyList<decimal?> Run(CorePairIndicatorBase indicator, IReadOnlyList<CoreCandleData> primary, IReadOnlyList<CoreCandleData?>? secondary, string symbol = Symbol)
    {
        indicator.ComparisonSymbol = symbol;
        indicator.SetSecondaryCandles(secondary);
        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return result.MainValues.ToList();
    }

    private static decimal?[] Ratio(IReadOnlyList<CoreCandleData> primary, IReadOnlyList<CoreCandleData?>? secondary, string symbol = Symbol)
        => Run(new CorePairRatioIndicator(), primary, secondary, symbol).ToArray();

    private static decimal?[] Spread(IReadOnlyList<CoreCandleData> primary, IReadOnlyList<CoreCandleData?>? secondary, string symbol = Symbol)
        => Run(new CorePairSpreadIndicator(), primary, secondary, symbol).ToArray();

    // ---- registration -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(IndicatorType.PairRatio, typeof(CorePairRatioIndicator))]
    [InlineData(IndicatorType.PairSpread, typeof(CorePairSpreadIndicator))]
    public void Factory_CreatesTheIndicatorOfTheType_AsACrossTickerIndicator(IndicatorType type, Type expected)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        ICoreIndicator? indicator = IndicatorFactory.Default.Create(type);

        Assert.NotNull(indicator);
        Assert.IsType(expected, indicator);
        Assert.IsAssignableFrom<ICrossTickerIndicator>(indicator);
        Assert.False(Assert.IsAssignableFrom<CorePairIndicatorBase>(indicator).IsOverlay);
    }

    [Theory]
    [InlineData(IndicatorType.PairRatio, "Pair Ratio")]
    [InlineData(IndicatorType.PairSpread, "Pair Spread")]
    public void TheDescriptionOfTheTypeIsTheLabel(IndicatorType type, string expected)
        => Assert.Equal(expected, type.GetDescription());

    // ---- values: V1, V2 ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void V1_RatioAndSpread_OfTwoSeries_AreAOverBAndAMinusB_WithNoRatioWhereBIsZero()
    {
        List<CoreCandleData> a = Bars(100m, 200m, 150m);
        List<CoreCandleData?> b = SecondaryBars(50m, 50m, 0m);

        Assert.Equal(new decimal?[] { 2m, 4m, null }, Ratio(a, b));
        Assert.Equal(new decimal?[] { 50m, 150m, 150m }, Spread(a, b));
    }

    [Fact]
    public void V2_Spread_IsPositive_WhenAIsAboveB_AndRatioIsTheExactDecimalQuotient()
    {
        List<CoreCandleData> a = Bars(100m, 105m);
        List<CoreCandleData?> b = SecondaryBars(90m, 92m);

        Assert.Equal(new decimal?[] { 10m, 13m }, Spread(a, b));
        Assert.Equal(new decimal?[] { 100m / 90m, 105m / 92m }, Ratio(a, b));
    }

    // ---- V3: relation to Relative Performance (comparison symbol over chart symbol) ----------------------------------------------------

    private static ComparisonSeriesResult RelativePerformance(decimal[] chart, decimal[] comparison, ComparisonMode mode)
    {
        DateTime[] timestamps = chart.Select((_, i) => Start.AddDays(i)).ToArray();
        CandleData?[] Series(decimal[] closes) => closes.Select((c, i) => (CandleData?)new CandleData(Start.AddDays(i), c, c, c, c, 1000)).ToArray();
        var data = new ComparisonAlignedData("P", timestamps,
            new Dictionary<string, CandleData?[]> { ["P"] = Series(chart), ["S"] = Series(comparison) }, Array.Empty<string>());
        return ComparisonSeriesCalculator.Calculate(data, 0, chart.Length, mode, ComparisonSeriesDefaults.DefaultZScorePeriod);
    }

    [Fact]
    public void V3_PairRatio_IsTheReciprocalOfTheRelativePerformanceRatio()
    {
        decimal[] chart = { 100m, 200m };
        decimal[] comparison = { 50m, 50m };

        decimal?[] rp = RelativePerformance(chart, comparison, ComparisonMode.Ratio).Series["S"].Values;
        decimal?[] pair = Ratio(Bars(chart), SecondaryBars(comparison));

        Assert.Equal(new decimal?[] { 0.5m, 0.25m }, rp);
        Assert.Equal(new decimal?[] { 2m, 4m }, pair);
        for (int i = 0; i < pair.Length; i++)
        {
            Assert.Equal(1m / rp[i]!.Value, pair[i]);
        }
    }

    [Fact]
    public void V3_PairSpread_IsTheNegationOfTheRelativePerformanceSpread()
    {
        decimal[] chart = { 100m, 105m };
        decimal[] comparison = { 90m, 92m };

        decimal?[] rp = RelativePerformance(chart, comparison, ComparisonMode.Spread).Series["S"].Values;
        decimal?[] pair = Spread(Bars(chart), SecondaryBars(comparison));

        Assert.Equal(new decimal?[] { -10m, -13m }, rp);
        Assert.Equal(new decimal?[] { 10m, 13m }, pair);
        for (int i = 0; i < pair.Length; i++)
        {
            Assert.Equal(-rp[i]!.Value, pair[i]);
        }
    }

    [Fact]
    public void TheIndicatorsUseTheSharedRelativePerformanceFunctions_SoThereIsOneFormulaDefinition()
    {
        decimal[] chart = { 100m, 7.5m, 0m, -4m, 33.333m };
        decimal[] comparison = { 3m, 0m, 5m, -8m, 0.001m };

        decimal?[] ratio = Ratio(Bars(chart), SecondaryBars(comparison));
        decimal?[] spread = Spread(Bars(chart), SecondaryBars(comparison));

        for (int i = 0; i < chart.Length; i++)
        {
            Assert.Equal(ComparisonSeriesCalculator.RatioValue(chart[i], comparison[i]), ratio[i]);
            Assert.Equal(ComparisonSeriesCalculator.SpreadValue(chart[i], comparison[i]), spread[i]);
        }
    }

    // ---- edge cases E1 - E16 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void E1_NullCandles_IsAFailure()
    {
        IIndicatorResult result = new CorePairRatioIndicator { ComparisonSymbol = Symbol }.Calculate(null!);

        Assert.False(result.IsSuccessful);
        Assert.Equal("Candles data is null.", result.ErrorMessage);
    }

    [Fact]
    public void E2_NoCandles_IsAnEmptySuccess()
    {
        var indicator = new CorePairSpreadIndicator { ComparisonSymbol = Symbol };
        indicator.SetSecondaryCandles(SecondaryBars(1m));

        IIndicatorResult result = indicator.Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void E3_ASingleBar_GivesOneValue()
    {
        Assert.Equal(new decimal?[] { 4m }, Ratio(Bars(8m), SecondaryBars(2m)));
        Assert.Equal(new decimal?[] { 6m }, Spread(Bars(8m), SecondaryBars(2m)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void E4_ABlankComparisonSymbol_GivesNoValue_EvenWhenCandlesAreSet(string symbol)
    {
        List<CoreCandleData> a = Bars(10m, 20m);
        List<CoreCandleData?> b = SecondaryBars(5m, 5m);

        Assert.Equal(new decimal?[] { null, null }, Ratio(a, b, symbol));
        Assert.Equal(new decimal?[] { null, null }, Spread(a, b, symbol));
    }

    [Fact]
    public void E5_NoSecondaryCandles_GivesNoValue()
    {
        Assert.Equal(new decimal?[] { null, null }, Ratio(Bars(10m, 20m), null));
        Assert.Equal(new decimal?[] { null, null }, Spread(Bars(10m, 20m), null));
    }

    [Fact]
    public void E6_EmptySecondaryCandles_GivesNoValue()
    {
        Assert.Equal(new decimal?[] { null, null }, Ratio(Bars(10m, 20m), new List<CoreCandleData?>()));
        Assert.Equal(new decimal?[] { null, null }, Spread(Bars(10m, 20m), new List<CoreCandleData?>()));
    }

    [Fact]
    public void E7_OnlyTheBarsWithTheSameDate_HaveAValue_AndNothingIsFilled()
    {
        List<CoreCandleData> a = Bars(10m, 20m, 30m, 40m, 50m);
        var b = new List<CoreCandleData?> { Bar(1, 5m), Bar(3, 8m) };

        Assert.Equal(new decimal?[] { null, 4m, null, 5m, null }, Ratio(a, b));
        Assert.Equal(new decimal?[] { null, 15m, null, 32m, null }, Spread(a, b));
    }

    [Fact]
    public void E8_ANullElementOfTheSecondaryCandles_GivesNoValueAtThatBar()
    {
        List<CoreCandleData> a = Bars(10m, 20m, 30m);
        var b = new List<CoreCandleData?> { Bar(0, 5m), null, Bar(2, 10m) };

        Assert.Equal(new decimal?[] { 2m, null, 3m }, Ratio(a, b));
        Assert.Equal(new decimal?[] { 5m, null, 20m }, Spread(a, b));
    }

    [Theory]
    // a, b, expected ratio, expected spread
    [InlineData(10, 0, null, 10)]      // E9: B == 0 -> no ratio, the spread is still defined
    [InlineData(0, 5, 0, -5)]          // E10: A == 0, B != 0
    [InlineData(10, -5, -2, 15)]       // E11: negative B is computed normally
    [InlineData(-10, 5, -2, -15)]      // E11: negative A is computed normally
    [InlineData(-10, -5, 2, -5)]
    public void E9toE11_ZeroAndNegativeValues(int a, int b, int? expectedRatio, int expectedSpread)
    {
        decimal? expectedRatioValue = expectedRatio;

        Assert.Equal(new[] { expectedRatioValue }, Ratio(Bars(a), SecondaryBars(b)));
        Assert.Equal(new decimal?[] { expectedSpread }, Spread(Bars(a), SecondaryBars(b)));
    }

    [Fact]
    public void E12_ADecimalOverflow_GivesNoValueAtThatBarOnly()
    {
        // decimal.MaxValue / 0.5 and decimal.MaxValue - (-1) overflow; the following bar is unaffected.
        List<CoreCandleData> a = Bars(decimal.MaxValue, 1m);

        Assert.Equal(new decimal?[] { null, 1m }, Ratio(a, SecondaryBars(0.5m, 1m)));
        Assert.Equal(new decimal?[] { null, 0m }, Spread(a, SecondaryBars(-1m, 1m)));
    }

    [Fact]
    public void E13_CalculatingTwice_GivesTheSameValues_WithoutAccumulation()
    {
        List<CoreCandleData> a = Bars(100m, 200m, 150m);
        List<CoreCandleData?> b = SecondaryBars(50m, 50m, 30m);
        var indicator = new CorePairRatioIndicator { ComparisonSymbol = Symbol };
        indicator.SetSecondaryCandles(b);

        decimal?[] first = indicator.Calculate(a).MainValues.ToArray();
        decimal?[] second = indicator.Calculate(a).MainValues.ToArray();

        Assert.Equal(new decimal?[] { 2m, 4m, 5m }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void E14_ASingleInputSeries_HasNoSecondSeries_SoNoBarHasAValue()
    {
        var indicator = new CorePairRatioIndicator { ComparisonSymbol = Symbol };
        indicator.SetSecondaryCandles(SecondaryBars(1m, 2m, 3m));

        IIndicatorResult result = indicator.CalculateSeries(new List<decimal?> { 1m, 2m, 3m });

        Assert.True(result.IsSuccessful);
        Assert.Equal(new decimal?[] { null, null, null }, result.MainValues.ToArray());
        Assert.Empty(new CorePairSpreadIndicator().CalculateSeries(new List<decimal?>()).MainValues);
    }

    [Fact]
    public void E15_ComparingASymbolWithItself_GivesRatioOneAndSpreadZero()
    {
        List<CoreCandleData> a = Bars(12.5m, 40m, 7m);
        List<CoreCandleData?> same = a.Select(c => (CoreCandleData?)c).ToList();

        Assert.Equal(new decimal?[] { 1m, 1m, 1m }, Ratio(a, same));
        Assert.Equal(new decimal?[] { 0m, 0m, 0m }, Spread(a, same));
    }

    [Fact]
    public void E16_EachSideUsesItsOwnPriceType()
    {
        var a = new List<CoreCandleData> { new(Start, 100m, 110m, 90m, 100m, 1) };
        var b = new List<CoreCandleData?> { new CoreCandleData(Start, 50m, 60m, 40m, 50m, 1) };

        var ratio = new CorePairRatioIndicator { PriceSource = PriceType.High, ComparisonPriceSource = PriceType.Close };
        Assert.Equal(new decimal?[] { 2.2m }, Run(ratio, a, b).ToArray());

        var spread = new CorePairSpreadIndicator { PriceSource = PriceType.Close, ComparisonPriceSource = PriceType.Low };
        Assert.Equal(new decimal?[] { 60m }, Run(spread, a, b).ToArray());
    }

    [Fact]
    public void TheOutputHasOneValuePerChartBar_WhateverTheSecondaryLength()
    {
        List<CoreCandleData> a = Bars(1m, 2m, 3m, 4m);

        Assert.Equal(a.Count, Ratio(a, SecondaryBars(1m, 1m, 1m, 1m)).Length);
        Assert.Equal(a.Count, Spread(a, SecondaryBars(1m)).Length);
        Assert.Equal(a.Count, Spread(a, SecondaryBars(1m, 1m, 1m, 1m, 1m, 1m)).Length);
    }

    // ---- registration in the application's defaults and in the backtest/screener eligibility rule -------------------------------------------

    [Fact]
    public void DefaultSettings_OfPairRatio_AreADisabledSubPanelLineWithoutZeroBaseline()
    {
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.PairRatio);

        Assert.IsType<CorePairRatioParameter>(settings.ParameterObject);
        Assert.Equal(CoreIndicatorCategory.Other, settings.Category);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.False(settings.IncludeZeroBaseline);
        Assert.Equal(IndicatorDefaultConstants.PairRatioColor, settings.Color);
        SeriesColorConfig series = Assert.Single(settings.SeriesColors);
        Assert.Equal("Values", series.Name);
        Assert.Equal("Pair Ratio (A/B)", series.DisplayName);
        Assert.Equal(IndicatorDefaultConstants.PairRatioColor, series.Color);
        Assert.Equal(new[] { "Main" }, series.TargetSeries);
    }

    [Fact]
    public void DefaultSettings_OfPairSpread_AreADisabledSubPanelLineWithTheZeroBaseline()
    {
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.PairSpread);

        Assert.IsType<CorePairSpreadParameter>(settings.ParameterObject);
        Assert.Equal(CoreIndicatorCategory.Other, settings.Category);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.Equal(IndicatorDefaultConstants.PairSpreadColor, settings.Color);
        SeriesColorConfig series = Assert.Single(settings.SeriesColors);
        Assert.Equal("Values", series.Name);
        Assert.Equal("Pair Spread (A-B)", series.DisplayName);
        Assert.Equal(IndicatorDefaultConstants.PairSpreadColor, series.Color);
        Assert.Equal(new[] { "Main" }, series.TargetSeries);
    }

    [Theory]
    [InlineData(IndicatorType.PairRatio)]
    [InlineData(IndicatorType.PairSpread)]
    public void TheTypes_AreRefusedByTheBacktestEligibilityRule_AsNotEvaluableWithoutAComparisonSymbol(IndicatorType type)
    {
        Assert.Equal(BacktestIndicatorViolationReason.SynchronousCalculationUnsupported, BacktestIndicatorEligibility.Check(type, null));
        Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(type));
        Assert.Contains(type, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
    }

    // ---- parameters, names ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Configure_TakesTheThreeSettingsOfThePairParameter_AndIgnoresAForeignParameter()
    {
        var indicator = new CorePairRatioIndicator();
        indicator.Configure(new CorePairRatioParameter { PriceSource = PriceType.High, ComparisonSymbol = " spy ", ComparisonPriceSource = PriceType.Low });

        Assert.Equal(PriceType.High, indicator.PriceSource);
        Assert.Equal("SPY", indicator.ComparisonSymbol);
        Assert.Equal(PriceType.Low, indicator.ComparisonPriceSource);

        indicator.Configure(new CoreSmaParameter());

        Assert.Equal(PriceType.High, indicator.PriceSource);
        Assert.Equal("SPY", indicator.ComparisonSymbol);
        Assert.Equal(PriceType.Low, indicator.ComparisonPriceSource);
    }

    [Fact]
    public void FactoryCreate_WithAParameterObject_ConfiguresTheIndicator()
    {
        var parameter = new CorePairSpreadParameter { ComparisonSymbol = "qqq" };

        var indicator = Assert.IsType<CorePairSpreadIndicator>(IndicatorFactory.Default.Create(IndicatorType.PairSpread, parameter));

        Assert.Equal("QQQ", indicator.ComparisonSymbol);
    }

    [Fact]
    public void ParameterDefaults_AreCloseAndNoSymbol_AndValidateNeverThrows()
    {
        foreach (CorePairParameterBase parameter in new CorePairParameterBase[] { new CorePairRatioParameter(), new CorePairSpreadParameter() })
        {
            Assert.Equal(PriceType.Close, parameter.PriceSource);
            Assert.Equal(PriceType.Close, parameter.ComparisonPriceSource);
            Assert.Equal(string.Empty, parameter.ComparisonSymbol);
            Assert.Equal(0, parameter.GetRequiredWarmupBars());
            parameter.Validate();
            Assert.IsAssignableFrom<ICrossTickerParameter>(parameter);
        }
    }

    [Fact]
    public void V_name_TheIndicatorNameAndTheParameterDisplayNameShareOneLabel()
    {
        Assert.Equal("Pair Ratio", new CorePairRatioIndicator().Name);
        Assert.Equal("Pair Ratio(SPY)", new CorePairRatioIndicator { ComparisonSymbol = "spy" }.Name);
        Assert.Equal("Pair Spread(SPY, A:High, B:Low)",
            new CorePairSpreadIndicator { ComparisonSymbol = "SPY", PriceSource = PriceType.High, ComparisonPriceSource = PriceType.Low }.Name);

        Assert.Equal("Pair Ratio", new CorePairRatioParameter().GetDisplayName("Pair Ratio"));
        Assert.Equal("Pair Ratio(SPY)", new CorePairRatioParameter { ComparisonSymbol = "spy" }.GetDisplayName("Pair Ratio"));
        Assert.Equal("Pair Spread(SPY, A:High, B:Low)",
            new CorePairSpreadParameter { ComparisonSymbol = "SPY", PriceSource = PriceType.High, ComparisonPriceSource = PriceType.Low }.GetDisplayName("Pair Spread"));
    }
}
