using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// Statistical functions plan, Run R7: <see cref="CoreQuantileBandsIndicator"/>, <see cref="CoreTheilSenRegressionIndicator"/> and <see cref="CoreCornishFisherVarIndicator"/>.
/// Values are pinned to hand-solved windows and to the kernels on the same window, together with the registration contract of plan section 4.1
/// (overlay flags, warm-up, null propagation, causality, chart suppression, classification, parameter ranges).
/// </summary>
public class StatisticalRobustIndicatorsTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 11)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i % 17 == 0 ? 0.03 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i)).ToList();

    // ---------------------------------------------------------------- QuantileBands

    [Fact]
    public void QuantileBands_OnTheNumbersOneToTen_GiveTheHandSolvedQuantiles()
    {
        // Window 5 of 1..10: the last window is 6..10 -> median 8, Q(0.75) = 9, Q(0.25) = 7, IQR = 2, |x - 8| = 2 1 0 1 2 -> MAD 1.
        var result = new CoreQuantileBandsIndicator { Period = 5 }.Calculate(FromCloses(Enumerable.Range(1, 10).Select(i => (decimal)i)));

        Assert.True(result.IsSuccessful);
        Assert.Equal(8m, result.MainValues[9]);
        Assert.Equal(9m, result.GetSeries(CoreQuantileBandsIndicator.UpperBandSeriesName)[9]);
        Assert.Equal(7m, result.GetSeries(CoreQuantileBandsIndicator.LowerBandSeriesName)[9]);
        Assert.Equal(2m, result.GetSeries(CoreQuantileBandsIndicator.IqrSeriesName)[9]);
        Assert.Equal(1m, result.GetSeries(CoreQuantileBandsIndicator.MadSeriesName)[9]);
        Assert.Equal(3m, result.MainValues[4]);
        Assert.All(result.MainValues.Take(4), v => Assert.Null(v));
    }

    [Fact]
    public void QuantileBands_EqualTheKernelOnEveryWindow_AndAreAsymmetricForASkewedWindow()
    {
        var candles = Candles(120);
        const int period = 25;
        var result = new CoreQuantileBandsIndicator { Period = period, UpperQuantile = 0.9, LowerQuantile = 0.2 }.Calculate(candles);

        var upper = result.GetSeries(CoreQuantileBandsIndicator.UpperBandSeriesName);
        var lower = result.GetSeries(CoreQuantileBandsIndicator.LowerBandSeriesName);
        var iqr = result.GetSeries(CoreQuantileBandsIndicator.IqrSeriesName);
        var mad = result.GetSeries(CoreQuantileBandsIndicator.MadSeriesName);
        bool anyAsymmetric = false;
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(upper[i]);
                Assert.Null(lower[i]);
                Assert.Null(iqr[i]);
                Assert.Null(mad[i]);
                continue;
            }

            decimal[] window = candles.Skip(i - period + 1).Take(period).Select(c => c.Close).ToArray();
            decimal[] scratch = new decimal[period];
            Assert.Equal(OrderStatisticsMath.Median<decimal>(window.ToArray()), result.MainValues[i]);
            Assert.Equal(OrderStatisticsMath.QuantileType7<decimal>(window.ToArray(), 0.9m), upper[i]);
            Assert.Equal(OrderStatisticsMath.QuantileType7<decimal>(window.ToArray(), 0.2m), lower[i]);
            Assert.Equal(OrderStatisticsMath.InterquartileRange<decimal>(window.ToArray()), iqr[i]);
            Assert.Equal(OrderStatisticsMath.MedianAbsoluteDeviation<decimal>(window.ToArray(), scratch), mad[i]);
            Assert.True(lower[i] <= result.MainValues[i] && result.MainValues[i] <= upper[i]);
            anyAsymmetric |= (upper[i] - result.MainValues[i]) != (result.MainValues[i] - lower[i]);
        }

        Assert.True(anyAsymmetric);
    }

    [Fact]
    public void QuantileBands_Defaults_AndNoDoubleConversionOfThePrice()
    {
        var indicator = new CoreQuantileBandsIndicator();
        Assert.Equal(20, indicator.Period);
        Assert.Equal(0.75, indicator.UpperQuantile);
        Assert.Equal(0.25, indicator.LowerQuantile);

        // A 28-digit price survives the median untouched (decimal arithmetic end to end).
        decimal precise = 123.4567890123456789012345678m;
        var result = new CoreQuantileBandsIndicator { Period = 5 }.Calculate(FromCloses(Enumerable.Repeat(precise, 8)));
        Assert.Equal(precise, result.MainValues[7]);
        Assert.Equal(0m, result.GetSeries(CoreQuantileBandsIndicator.IqrSeriesName)[7]);
        Assert.Equal(0m, result.GetSeries(CoreQuantileBandsIndicator.MadSeriesName)[7]);
    }

    [Fact]
    public void QuantileBands_ParameterRanges_AreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreQuantileBandsParameter { Period = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreQuantileBandsParameter { Period = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreQuantileBandsParameter { UpperQuantile = 0.5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreQuantileBandsParameter { UpperQuantile = 1.0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreQuantileBandsParameter { LowerQuantile = 0.5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreQuantileBandsParameter { LowerQuantile = 0.0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreQuantileBandsParameter { LowerQuantile = double.NaN }.Validate());
        new CoreQuantileBandsParameter { Period = 500, UpperQuantile = 0.99, LowerQuantile = 0.01 }.Validate();

        Assert.False(new CoreQuantileBandsIndicator { UpperQuantile = 0.4 }.Calculate(Candles(60)).IsSuccessful);
    }

    // ---------------------------------------------------------------- TheilSenRegression

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheilSen_OnAnExactLine_GivesTheLineItsSlopeAndZeroWidthBands(bool siegel)
    {
        // y = 100 + 0.5 i: the line, the slope 0.5 and (window-relative) intercept = the price of the oldest bar of the window.
        var closes = Enumerable.Range(0, 40).Select(i => 100m + (0.5m * i)).ToList();
        var result = new CoreTheilSenRegressionIndicator { Period = 12, UseSiegelMedian = siegel }.Calculate(FromCloses(closes));

        Assert.True(result.IsSuccessful);
        var slope = result.GetSeries(CoreTheilSenRegressionIndicator.SlopeSeriesName);
        var intercept = result.GetSeries(CoreTheilSenRegressionIndicator.InterceptSeriesName);
        var upper = result.GetSeries(CoreTheilSenRegressionIndicator.UpperBandSeriesName);
        var lower = result.GetSeries(CoreTheilSenRegressionIndicator.LowerBandSeriesName);
        for (int i = 11; i < closes.Count; i++)
        {
            Assert.Equal(closes[i], result.MainValues[i]);
            Assert.Equal(0.5m, slope[i]);
            Assert.Equal(closes[i - 11], intercept[i]);
            Assert.Equal(closes[i], upper[i]);
            Assert.Equal(closes[i], lower[i]);
        }

        Assert.All(result.MainValues.Take(11), v => Assert.Null(v));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheilSen_EqualsTheKernelOnEveryWindow(bool siegel)
    {
        var candles = Candles(100);
        const int period = 18;
        var result = new CoreTheilSenRegressionIndicator { Period = period, BandMultiplier = 1.5, UseSiegelMedian = siegel }.Calculate(candles);

        var slope = result.GetSeries(CoreTheilSenRegressionIndicator.SlopeSeriesName);
        var intercept = result.GetSeries(CoreTheilSenRegressionIndicator.InterceptSeriesName);
        var upper = result.GetSeries(CoreTheilSenRegressionIndicator.UpperBandSeriesName);
        var lower = result.GetSeries(CoreTheilSenRegressionIndicator.LowerBandSeriesName);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(slope[i]);
                continue;
            }

            decimal[] window = candles.Skip(i - period + 1).Take(period).Select(c => c.Close).ToArray();
            decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(period, siegel)];
            Assert.True(RobustRegressionMath.TryFit(window, siegel, scratch, out RobustLine line));
            Assert.Equal(line.FittedLast, result.MainValues[i]);
            Assert.Equal(line.Slope, slope[i]);
            Assert.Equal(line.Intercept, intercept[i]);
            Assert.Equal(line.FittedLast + (1.5m * line.ResidualSigma), upper[i]);
            Assert.Equal(line.FittedLast - (1.5m * line.ResidualSigma), lower[i]);
            Assert.True(lower[i] <= result.MainValues[i] && result.MainValues[i] <= upper[i]);
        }
    }

    [Fact]
    public void TheilSen_ABandMultiplierOfZero_CollapsesTheBandsOntoTheLine_AndTheModeChangesTheName()
    {
        var candles = Candles(60);
        var result = new CoreTheilSenRegressionIndicator { Period = 10, BandMultiplier = 0.0 }.Calculate(candles);

        Assert.Equal(result.MainValues, result.GetSeries(CoreTheilSenRegressionIndicator.UpperBandSeriesName));
        Assert.Equal(result.MainValues, result.GetSeries(CoreTheilSenRegressionIndicator.LowerBandSeriesName));
        Assert.Equal("Theil-Sen Regression (10)", new CoreTheilSenRegressionIndicator { Period = 10 }.Name);
        Assert.Equal("Siegel Regression (10)", new CoreTheilSenRegressionIndicator { Period = 10, UseSiegelMedian = true }.Name);
    }

    [Fact]
    public void TheilSen_ParameterRanges_FollowTheMeasuredIndicatorBound()
    {
        // The indicator bound is its own measured constant (DI-1 = A2, 2026-11-27): above the 30 bar bound of the other consumers, below the hard cap of the kernel overload.
        Assert.Equal(70, IndicatorDefaultConstants.TheilSenMaxPeriod);
        Assert.True(IndicatorDefaultConstants.TheilSenMaxPeriod > RobustRegressionMath.MaxWindowLength);
        Assert.True(IndicatorDefaultConstants.TheilSenMaxPeriod <= RobustRegressionMath.HardMaxWindowLength);
        Assert.Equal(20, new CoreTheilSenRegressionIndicator().Period);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTheilSenRegressionParameter { Period = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTheilSenRegressionParameter { Period = 71 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTheilSenRegressionParameter { BandMultiplier = -0.1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTheilSenRegressionParameter { BandMultiplier = 10.1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTheilSenRegressionParameter { BandMultiplier = double.NaN }.Validate());
        new CoreTheilSenRegressionParameter { Period = 70, BandMultiplier = 10.0, UseSiegelMedian = true }.Validate();

        Assert.False(new CoreTheilSenRegressionIndicator { Period = 71 }.Calculate(Candles(120)).IsSuccessful);
        var longest = new CoreTheilSenRegressionIndicator { Period = 70, UseSiegelMedian = true }.Calculate(Candles(120));
        Assert.True(longest.IsSuccessful);
        Assert.Null(longest.MainValues[68]);
        Assert.NotNull(longest.MainValues[69]);
        Assert.NotNull(longest.MainValues[119]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheilSen_AWindowAboveThirty_EqualsTheKernelFitBarByBar_AndTheBoundsOfTheOtherConsumersStayThirty(bool siegel)
    {
        const int period = 70;
        var candles = Candles(130);
        var result = new CoreTheilSenRegressionIndicator { Period = period, UseSiegelMedian = siegel }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        for (int i = period - 1; i < candles.Count; i++)
        {
            decimal[] window = candles.Skip(i - period + 1).Take(period).Select(c => c.Close).ToArray();
            decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(period, siegel)];
            Assert.True(RobustRegressionMath.TryFit(window, siegel, scratch, IndicatorDefaultConstants.TheilSenMaxPeriod, out RobustLine line));
            Assert.Equal(line.FittedLast, result.MainValues[i]);
        }

        Assert.Equal(30, SeriesSummaryStatistics.MaxRobustTrendBars);
        Assert.Equal(RobustRegressionMath.MaxWindowLength, SeriesSummaryStatistics.MaxRobustTrendBars);
    }

    // ---------------------------------------------------------------- CornishFisherVaR

    [Fact]
    public void CornishFisherVar_EqualsTheKernelOnTheSimpleReturnWindow_AndWarmsUpAtBarPeriod()
    {
        var candles = Candles(140);
        const int period = 40;
        const double confidence = 0.9;
        var result = new CoreCornishFisherVarIndicator { Period = period, ConfidenceLevel = confidence }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        var gaussian = result.GetSeries(CoreCornishFisherVarIndicator.GaussianVarSeriesName);
        var sharpe = result.GetSeries(CoreCornishFisherVarIndicator.ProbabilisticSharpeRatioSeriesName);
        double[] returns = ReturnSeriesWindow.FromPrices(candles.Select(c => (decimal?)c.Close).ToList(), useLogReturn: false).Returns;
        bool anyCornishFisher = false;
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(gaussian[i]);
                Assert.Null(sharpe[i]);
                continue;
            }

            Assert.True(WindowMoments.TryCompute(returns.AsSpan(i - period + 1, period), FlatWindowRule.Returns, out WindowMoments moments));
            Assert.True(NormalityAndTailMath.TryCornishFisherValueAtRisk(moments, confidence, out double gaussianVar, out double cornishFisherVar));
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(cornishFisherVar), result.MainValues[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(gaussianVar), gaussian[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(NormalityAndTailMath.ProbabilisticSharpeRatio(moments)), sharpe[i]);
            Assert.NotNull(gaussian[i]);
            anyCornishFisher |= result.MainValues[i].HasValue;
        }

        Assert.True(anyCornishFisher);
    }

    [Fact]
    public void CornishFisherVar_IsTheNegativeLowerTailReturn_ForASymmetricWindow()
    {
        // Returns alternate between +1 % and -1 %: mean about 0, so the Gaussian VaR at 95 % is about z * s = -1.645 * 1.01 % (a loss is negative, unlike the portfolio-risk valueAtRisk).
        decimal price = 100m;
        var closes = new List<decimal> { price };
        for (int i = 0; i < 60; i++)
        {
            price *= i % 2 == 0 ? 1.01m : 0.99m;
            closes.Add(price);
        }

        var result = new CoreCornishFisherVarIndicator { Period = 30 }.Calculate(FromCloses(closes));

        decimal gaussian = result.GetSeries(CoreCornishFisherVarIndicator.GaussianVarSeriesName)[^1]!.Value;
        Assert.InRange(gaussian, -0.0175m, -0.0160m);
        decimal cornishFisher = result.MainValues[^1]!.Value;
        Assert.True(cornishFisher < 0m);
    }

    [Fact]
    public void CornishFisherVar_AFlatWindow_KeepsTheGaussianMagnitude_ButNothingThatDividesByTheDeviation()
    {
        var result = new CoreCornishFisherVarIndicator { Period = 20 }.Calculate(FromCloses(Enumerable.Repeat(50m, 60)));

        Assert.True(result.IsSuccessful);
        Assert.All(result.MainValues, v => Assert.Null(v));
        Assert.All(result.GetSeries(CoreCornishFisherVarIndicator.ProbabilisticSharpeRatioSeriesName), v => Assert.Null(v));
        var gaussian = result.GetSeries(CoreCornishFisherVarIndicator.GaussianVarSeriesName);
        Assert.All(gaussian.Take(20), v => Assert.Null(v));
        Assert.All(gaussian.Skip(20), v => Assert.Equal(0m, v));
    }

    [Fact]
    public void CornishFisherVar_WhereTheExpansionIsNotMonotone_HasOnlyTheGaussianVar()
    {
        // 0.1 % growth every bar and one +20 % jump at bar 40: the window holding the jump has G1 = sqrt(30), G2 = 30 (plan section 3.5, U-7): the Cornish-Fisher quantile is not monotone.
        decimal price = 100m;
        var closes = new List<decimal>();
        for (int i = 0; i < 100; i++)
        {
            price *= i == 40 ? 1.2m : 1.001m;
            closes.Add(price);
        }

        var result = new CoreCornishFisherVarIndicator { Period = 30 }.Calculate(FromCloses(closes));

        var gaussian = result.GetSeries(CoreCornishFisherVarIndicator.GaussianVarSeriesName);
        for (int i = 40; i <= 69; i++)
        {
            Assert.Null(result.MainValues[i]);
            Assert.NotNull(gaussian[i]);
        }
    }

    [Fact]
    public void CornishFisherVar_ParameterRanges_AreChecked()
    {
        Assert.Equal(NormalityAndTailMath.MinConfidenceLevel, IndicatorDefaultConstants.CornishFisherVarMinConfidenceLevel);
        Assert.Equal(NormalityAndTailMath.MaxConfidenceLevel, IndicatorDefaultConstants.CornishFisherVarMaxConfidenceLevel);
        Assert.Equal(30, new CoreCornishFisherVarIndicator().Period);
        Assert.Equal(0.95, new CoreCornishFisherVarIndicator().ConfidenceLevel);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreCornishFisherVarParameter { Period = 9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreCornishFisherVarParameter { Period = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreCornishFisherVarParameter { ConfidenceLevel = 0.49 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreCornishFisherVarParameter { ConfidenceLevel = 0.9991 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreCornishFisherVarParameter { ConfidenceLevel = double.NaN }.Validate());
        new CoreCornishFisherVarParameter { Period = 500, ConfidenceLevel = 0.999 }.Validate();
        new CoreCornishFisherVarParameter { ConfidenceLevel = 0.5 }.Validate();

        Assert.False(new CoreCornishFisherVarIndicator { ConfidenceLevel = 1.0 }.Calculate(Candles(60)).IsSuccessful);
    }

    // ---------------------------------------------------------------- contract shared by the three types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.QuantileBands, true, CoreIndicatorCategory.Trend, "Volatility", ChannelNormalization.WindowMinMax },
        new object[] { IndicatorType.TheilSenRegression, true, CoreIndicatorCategory.Trend, "Math", ChannelNormalization.WindowMinMax },
        new object[] { IndicatorType.CornishFisherVaR, false, CoreIndicatorCategory.Volatility, "Math", ChannelNormalization.WindowZScore },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_WithItsParameterObject_AndIsDisabledByDefault(IndicatorType type, bool overlay, CoreIndicatorCategory category, string group, ChannelNormalization normalization)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.Equal(overlay, ((CoreIndicatorBase)IndicatorFactory.Default.Create(type)!).IsOverlay);

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.Equal(overlay, settings.IsOverlay);
        Assert.Equal(category, settings.Category);
        Assert.NotNull(settings.ParameterObject);
        settings.ParameterObject!.Validate();
        Assert.True(settings.ParameterObject!.GetRequiredWarmupBars() > 0);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);

        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { group }, groups);
        Assert.Equal(normalization, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EveryDefaultSeriesIsEmitted_AndMainIsMappedExplicitly_AndSuppressedSeriesHaveNoColourEntry(IndicatorType type, bool overlay, CoreIndicatorCategory category, string group, ChannelNormalization normalization)
    {
        _ = (overlay, category, group, normalization);
        var factory = IndicatorFactory.Default;
        var result = factory.Create(type)!.Calculate(Candles(200));
        Assert.True(result.IsSuccessful);

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
    public void TheSuppressedSeries_AreTheDocumentedOnes()
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.QuantileBands, "IQR"));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.QuantileBands, "MAD"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.QuantileBands, "UpperBand"));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.TheilSenRegression, "Slope"));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.TheilSenRegression, "Intercept"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.TheilSenRegression, "LowerBand"));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.CornishFisherVaR, "ProbabilisticSharpeRatio"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.CornishFisherVaR, "GaussianVaR"));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndSupportsAnIndicatorTimeframe(IndicatorType type, bool overlay, CoreIndicatorCategory category, string group, ChannelNormalization normalization)
    {
        _ = (overlay, category, group, normalization);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.False(ScreenerIndicatorEligibility.IsUniverseBreadthType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void FutureBarsDoNotChangeThePast(IndicatorType type, bool overlay, CoreIndicatorCategory category, string group, ChannelNormalization normalization)
    {
        _ = (overlay, category, group, normalization);
        var original = Candles(220);
        var poisoned = original.ToList();
        for (int i = 150; i < poisoned.Count; i++)
        {
            decimal absurd = 1m + (i % 7) * 400m;
            poisoned[i] = new CoreCandleData(poisoned[i].Timestamp, absurd, absurd, absurd, absurd, 1L);
        }

        var a = IndicatorFactory.Default.Create(type)!.Calculate(original);
        var b = IndicatorFactory.Default.Create(type)!.Calculate(poisoned);
        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, IndicatorFactory.Default))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: IndicatorFactory.Default);
            Assert.Equal(a.GetSeries(name).Take(150), b.GetSeries(name).Take(150));
            Assert.Contains(a.GetSeries(name).Take(150), v => v.HasValue);
        }
    }
}
