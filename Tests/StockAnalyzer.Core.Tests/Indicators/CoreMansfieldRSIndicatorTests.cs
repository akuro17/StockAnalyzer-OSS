#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreMansfieldRSIndicatorTests
{
    private static readonly DateTime Start = new(2024, 1, 1);
    private const string BenchmarkSymbol = "SPY";

    private static CoreCandleData Bar(int dayOffset, decimal close)
        => new(Start.AddDays(dayOffset), close, close, close, close, 1000);

    private static List<CoreCandleData> Bars(params decimal[] closes)
        => closes.Select((close, i) => Bar(i, close)).ToList();

    private static List<CoreCandleData?> SecondaryBars(params decimal[] closes)
        => closes.Select((close, i) => (CoreCandleData?)Bar(i, close)).ToList();

    private static IReadOnlyList<decimal?> Calculate(
        IReadOnlyList<CoreCandleData> primary,
        IReadOnlyList<CoreCandleData?>? secondary,
        int period = IndicatorDefaultConstants.MansfieldRsDefaultPeriod,
        string symbol = BenchmarkSymbol)
    {
        var indicator = new CoreMansfieldRSIndicator
        {
            Period = period,
            ComparisonSymbol = symbol
        };
        indicator.SetSecondaryCandles(secondary);
        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return result.MainValues;
    }

    [Fact]
    public void EnumAndFactory_AppendedAtEnd_AndRegisteredAsTwoTickerSubPanelIndicator()
    {
        Assert.Equal((int)IndicatorType.RogersSatchellVolatility + 1, (int)IndicatorType.MansfieldRS);
        Assert.Equal("Mansfield RS", IndicatorType.MansfieldRS.GetDescription());
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.MansfieldRS));

        ICoreIndicator? indicator = IndicatorFactory.Default.Create(IndicatorType.MansfieldRS);
        Assert.NotNull(indicator);
        var typed = Assert.IsType<CoreMansfieldRSIndicator>(indicator);
        Assert.IsAssignableFrom<ICrossTickerIndicator>(typed);
        Assert.False(typed.IsOverlay);
    }

    [Fact]
    public void SSoTArtifacts_DefaultSettingsEligibilityGroupAndNormalization_AreRegistered()
    {
        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault()
            .Single(s => s.TypeEnum == IndicatorType.MansfieldRS);

        var param = Assert.IsType<CoreMansfieldRSParameter>(settings.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.MansfieldRsDefaultPeriod, param.Period);
        Assert.Equal(CoreIndicatorCategory.Other, settings.Category);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.Equal(IndicatorDefaultConstants.RsiColor, settings.Color);
        SeriesColorConfig series = Assert.Single(settings.SeriesColors);
        Assert.Equal(IndicatorResult.MainSeriesName, series.Name);
        Assert.Equal("Mansfield RS", series.DisplayName);
        Assert.Equal(IndicatorDefaultConstants.RsiColor, series.Color);
        Assert.Equal(new[] { IndicatorResult.MainSeriesName }, series.TargetSeries);

        Assert.Equal(BacktestIndicatorViolationReason.SynchronousCalculationUnsupported, BacktestIndicatorEligibility.Check(IndicatorType.MansfieldRS, null));
        Assert.Contains(IndicatorType.MansfieldRS, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.True(ScreenerIndicatorEligibility.IsAllowed(IndicatorType.MansfieldRS));

        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(IndicatorType.MansfieldRS))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { "Math" }, groups);

        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.MansfieldRS));
    }

    [Fact]
    public void ProportionalSeries_AEqualsBMultipliedByConstant_ReturnsZeroAfterWarmup()
    {
        List<CoreCandleData> a = Bars(200m, 240m, 180m, 300m, 260m);
        List<CoreCandleData?> b = SecondaryBars(100m, 120m, 90m, 150m, 130m);

        IReadOnlyList<decimal?> values = Calculate(a, b, period: 3);

        Assert.Equal(new decimal?[] { null, null, 0m, 0m, 0m }, values);
    }

    [Fact]
    public void HandCalculatedValues_WhenPrimaryOutperformsByTenPercent()
    {
        // ratios: [1.0, 1.0, 1.0, 1.1]
        // at i=3 (Period=4): sma = (1.0 + 1.0 + 1.0 + 1.1) / 4 = 1.025m
        // MansfieldRS[3] = (1.1m / 1.025m - 1m) * 100m
        List<CoreCandleData> a = Bars(100m, 100m, 100m, 110m);
        List<CoreCandleData?> b = SecondaryBars(100m, 100m, 100m, 100m);

        IReadOnlyList<decimal?> values = Calculate(a, b, period: 4);

        decimal expected = (1.1m / 1.025m - 1m) * 100m;
        Assert.Equal(4, values.Count);
        Assert.Null(values[0]);
        Assert.Null(values[1]);
        Assert.Null(values[2]);
        Assert.Equal(expected, values[3]);
    }

    [Fact]
    public void ComparisonHoliday_MissingDateProducesNullAtHolidayAndWhileInSmaWindow()
    {
        List<CoreCandleData> a = Bars(100m, 100m, 100m, 100m, 100m, 110m);
        // Missing dayOffset=2 in comparison candles
        var b = new List<CoreCandleData?>
        {
            Bar(0, 100m),
            Bar(1, 100m),
            Bar(3, 100m),
            Bar(4, 100m),
            Bar(5, 100m)
        };

        IReadOnlyList<decimal?> values = Calculate(a, b, period: 3);

        decimal expectedBar5 = (1.1m / ((1m + 1m + 1.1m) / 3m) - 1m) * 100m;
        Assert.Equal(6, values.Count);
        Assert.Null(values[0]);
        Assert.Null(values[1]);
        Assert.Null(values[2]); // missing comparison date
        Assert.Null(values[3]); // window [1,2,3] contains null at 2
        Assert.Null(values[4]); // window [2,3,4] contains null at 2
        Assert.Equal(expectedBar5, values[5]); // window [3,4,5] has 3 consecutive valid ratios
    }

    [Fact]
    public void PeriodOne_ReturnsZeroOnEveryValidSharedBar()
    {
        List<CoreCandleData> a = Bars(100m, 120m, 95m, 150m);
        List<CoreCandleData?> b = SecondaryBars(50m, 80m, 100m, 75m);

        IReadOnlyList<decimal?> values = Calculate(a, b, period: 1);

        Assert.Equal(new decimal?[] { 0m, 0m, 0m, 0m }, values);
    }

    [Fact]
    public void ZeroComparisonOrZeroBaseline_ReturnsNull()
    {
        List<CoreCandleData> a = Bars(0m, 0m, 100m, 100m);
        List<CoreCandleData?> b = SecondaryBars(100m, 100m, 0m, 100m);

        IReadOnlyList<decimal?> values = Calculate(a, b, period: 2);

        Assert.Null(values[0]); // warmup
        Assert.Null(values[1]); // sma = 0 -> null
        Assert.Null(values[2]); // b = 0 -> ratio is null
        Assert.Null(values[3]); // sma window [2,3] has null
    }

    [Fact]
    public void MissingSymbolOrMissingSecondaryCandles_ReturnsAllNulls()
    {
        List<CoreCandleData> a = Bars(100m, 110m, 120m);
        List<CoreCandleData?> b = SecondaryBars(50m, 55m, 60m);

        Assert.Equal(new decimal?[] { null, null, null }, Calculate(a, b, period: 2, symbol: string.Empty));
        Assert.Equal(new decimal?[] { null, null, null }, Calculate(a, null, period: 2));
        Assert.Equal(new decimal?[] { null, null, null }, Calculate(a, new List<CoreCandleData?>(), period: 2));
    }

    [Fact]
    public void EmptyCandles_ReturnsSuccessfulEmptyResult()
    {
        var indicator = new CoreMansfieldRSIndicator { ComparisonSymbol = BenchmarkSymbol, Period = 2 };
        indicator.SetSecondaryCandles(SecondaryBars(100m));

        IIndicatorResult result = indicator.Calculate(Array.Empty<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void CalculateSeries_ReturnsAllNulls()
    {
        var indicator = new CoreMansfieldRSIndicator { ComparisonSymbol = BenchmarkSymbol, Period = 2 };

        IIndicatorResult result = indicator.CalculateSeries(new decimal?[] { 100m, 105m, 110m });

        Assert.True(result.IsSuccessful);
        Assert.Equal(new decimal?[] { null, null, null }, result.MainValues);
    }

    [Fact]
    public void ParameterValidationWarmupAndDisplayName_FollowContract()
    {
        var param = new CoreMansfieldRSParameter();
        Assert.Equal(IndicatorDefaultConstants.MansfieldRsDefaultPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.MansfieldRsDefaultPeriod - 1, param.GetRequiredWarmupBars());
        Assert.IsAssignableFrom<CorePairParameterBase>(param);
        Assert.IsAssignableFrom<ICrossTickerParameter>(param);
        param.Validate();

        new CoreMansfieldRSParameter { Period = IndicatorDefaultConstants.DefaultPeriodMin }.Validate();
        new CoreMansfieldRSParameter { Period = IndicatorDefaultConstants.DefaultPeriodMax }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMansfieldRSParameter { Period = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMansfieldRSParameter { Period = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());

        var custom = new CoreMansfieldRSParameter
        {
            Period = 200,
            ComparisonSymbol = " spy ",
            PriceSource = PriceType.High,
            ComparisonPriceSource = PriceType.Low
        };
        Assert.Equal("Mansfield RS (200, SPY, A:High, B:Low)", custom.GetDisplayName("Mansfield RS"));

        var indicator = Assert.IsType<CoreMansfieldRSIndicator>(IndicatorFactory.Default.Create(IndicatorType.MansfieldRS, custom));
        Assert.Equal(200, indicator.Period);
        Assert.Equal("SPY", indicator.ComparisonSymbol);
        Assert.Equal(PriceType.High, indicator.PriceSource);
        Assert.Equal(PriceType.Low, indicator.ComparisonPriceSource);
        Assert.Equal("Mansfield RS (200, SPY, A:High, B:Low)", indicator.Name);

        indicator.Configure(new CoreSmaParameter { Period = 10 });
        Assert.Equal(200, indicator.Period);
    }

    [Fact]
    public void PrefixLengthInvariance_ShortTailMatchesFullSeriesExactDecimal()
    {
        var primary = new List<CoreCandleData>();
        var secondary = new List<CoreCandleData?>();
        for (int i = 0; i < 220; i++)
        {
            decimal a = 100m + (i % 13) * 1.37m + i * 0.11m;
            decimal b = 97m + (i % 11) * 1.19m + i * 0.07m;
            primary.Add(Bar(i, a));
            secondary.Add(Bar(i, b));
        }

        IReadOnlyList<decimal?> full = Calculate(primary, secondary, period: 52);
        IReadOnlyList<decimal?> shortTail = Calculate(primary.Skip(100).ToList(), secondary.Skip(100).ToList(), period: 52);

        Assert.Equal(full[^1], shortTail[^1]);
    }
}
