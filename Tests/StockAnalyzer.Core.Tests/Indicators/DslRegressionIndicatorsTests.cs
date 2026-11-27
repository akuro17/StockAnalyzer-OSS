using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>DSL indicator and drawing tool expansion, run I-4: <see cref="CorePolynomialRegressionIndicator"/> and <see cref="CoreNonlinearRegressionIndicator"/>.</summary>
public class DslRegressionIndicatorsTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 5)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i % 11 == 0 ? 0.03 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i)).ToList();

    private static List<CoreCandleData> FromCurve(int count, Func<double, double> curve) =>
        FromCloses(Enumerable.Range(1, count).Select(x => Math.Round((decimal)curve(x), 6)));

    // ---------------------------------------------------------------- PolynomialRegression

    [Fact]
    public void Polynomial_OnALine_GivesTheFittedValueAndTheExactExtrapolation()
    {
        var candles = FromCloses(Enumerable.Range(0, 30).Select(i => 100m + (2m * i)));

        var fitted = new CorePolynomialRegressionIndicator { Period = 10, Degree = 1 }.Calculate(candles);
        var ahead = new CorePolynomialRegressionIndicator { Period = 10, Degree = 1, ForecastHorizon = 3 }.Calculate(candles);

        Assert.Equal(158.0, (double)fitted.MainValues[29]!.Value, 9);   // the newest close
        Assert.Equal(164.0, (double)ahead.MainValues[29]!.Value, 9);    // three bars ahead of the line, drawn at the newest bar
        Assert.Null(fitted.MainValues[8]);
        Assert.NotNull(fitted.MainValues[9]);
    }

    [Fact]
    public void Polynomial_OnAParabola_IsExactForDegreeTwoAndEqualsTheKernelOnEveryWindow()
    {
        var candles = FromCloses(Enumerable.Range(0, 40).Select(i => 50m + (decimal)(0.1 * i * i)));
        var result = new CorePolynomialRegressionIndicator { Period = 15, Degree = 2, ForecastHorizon = 2 }.Calculate(candles);
        double[] closes = candles.Select(c => (double)c.Close).ToArray();

        for (int i = 14; i < candles.Count; i++)
        {
            var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
            MathResult<double> kernel = DslRegressionMath.PolyProject(closes.AsSpan(i - 14, 15), 2, 2, ref budget);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(kernel.Value), result.MainValues[i]);
            Assert.Equal(50.0 + (0.1 * (i + 2) * (i + 2)), (double)result.MainValues[i]!.Value, 6);
        }
    }

    [Fact]
    public void Polynomial_APeriodBelowDegreePlusTwoGivesNoValue_WithoutAnException()
    {
        var result = new CorePolynomialRegressionIndicator { Period = 5, Degree = 4 }.Calculate(Candles(40));

        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, v => Assert.Null(v));
        new CorePolynomialRegressionParameter { Period = 5, Degree = 4 }.Validate(); // no cross-field check
    }

    [Fact]
    public void Polynomial_ParametersAreValidated_AndAGapGivesNoValueForItsWindows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePolynomialRegressionParameter { Period = 3 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePolynomialRegressionParameter { Period = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePolynomialRegressionParameter { Degree = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePolynomialRegressionParameter { Degree = 7 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePolynomialRegressionParameter { ForecastHorizon = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePolynomialRegressionParameter { ForecastHorizon = 11 }.Validate());
        new CorePolynomialRegressionParameter { Period = 500, Degree = 6, ForecastHorizon = 10 }.Validate();
        Assert.False(new CorePolynomialRegressionIndicator { Degree = 9 }.Calculate(Candles(40)).IsSuccessful);

        var defaults = new CorePolynomialRegressionParameter();
        Assert.Equal((30, 2, 0), (defaults.Period, defaults.Degree, defaults.ForecastHorizon));
    }

    [Fact]
    public void Polynomial_WorksOnALongSeriesWithoutExhaustingTheBudget()
    {
        // A budget shared by all bars (10,000 operations) would be spent after a few bars of a 6-degree fit over 200 prices.
        var result = new CorePolynomialRegressionIndicator { Period = 200, Degree = 6 }.Calculate(Candles(900));

        Assert.NotNull(result.MainValues[^1]);
        Assert.Equal(900 - 200 + 1, result.MainValues.Count(v => v.HasValue));
    }

    // ---------------------------------------------------------------- NonlinearRegression

    private static CoreNonlinearRegressionIndicator Nonlinear(NonlinearRegressionModel model, int period) => new() { Period = period, ModelType = model };

    [Fact]
    public void Nonlinear_Exponential_RecoversAnExactExponentialCurve()
    {
        var candles = FromCurve(30, x => 50.0 * Math.Exp(0.02 * x));
        var result = Nonlinear(NonlinearRegressionModel.Exponential, 30).Calculate(candles);

        Assert.Equal((double)candles[29].Close, (double)result.MainValues[29]!.Value, 3);
        Assert.Equal(0.02, (double)result.GetSeries(CoreNonlinearRegressionIndicator.ParameterBSeriesName)[29]!.Value, 6);
        Assert.Equal(1.0, (double)result.GetSeries(CoreNonlinearRegressionIndicator.RSquaredSeriesName)[29]!.Value, 6);
        Assert.All(result.MainValues.Take(29), v => Assert.Null(v));
    }

    [Fact]
    public void Nonlinear_Logarithmic_AndPower_RecoverTheirCurves()
    {
        var log = FromCurve(30, x => 10.0 + (5.0 * Math.Log(x)));
        var logResult = Nonlinear(NonlinearRegressionModel.Logarithmic, 30).Calculate(log);
        Assert.Equal((double)log[29].Close, (double)logResult.MainValues[29]!.Value, 4);
        Assert.Equal(5.0, (double)logResult.GetSeries(CoreNonlinearRegressionIndicator.ParameterBSeriesName)[29]!.Value, 5);

        var power = FromCurve(30, x => 3.0 * Math.Pow(x, 1.5));
        var powerResult = Nonlinear(NonlinearRegressionModel.Power, 30).Calculate(power);
        Assert.Equal((double)power[29].Close, (double)powerResult.MainValues[29]!.Value, 3);
        Assert.Equal(1.5, (double)powerResult.GetSeries(CoreNonlinearRegressionIndicator.ParameterBSeriesName)[29]!.Value, 6);
        Assert.Equal(1.0, (double)powerResult.GetSeries(CoreNonlinearRegressionIndicator.RSquaredSeriesName)[29]!.Value, 6);
    }

    [Fact]
    public void Nonlinear_Logistic_FitsAnSShapedWindow_AndTheKernelCoefficientsAreInOriginalXUnits()
    {
        var candles = FromCurve(30, x => 100.0 + (20.0 / (1.0 + Math.Exp(-0.3 * (x - 15.0)))));
        var result = Nonlinear(NonlinearRegressionModel.Logistic, 30).Calculate(candles);

        Assert.NotNull(result.MainValues[29]);
        Assert.Equal((double)candles[29].Close, (double)result.MainValues[29]!.Value, 0);
        Assert.True((double)result.GetSeries(CoreNonlinearRegressionIndicator.RSquaredSeriesName)[29]!.Value > 0.99);
        Assert.True((double)result.GetSeries(CoreNonlinearRegressionIndicator.ParameterBSeriesName)[29]!.Value > 0.2);

        // The unit proof: the log-likelihood the kernel reports equals the one of the reported (b0, b1) evaluated at the ORIGINAL x = 1 .. n.
        double[] y = candles.Select(c => (double)c.Close).ToArray();
        double min = y.Min();
        double range = y.Max() - min;
        const double eps = IndicatorDefaultConstants.NonlinearLogisticEpsilon;
        double[] mapped = y.Select(v => eps + ((1.0 - (2.0 * eps)) * ((v - min) / range))).ToArray();
        double[] x = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
        var kernelResult = new double[3];
        var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        Assert.Equal(MathErrorCode.None, DslRegressionMath.RegLogisticFit(x, mapped, DslTolerance.DefaultTol, kernelResult, ref budget));

        double logLikelihood = 0.0;
        for (int j = 0; j < 30; j++)
        {
            double p = 1.0 / (1.0 + Math.Exp(-(kernelResult[0] + (kernelResult[1] * x[j]))));
            logLikelihood += (mapped[j] * Math.Log(p)) + ((1.0 - mapped[j]) * Math.Log(1.0 - p));
        }

        Assert.Equal(logLikelihood, kernelResult[2], 6);
    }

    [Fact]
    public void Nonlinear_RSquaredIsNotClipped_AndEqualsTheIndependentPriceScaleFormula()
    {
        // A symmetric V-shaped window: the log-linear fit has slope 0 and sits at the geometric mean, below the arithmetic mean, so SSE > SST and R-squared is negative.
        var candles = FromCurve(30, x => 10.0 + (Math.Pow(x - 15.5, 2) / 4.0));
        var result = Nonlinear(NonlinearRegressionModel.Exponential, 30).Calculate(candles);

        double[] y = candles.Select(c => (double)c.Close).ToArray();
        double meanLog = y.Average(Math.Log);
        double constant = Math.Exp(meanLog);
        double mean = y.Average();
        double sse = y.Sum(v => (v - constant) * (v - constant));
        double sst = y.Sum(v => (v - mean) * (v - mean));
        double expected = 1.0 - (sse / sst);

        Assert.True(expected < 0.0);
        decimal? actual = result.GetSeries(CoreNonlinearRegressionIndicator.RSquaredSeriesName)[29];
        Assert.NotNull(actual);
        Assert.Equal(expected, (double)actual!.Value, 6);
        Assert.Equal(constant, (double)result.MainValues[29]!.Value, 6);
    }

    [Fact]
    public void Nonlinear_ARejectedWindowGivesNoValue()
    {
        // Exponential and Power need every price above zero; a window of equal prices has no variance for the logistic mapping.
        var withZero = FromCloses(Enumerable.Range(1, 12).Select(i => i == 6 ? 0m : 10m + i));
        Assert.All(Nonlinear(NonlinearRegressionModel.Exponential, 12).Calculate(withZero).MainValues, v => Assert.Null(v));
        Assert.All(Nonlinear(NonlinearRegressionModel.Power, 12).Calculate(withZero).MainValues, v => Assert.Null(v));
        Assert.NotNull(Nonlinear(NonlinearRegressionModel.Logarithmic, 12).Calculate(withZero).MainValues[11]);

        var flat = FromCloses(Enumerable.Repeat(50m, 20));
        Assert.All(Nonlinear(NonlinearRegressionModel.Logistic, 12).Calculate(flat).MainValues, v => Assert.Null(v));
        Assert.All(Nonlinear(NonlinearRegressionModel.Exponential, 12).Calculate(flat).GetSeries(CoreNonlinearRegressionIndicator.RSquaredSeriesName), v => Assert.Null(v));
    }

    [Fact]
    public void Nonlinear_ParametersAreValidated_AndEveryModelWorksOnALongSeries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreNonlinearRegressionParameter { Period = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreNonlinearRegressionParameter { Period = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreNonlinearRegressionParameter { ModelType = (NonlinearRegressionModel)9 }.Validate());
        new CoreNonlinearRegressionParameter { Period = 5 }.Validate();
        new CoreNonlinearRegressionParameter { Period = 500 }.Validate();
        Assert.Equal((30, NonlinearRegressionModel.Exponential), (new CoreNonlinearRegressionParameter().Period, new CoreNonlinearRegressionParameter().ModelType));
        Assert.False(new CoreNonlinearRegressionIndicator { Period = 2 }.Calculate(Candles(30)).IsSuccessful);

        var candles = Candles(800);
        foreach (NonlinearRegressionModel model in Enum.GetValues<NonlinearRegressionModel>())
        {
            var result = Nonlinear(model, 60).Calculate(candles);
            Assert.True(result.IsSuccessful);
            Assert.NotNull(result.MainValues[^1]);
            Assert.True(result.MainValues.Count(v => v.HasValue) > 700, model.ToString());
        }
    }

    // ---------------------------------------------------------------- contract shared by the two types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.PolynomialRegression },
        new object[] { IndicatorType.NonlinearRegression },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_AsAPriceOverlay_DisabledByDefault(IndicatorType type)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.True(((CoreIndicatorBase)IndicatorFactory.Default.Create(type)!).IsOverlay);

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Trend, settings.Category);
        settings.ParameterObject!.Validate();
        Assert.True(settings.ParameterObject!.GetRequiredWarmupBars() > 0);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);

        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { "Math" }, groups);
        Assert.Equal(ChannelNormalization.WindowMinMax, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EveryDefaultSeriesIsEmitted_AndSuppressedSeriesHaveNoColourEntry(IndicatorType type)
    {
        var factory = IndicatorFactory.Default;
        var result = factory.Create(type)!.Calculate(Candles(150));
        Assert.True(result.IsSuccessful);
        Assert.Contains(result.MainValues, v => v.HasValue);

        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: factory);
            Assert.True(result.HasSeries(name), $"{type}: '{name}' is not emitted");
        }

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == IndicatorResult.MainSeriesName && sc.TargetSeries.Contains(IndicatorResult.MainSeriesName));
        foreach (SeriesColorConfig colour in settings.SeriesColors)
        {
            foreach (string target in colour.TargetSeries)
            {
                Assert.False(IndicatorChartSuppression.IsSuppressed(type, target), $"{type}: the suppressed series '{target}' must not have a colour entry");
            }
        }
    }

    [Fact]
    public void TheNonlinearCompanionSeries_AreChartSuppressedAndTheMainSeriesIsNot()
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.NonlinearRegression, "ParameterB"));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.NonlinearRegression, "RSquared"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.NonlinearRegression, IndicatorResult.MainSeriesName));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.PolynomialRegression, IndicatorResult.MainSeriesName));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndNeverReadsTheFuture(IndicatorType type)
    {
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));

        var original = Candles(220);
        var poisoned = original.ToList();
        for (int i = 150; i < poisoned.Count; i++)
        {
            decimal c = original[i].Close * 7m + 13m;
            poisoned[i] = new CoreCandleData(original[i].Timestamp, c, c, c, c, 1);
        }

        var factory = IndicatorFactory.Default;
        var before = factory.Create(type)!.Calculate(original).MainValues;
        var after = factory.Create(type)!.Calculate(poisoned).MainValues;
        for (int i = 0; i < 150; i++)
        {
            Assert.Equal(before[i], after[i]);
        }

        Assert.Empty(factory.Create(type)!.Calculate(new List<CoreCandleData>()).MainValues);
        Assert.All(factory.Create(type)!.Calculate(Candles(1)).MainValues, v => Assert.Null(v));
    }
}
