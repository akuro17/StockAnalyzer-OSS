using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// DSL indicator and drawing tool expansion, run I-2: <see cref="CoreTreynorRatioIndicator"/>, <see cref="CoreInformationRatioIndicator"/>, <see cref="CoreTrackingErrorIndicator"/> and
/// <see cref="CoreRollingCovarianceIndicator"/>. Every value is pinned to an independent transcription of the formula on the same window of returns, and the pairing rules
/// (date alignment, all pairs valid, no comparison symbol) and the registration contract are tested.
/// </summary>
public class DslCrossTickerIndicatorsTests
{
    private const string Benchmark = "SPY";

    private static List<CoreCandleData> Candles(int count, int seed, double drift = 0.0)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + drift + (0.02 * (rng.NextDouble() - 0.5)));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData?> AsSecondary(IEnumerable<CoreCandleData> candles) => candles.Select(c => (CoreCandleData?)c).ToList();

    private static T With<T>(T indicator, IEnumerable<CoreCandleData>? benchmark) where T : CoreCrossTickerReturnStatisticIndicatorBase
    {
        indicator.ComparisonSymbol = Benchmark;
        if (benchmark != null)
        {
            indicator.SetSecondaryCandles(AsSecondary(benchmark));
        }

        return indicator;
    }

    private static double[] Returns(IReadOnlyList<CoreCandleData> candles, bool log)
    {
        var r = new double[candles.Count];
        for (int i = 1; i < candles.Count; i++)
        {
            double previous = (double)candles[i - 1].Close;
            double current = (double)candles[i].Close;
            r[i] = log ? Math.Log(current / previous) : (current - previous) / previous;
        }

        return r;
    }

    private static (double[] A, double[] B) Window(double[] a, double[] b, int last, int period) =>
        (a.Skip(last - period + 1).Take(period).ToArray(), b.Skip(last - period + 1).Take(period).ToArray());

    private static double Cov(double[] x, double[] y, bool sample)
    {
        double mx = x.Average();
        double my = y.Average();
        return x.Zip(y, (p, q) => (p - mx) * (q - my)).Sum() / (sample ? x.Length - 1 : x.Length);
    }

    private static double Sd(double[] x) => Math.Sqrt(Cov(x, x, sample: true));

    // ---------------------------------------------------------------- values against independent transcriptions

    [Fact]
    public void Treynor_EqualsTheIndependentFormulaOnEveryWindow_ForSimpleAndLogReturns()
    {
        var a = Candles(120, 3);
        var b = Candles(120, 8);
        const int period = 30;

        foreach (bool log in new[] { false, true })
        {
            var result = With(new CoreTreynorRatioIndicator { Period = period, CalculationMode = log ? BetaCalculationMode.LogReturn : BetaCalculationMode.SimpleReturn }, b).Calculate(a);
            double[] ra = Returns(a, log);
            double[] rb = Returns(b, log);
            for (int i = 0; i < a.Count; i++)
            {
                if (i < period)
                {
                    Assert.Null(result.MainValues[i]);
                    continue;
                }

                var (wa, wb) = Window(ra, rb, i, period);
                double beta = Cov(wa, wb, true) / Cov(wb, wb, true);
                Assert.Equal(wa.Average() / beta, (double)result.MainValues[i]!.Value, 9);
            }
        }
    }

    [Fact]
    public void Treynor_Annualised_IsLinearInPeriodsPerYear_AndTheRiskFreeRateIsPerBar()
    {
        var a = Candles(90, 4);
        var b = Candles(90, 9);
        const int period = 40;
        const int ppy = 52;
        int last = a.Count - 1;
        double[] ra = Returns(a, false);
        double[] rb = Returns(b, false);
        var (wa, wb) = Window(ra, rb, last, period);
        double beta = Cov(wa, wb, true) / Cov(wb, wb, true);

        var annual = With(new CoreTreynorRatioIndicator { Period = period, Annualize = true, PeriodsPerYear = ppy, AnnualRiskFreeRate = 0.052m }, b).Calculate(a);
        var perBarWithRate = With(new CoreTreynorRatioIndicator { Period = period, PeriodsPerYear = ppy, AnnualRiskFreeRate = 0.052m }, b).Calculate(a);

        Assert.Equal((wa.Average() - 0.001) * ppy / beta, (double)annual.MainValues[last]!.Value, 9);
        Assert.Equal((wa.Average() - 0.001) / beta, (double)perBarWithRate.MainValues[last]!.Value, 9);
    }

    [Fact]
    public void InformationRatio_AndTrackingError_EqualTheIndependentFormulasOnTheActiveReturn()
    {
        var a = Candles(110, 5, 0.0004);
        var b = Candles(110, 12);
        const int period = 25;
        const int ppy = 252;
        double[] ra = Returns(a, false);
        double[] rb = Returns(b, false);

        var ir = With(new CoreInformationRatioIndicator { Period = period }, b).Calculate(a);
        var irAnnual = With(new CoreInformationRatioIndicator { Period = period, Annualize = true }, b).Calculate(a);
        var te = With(new CoreTrackingErrorIndicator { Period = period, AsPercent = false }, b).Calculate(a);
        var tePercent = With(new CoreTrackingErrorIndicator { Period = period }, b).Calculate(a);
        var teAnnual = With(new CoreTrackingErrorIndicator { Period = period, AsPercent = false, Annualize = true }, b).Calculate(a);

        for (int i = period; i < a.Count; i++)
        {
            var (wa, wb) = Window(ra, rb, i, period);
            double[] active = wa.Zip(wb, (p, q) => p - q).ToArray();
            double informationRatio = active.Average() / Sd(active);
            Assert.Equal(informationRatio, (double)ir.MainValues[i]!.Value, 9);
            Assert.Equal(informationRatio * Math.Sqrt(ppy), (double)irAnnual.MainValues[i]!.Value, 9);
            Assert.Equal(Sd(active), (double)te.MainValues[i]!.Value, 9);
            Assert.Equal(Sd(active) * 100.0, (double)tePercent.MainValues[i]!.Value, 7);
            Assert.Equal(Sd(active) * Math.Sqrt(ppy), (double)teAnnual.MainValues[i]!.Value, 9);
        }

        Assert.Null(ir.MainValues[period - 1]);
    }

    [Fact]
    public void Covariance_EqualsTheIndependentFormula_ForSampleAndPopulation()
    {
        var a = Candles(100, 6);
        var b = Candles(100, 14);
        const int period = 20;
        double[] ra = Returns(a, false);
        double[] rb = Returns(b, false);

        var sample = With(new CoreRollingCovarianceIndicator { Period = period }, b).Calculate(a);
        var population = With(new CoreRollingCovarianceIndicator { Period = period, UseSampleCovariance = false }, b).Calculate(a);

        for (int i = period; i < a.Count; i++)
        {
            var (wa, wb) = Window(ra, rb, i, period);
            Assert.Equal(Cov(wa, wb, true), (double)sample.MainValues[i]!.Value, 12);
            Assert.Equal(Cov(wa, wb, false), (double)population.MainValues[i]!.Value, 12);
        }

        Assert.Null(sample.MainValues[period - 1]);
    }

    // ---------------------------------------------------------------- degenerate windows

    [Fact]
    public void ABenchmarkEqualToTheChartSymbol_GivesBetaOneTrackingErrorZeroAndNoInformationRatio()
    {
        var a = Candles(60, 7);
        const int period = 20;
        double[] ra = Returns(a, false);
        int last = a.Count - 1;
        double mean = ra.Skip(last - period + 1).Average();

        var treynor = With(new CoreTreynorRatioIndicator { Period = period }, a).Calculate(a);
        var te = With(new CoreTrackingErrorIndicator { Period = period }, a).Calculate(a);
        var ir = With(new CoreInformationRatioIndicator { Period = period }, a).Calculate(a);

        Assert.Equal(mean, (double)treynor.MainValues[last]!.Value, 9);          // beta = 1
        Assert.Equal(0m, te.MainValues[last]);                                   // a constant (zero) active return: a value, not null
        Assert.Null(ir.MainValues[last]);                                        // no tracking error: the ratio is undefined
    }

    [Fact]
    public void ABenchmarkWithoutVariance_GivesNoTreynorValue()
    {
        var a = Candles(60, 7);
        var flat = Enumerable.Range(0, 60).Select(i => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), 50m, 50m, 50m, 50m, 1000)).ToList();

        var treynor = With(new CoreTreynorRatioIndicator { Period = 20 }, flat).Calculate(a);

        Assert.All(treynor.MainValues, v => Assert.Null(v));
    }

    // ---------------------------------------------------------------- pairing rules

    [Fact]
    public void WithoutAComparisonSymbolOrItsCandles_EveryBarHasNoValue()
    {
        var a = Candles(60, 1);
        var b = Candles(60, 2);

        Assert.All(new CoreTreynorRatioIndicator { Period = 20 }.Calculate(a).MainValues, v => Assert.Null(v));
        var noCandles = With(new CoreRollingCovarianceIndicator { Period = 20 }, null).Calculate(a);
        Assert.Equal(a.Count, noCandles.MainValues.Count);
        Assert.All(noCandles.MainValues, v => Assert.Null(v));
        var noSymbol = new CoreInformationRatioIndicator { Period = 20 };
        noSymbol.SetSecondaryCandles(AsSecondary(b));
        Assert.All(noSymbol.Calculate(a).MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void ADateMissingFromTheBenchmark_InvalidatesTheWindowsHoldingItsReturnAndTheNext()
    {
        var a = Candles(80, 1);
        var b = Candles(80, 2);
        const int period = 10;
        b.RemoveAt(40); // the benchmark has no bar on the date of chart bar 40

        var result = With(new CoreRollingCovarianceIndicator { Period = period }, b).Calculate(a);

        // The returns of chart bars 40 and 41 are invalid, so the windows ending at bars 40 .. 50 contain one of them.
        for (int i = 40; i <= 50; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        Assert.NotNull(result.MainValues[39]);
        Assert.NotNull(result.MainValues[51]);
    }

    [Fact]
    public void ABenchmarkOnOtherDatesIsAlignedByCalendarDate()
    {
        var a = Candles(60, 1);
        var b = Candles(60, 2);
        var shifted = b.Select(c => new CoreCandleData(c.Timestamp.AddDays(5), c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

        var aligned = With(new CoreRollingCovarianceIndicator { Period = 15 }, b).Calculate(a);
        var offset = With(new CoreRollingCovarianceIndicator { Period = 15 }, shifted).Calculate(a);

        // The shifted benchmark has no bars on the first 5 chart dates: those windows are empty, the later ones hold the benchmark of five bars earlier.
        Assert.NotNull(aligned.MainValues[30]);
        Assert.Null(offset.MainValues[10]);
        Assert.NotEqual(aligned.MainValues[40], offset.MainValues[40]);
    }

    // ---------------------------------------------------------------- parameters

    [Fact]
    public void ParametersAreValidated_AndDefaultsDoNotAnnualize()
    {
        Assert.False(new CoreTreynorRatioParameter().Annualize);
        Assert.False(new CoreInformationRatioParameter().Annualize);
        Assert.False(new CoreTrackingErrorParameter().Annualize);
        Assert.True(new CoreTrackingErrorParameter().AsPercent);
        Assert.True(new CoreRollingCovarianceParameter().UseSampleCovariance);
        Assert.Equal(BetaCalculationMode.SimpleReturn, new CoreTreynorRatioParameter().CalculationMode);
        Assert.Equal(60, new CoreTreynorRatioParameter().Period);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTreynorRatioParameter { Period = 2 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreInformationRatioParameter { Period = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTrackingErrorParameter { Period = 2 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingCovarianceParameter { Period = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTreynorRatioParameter { PeriodsPerYear = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTreynorRatioParameter { AnnualRiskFreeRate = 1.5m }.Validate());
        new CoreRollingCovarianceParameter { Period = 2 }.Validate();
        new CoreTreynorRatioParameter { Period = 3 }.Validate();
        new CoreTrackingErrorParameter { Period = 500, PeriodsPerYear = 366 }.Validate();

        Assert.False(With(new CoreTreynorRatioIndicator { Period = 2 }, Candles(30, 2)).Calculate(Candles(30, 1)).IsSuccessful);
        Assert.Equal("SPY", new CoreInformationRatioParameter { ComparisonSymbol = " spy " }.ComparisonSymbol);
    }

    [Fact]
    public void ConfigureCopiesEverySetting()
    {
        var indicator = new CoreTrackingErrorIndicator();
        indicator.Configure(new CoreTrackingErrorParameter { Period = 33, ComparisonSymbol = "qqq", ComparisonPriceSource = PriceType.Open, CalculationMode = BetaCalculationMode.LogReturn, Annualize = true, PeriodsPerYear = 52, AsPercent = false });

        Assert.Equal(33, indicator.Period);
        Assert.Equal("QQQ", indicator.ComparisonSymbol);
        Assert.Equal(PriceType.Open, indicator.ComparisonPriceSource);
        Assert.Equal(BetaCalculationMode.LogReturn, indicator.CalculationMode);
        Assert.True(indicator.Annualize);
        Assert.Equal(52, indicator.PeriodsPerYear);
        Assert.False(indicator.AsPercent);
        Assert.Contains("QQQ", indicator.Name);
        Assert.Contains("Log", indicator.Name);
    }

    // ---------------------------------------------------------------- contract shared by the four types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.TreynorRatio },
        new object[] { IndicatorType.InformationRatio },
        new object[] { IndicatorType.TrackingError },
        new object[] { IndicatorType.RollingCovariance },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_AsATwoSymbolSubPaneIndicator_DisabledByDefault(IndicatorType type)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.IsAssignableFrom<ICrossTickerIndicator>(IndicatorFactory.Default.Create(type));
        Assert.False(((CoreIndicatorBase)IndicatorFactory.Default.Create(type)!).IsOverlay);

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.True(settings.IncludeZeroBaseline);
        Assert.IsAssignableFrom<ICrossTickerParameter>(settings.ParameterObject);
        settings.ParameterObject!.Validate();
        Assert.True(settings.ParameterObject!.GetRequiredWarmupBars() > 0);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);

        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { "Math" }, groups);
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheScreenerSeesATwoSymbolType_AndTheBacktestRefusesItWithoutAComparisonSymbol(IndicatorType type)
    {
        Assert.True(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.False(ScreenerIndicatorEligibility.IsUniverseBreadthType(type));
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.Contains(type, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.Equal(BacktestIndicatorViolationReason.SynchronousCalculationUnsupported, BacktestIndicatorEligibility.Check(type, null));
        Assert.False(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type)); // a two-symbol type is always calculated on the chart bars (IndicatorTimeframeSupport.NotApplicable)
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EveryDefaultSeriesIsEmitted_AndMainIsMappedExplicitly(IndicatorType type)
    {
        var factory = IndicatorFactory.Default;
        var indicator = (CoreCrossTickerReturnStatisticIndicatorBase)factory.Create(type)!;
        indicator.ComparisonSymbol = Benchmark;
        indicator.SetSecondaryCandles(AsSecondary(Candles(200, 2)));
        var result = indicator.Calculate(Candles(200, 1));
        Assert.True(result.IsSuccessful);
        Assert.Contains(result.MainValues, v => v.HasValue);

        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: factory);
            Assert.True(result.HasSeries(name), $"{type}: '{name}' is not emitted");
        }

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == IndicatorResult.MainSeriesName && sc.TargetSeries.Contains(IndicatorResult.MainSeriesName));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void FutureBarsOfEitherSymbolDoNotChangeThePast(IndicatorType type)
    {
        var a = Candles(220, 1);
        var b = Candles(220, 2);
        var poisonedA = a.ToList();
        var poisonedB = b.ToList();
        for (int i = 150; i < a.Count; i++)
        {
            decimal ca = a[i].Close * 7m + 13m;
            decimal cb = b[i].Close * 3m + 5m;
            poisonedA[i] = new CoreCandleData(a[i].Timestamp, ca, ca, ca, ca, 1);
            poisonedB[i] = new CoreCandleData(b[i].Timestamp, cb, cb, cb, cb, 1);
        }

        IReadOnlyList<decimal?> Run(List<CoreCandleData> chart, List<CoreCandleData> benchmark)
        {
            var indicator = (CoreCrossTickerReturnStatisticIndicatorBase)IndicatorFactory.Default.Create(type)!;
            indicator.ComparisonSymbol = Benchmark;
            indicator.SetSecondaryCandles(AsSecondary(benchmark));
            return indicator.Calculate(chart).MainValues;
        }

        var before = Run(a, b);
        var after = Run(poisonedA, poisonedB);
        Assert.Contains(before.Take(150), v => v.HasValue);
        for (int i = 0; i < 150; i++)
        {
            Assert.Equal(before[i], after[i]);
        }
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EmptyAndTinyInputsGiveNoValueAndNeverThrow(IndicatorType type)
    {
        var indicator = (CoreCrossTickerReturnStatisticIndicatorBase)IndicatorFactory.Default.Create(type)!;
        indicator.ComparisonSymbol = Benchmark;
        indicator.SetSecondaryCandles(AsSecondary(Candles(5, 2)));
        Assert.Empty(indicator.Calculate(new List<CoreCandleData>()).MainValues);
        Assert.All(indicator.Calculate(Candles(1, 1)).MainValues, v => Assert.Null(v));
        Assert.All(indicator.Calculate(Candles(5, 1)).MainValues, v => Assert.Null(v));
    }
}
