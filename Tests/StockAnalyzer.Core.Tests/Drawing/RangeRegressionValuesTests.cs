using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The polynomial and the nonlinear regression of a drawing range (DSL expansion run D-2).</summary>
public sealed class RangeRegressionValuesTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> WithCloses(IEnumerable<decimal> closes)
    {
        var list = new List<CoreCandleData>();
        int i = 0;
        foreach (decimal close in closes)
        {
            list.Add(new CoreCandleData(Start.AddDays(i++), close, close, close, close, 1000L));
        }

        return list;
    }

    /// <summary>A noisy upward path (deterministic), always above zero.</summary>
    private static List<CoreCandleData> Noisy(int count, decimal first = 100m)
    {
        double state = 777.0;
        var closes = new List<decimal>(count);
        for (int i = 0; i < count; i++)
        {
            state = (state * 1103515245.0 + 12345.0) % 2147483648.0;
            double noise = (state / 2147483648.0) - 0.5;
            closes.Add(Math.Round(first + (i * 0.15m) + (decimal)(Math.Sin(i * 0.2) * 3.0) + (decimal)noise, 4));
        }

        return WithCloses(closes);
    }

    // ---------------------------------------------------------------- polynomial

    [Fact]
    public void Polynomial_RecoversAKnownCubicOnEveryBar()
    {
        const int n = 40;
        double[] coefficients = { 100.0, 8.0, -5.0, 3.0 };
        var closes = new List<decimal>();
        for (int i = 0; i < n; i++)
        {
            double u = (2.0 * i / (n - 1)) - 1.0;
            closes.Add((decimal)Math.Round(coefficients[0] + (coefficients[1] * u) + (coefficients[2] * u * u) + (coefficients[3] * u * u * u), 10));
        }

        var candles = WithCloses(closes);
        PolynomialRegressionFit fit = PolynomialRegressionProjectionValues.Calculate(candles, 0, n - 1, 3, 0, 2.0m);

        Assert.True(fit.IsOk);
        Assert.Equal(n, fit.Curve.Count);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal((double)closes[i], (double)fit.Curve.Prices[i], 1e-8);
        }

        Assert.True(fit.ResidualSigma < 1e-7m);
        Assert.Equal(1.0, fit.RSquared, 9);
        Assert.Equal(closes[n - 1], fit.FittedLast, 7);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Polynomial_CurveAtTheProjectionPoints_EqualsTheDslProjectionOfTheIndicator(int degree)
    {
        var candles = Noisy(120);
        const int forecast = 10;

        PolynomialRegressionFit fit = PolynomialRegressionProjectionValues.Calculate(candles, 0, 119, degree, forecast, 2.0m);

        Assert.True(fit.IsOk);
        Assert.Equal(120 + forecast, fit.Curve.Count);
        double[] series = candles.Select(c => (double)c.Close).ToArray();
        for (int horizon = 0; horizon <= forecast; horizon++)
        {
            var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
            MathResult<double> expected = DslRegressionMath.PolyProject(series, degree, horizon, ref budget);
            Assert.True(expected.IsOk);
            double actual = (double)fit.Curve.Prices[119 + horizon];
            Assert.Equal(expected.Value, actual, Math.Max(1e-6, Math.Abs(expected.Value) * 1e-9));
        }
    }

    [Fact]
    public void Polynomial_BandOfDegreeOne_IsTheBandOfTheRegressionTrendTool()
    {
        // DX-13 = A: the residual standard deviation divides by N, like RegressionTrend, so the two tools draw the same band over the same range.
        var candles = Noisy(80);

        PolynomialRegressionFit fit = PolynomialRegressionProjectionValues.Calculate(candles, 10, 69, 1, 0, 2.0m);
        RegressionTrendFit trend = RegressionTrendValues.Fit(candles, candles[10].Timestamp, candles[69].Timestamp);

        Assert.True(fit.IsOk);
        Assert.True(trend.Result.IsValid);
        Assert.Equal((double)trend.Result.StdDev, (double)fit.ResidualSigma, 6);
        Assert.Equal((double)trend.Result.StdDev * 2.0, (double)fit.Curve.BandHalfWidth, 6);
        // The line itself is the regression line.
        Assert.Equal((double)trend.Result.GetValueAt(0), (double)fit.Curve.Prices[0], 5);
        Assert.Equal((double)trend.Result.GetValueAt(trend.Result.Count - 1), (double)fit.FittedLast, 5);
    }

    [Fact]
    public void Polynomial_NeedsDegreePlusTwoBars()
    {
        var candles = Noisy(30);

        Assert.Equal(RangeFitStatus.TooFewBars, PolynomialRegressionProjectionValues.Calculate(candles, 0, 3, 3, 0, 2.0m).Status);
        Assert.Equal(RangeFitStatus.Ok, PolynomialRegressionProjectionValues.Calculate(candles, 0, 4, 3, 0, 2.0m).Status);
        Assert.Equal(5, PolynomialRegressionProjectionValues.MinBars(3));
        Assert.Equal("Insufficient bars (< 5)", PolynomialRegressionProjectionValues.StatusText(PolynomialRegressionProjectionValues.Calculate(candles, 0, 3, 3, 0, 2.0m)));
    }

    [Fact]
    public void Polynomial_LongRange_IsFittedOnTheNewestBarsAndSaysSo()
    {
        var candles = Noisy(PolynomialRegressionProjectionValues.MaxBars + 90);
        int last = candles.Count - 1;

        PolynomialRegressionFit full = PolynomialRegressionProjectionValues.Calculate(candles, 0, last, 2, 3, 2.0m);
        PolynomialRegressionFit newest = PolynomialRegressionProjectionValues.Calculate(candles, last - 511, last, 2, 3, 2.0m);

        Assert.True(full.IsOk);
        Assert.Equal(candles.Count, full.RangeBars);
        Assert.Equal(512, full.EffectiveBars);
        Assert.True(full.IsTruncated);
        Assert.Equal(512 + 3, full.Curve.Count);
        Assert.Equal(newest.Curve.Prices, full.Curve.Prices);
        Assert.Equal(newest.Curve.Times, full.Curve.Times);
        Assert.Equal("Last 512 bars", RangeFitText.TruncationBadge(full.RangeBars, full.EffectiveBars));
        Assert.Equal(string.Empty, RangeFitText.TruncationBadge(newest.RangeBars, newest.EffectiveBars));
    }

    [Fact]
    public void Polynomial_ProjectionTimes_UseExistingBarsThenTheBarSpan()
    {
        var candles = Noisy(100);

        // The range ends 4 bars before the data end: the first 4 projected points are the existing bars, then the span of the data.
        PolynomialRegressionFit fit = PolynomialRegressionProjectionValues.Calculate(candles, 20, 95, 2, 7, 2.0m);

        Assert.True(fit.IsOk);
        Assert.Equal(76 + 7, fit.Curve.Count);
        Assert.Equal(76, fit.Curve.FittedCount);
        for (int k = 1; k <= 4; k++)
        {
            Assert.Equal(candles[95 + k].Timestamp, fit.Curve.Times[75 + k]);
        }

        Assert.Equal(candles[99].Timestamp + TimeSpan.FromDays(1), fit.Curve.Times[80]);
        Assert.Equal(candles[99].Timestamp + TimeSpan.FromDays(3), fit.Curve.Times[82]);
    }

    [Fact]
    public void Polynomial_ParametersAreClamped_AndInvalidRangesGiveNothing()
    {
        var candles = Noisy(60);

        PolynomialRegressionFit clamped = PolynomialRegressionProjectionValues.Calculate(candles, 0, 59, 99, 99, 99m);
        Assert.Equal(PolynomialRegressionProjectionValues.MaxDegree, clamped.Degree);
        Assert.Equal(PolynomialRegressionProjectionValues.MaxForecastBars, clamped.ForecastBars);
        Assert.Equal((decimal)RegressionTrendValues.MaxStdDevMultiplierRange, clamped.StdDevMultiplier);

        PolynomialRegressionFit low = PolynomialRegressionProjectionValues.Calculate(candles, 0, 59, -3, -3, 0m);
        Assert.Equal(PolynomialRegressionProjectionValues.MinDegree, low.Degree);
        Assert.Equal(0, low.ForecastBars);
        Assert.Equal((decimal)RegressionTrendValues.MinStdDevMultiplierRange, low.StdDevMultiplier);

        Assert.Equal(RangeFitStatus.NoRange, PolynomialRegressionProjectionValues.Calculate(candles, -1, 10, 2, 0, 2.0m).Status);
        Assert.Equal(RangeFitStatus.NoRange, PolynomialRegressionProjectionValues.Calculate(candles, 5, 60, 2, 0, 2.0m).Status);
        Assert.Equal(RangeFitStatus.NoRange, PolynomialRegressionProjectionValues.Calculate(candles, 9, 5, 2, 0, 2.0m).Status);
        Assert.Empty(PolynomialRegressionProjectionValues.Calculate(PolynomialRegressionFit.Empty, IndicatorColor.Gray));
    }

    [Fact]
    public void Polynomial_Values_ListTheFitTheBandAndTheCounts()
    {
        var candles = Noisy(PolynomialRegressionProjectionValues.MaxBars + 20);
        PolynomialRegressionFit fit = PolynomialRegressionProjectionValues.Calculate(candles, 0, candles.Count - 1, 2, 5, 1.5m);

        IReadOnlyList<DrawingCalculatedValue> values = PolynomialRegressionProjectionValues.Calculate(fit, IndicatorColor.Gray);

        Assert.Equal(
            new[] { "PolyFitted", "PolyProjected", "PolyUpper", "PolyLower", "PolySigma", "PolyRSquared", "PolyDegree", "RangeBars", "EffectiveBars" },
            values.Select(v => v.Key).ToArray());
        Assert.Equal(fit.FittedLast, values.Single(v => v.Key == "PolyFitted").NumericValue);
        Assert.Equal(fit.Curve.Prices[^1], values.Single(v => v.Key == "PolyProjected").NumericValue);
        Assert.Equal(fit.FittedLast + fit.Curve.BandHalfWidth, values.Single(v => v.Key == "PolyUpper").NumericValue);
        Assert.Equal(fit.FittedLast - fit.Curve.BandHalfWidth, values.Single(v => v.Key == "PolyLower").NumericValue);
        Assert.Equal("+1.5σ Band", values.Single(v => v.Key == "PolyUpper").Label);
        Assert.Equal(candles.Count, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(512, values.Single(v => v.Key == "EffectiveBars").NumericValue);

        // Without a projection there is no projected row; a failed fit has the state and the counts.
        var noProjection = PolynomialRegressionProjectionValues.Calculate(PolynomialRegressionProjectionValues.Calculate(candles, 0, 99, 2, 0, 2.0m), IndicatorColor.Gray);
        Assert.DoesNotContain(noProjection, v => v.Key == "PolyProjected");
        var tooShort = PolynomialRegressionProjectionValues.Calculate(PolynomialRegressionProjectionValues.Calculate(candles, 0, 2, 2, 0, 2.0m), IndicatorColor.Gray);
        Assert.Equal(new[] { "State", "RangeBars", "EffectiveBars" }, tooShort.Select(v => v.Key).ToArray());
    }

    [Fact]
    public void Polynomial_FlatCloses_DoNotThrow_AndHaveNoRSquared()
    {
        var candles = WithCloses(Enumerable.Repeat(100m, 40));

        PolynomialRegressionFit fit = PolynomialRegressionProjectionValues.Calculate(candles, 0, 39, 2, 0, 2.0m);

        if (fit.IsOk)
        {
            Assert.True(double.IsNaN(fit.RSquared));
            Assert.All(fit.Curve.Prices, p => Assert.Equal(100m, p, 6));
            Assert.Equal("-", PolynomialRegressionProjectionValues.Calculate(fit, IndicatorColor.Gray).Single(v => v.Key == "PolyRSquared").FormattedText);
        }
        else
        {
            Assert.Equal(RangeFitStatus.FitFailed, fit.Status);
        }
    }

    // ---------------------------------------------------------------- nonlinear

    [Theory]
    [InlineData(NonlinearRegressionModel.Exponential)]
    [InlineData(NonlinearRegressionModel.Logarithmic)]
    [InlineData(NonlinearRegressionModel.Power)]
    [InlineData(NonlinearRegressionModel.Logistic)]
    public void Nonlinear_LastBar_EqualsTheIndicatorOverTheSameWindow(NonlinearRegressionModel model)
    {
        var candles = Noisy(70);
        const int period = 45;
        var indicator = new CoreNonlinearRegressionIndicator { Period = period, ModelType = model };
        var result = indicator.Calculate(candles);
        var parameterB = indicator.ParameterB;
        var rSquared = indicator.RSquared;

        NonlinearRegressionFit fit = NonlinearRegressionCurveValues.Calculate(candles, 69 - period + 1, 69, model, 0);

        Assert.True(fit.IsOk);
        Assert.Equal(period, fit.EffectiveBars);
        Assert.Equal(period, fit.Curve.Count);
        Assert.Equal(result.MainValues[69], fit.FittedLast);
        Assert.Equal(parameterB[69], fit.ShapeParameter);
        Assert.Equal(rSquared[69], IndicatorCalculationHelperToDecimal(fit.RSquared));
    }

    private static decimal? IndicatorCalculationHelperToDecimal(double value)
        => StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(value);

    [Fact]
    public void Nonlinear_ExponentialData_IsRecovered_AndTheExtensionFollowsTheCurve()
    {
        const int n = 40;
        var closes = new List<decimal>();
        for (int x = 1; x <= n; x++)
        {
            closes.Add((decimal)Math.Round(2.0 * Math.Exp(0.03 * x), 10));
        }

        var candles = WithCloses(closes);
        NonlinearRegressionFit fit = NonlinearRegressionCurveValues.Calculate(candles, 0, n - 1, NonlinearRegressionModel.Exponential, 12);

        Assert.True(fit.IsOk);
        Assert.Equal(n + 12, fit.Curve.Count);
        Assert.Equal(0.03, (double)fit.ShapeParameter!.Value, 8);
        Assert.Equal(1.0, fit.RSquared, 9);
        for (int x = 1; x <= n + 12; x++)
        {
            Assert.Equal(2.0 * Math.Exp(0.03 * x), (double)fit.Curve.Prices[x - 1], 6);
        }

        Assert.Equal(n, fit.Curve.FittedCount);
    }

    [Fact]
    public void Nonlinear_LogModelsNeedPositiveCloses_OtherModelsDoNot()
    {
        var closes = Enumerable.Range(0, 30).Select(i => -10m + i).ToList();   // -10 .. 19
        var candles = WithCloses(closes);

        Assert.Equal(RangeFitStatus.FitFailed, NonlinearRegressionCurveValues.Calculate(candles, 0, 29, NonlinearRegressionModel.Exponential, 0).Status);
        Assert.Equal(RangeFitStatus.FitFailed, NonlinearRegressionCurveValues.Calculate(candles, 0, 29, NonlinearRegressionModel.Power, 0).Status);
        Assert.Equal(RangeFitStatus.Ok, NonlinearRegressionCurveValues.Calculate(candles, 0, 29, NonlinearRegressionModel.Logarithmic, 0).Status);
        Assert.Equal(RangeFitStatus.Ok, NonlinearRegressionCurveValues.Calculate(candles, 0, 29, NonlinearRegressionModel.Logistic, 0).Status);
    }

    [Fact]
    public void Nonlinear_Logistic_FlatRangeHasNoCurve_AndASigmoidIsFollowed()
    {
        var flat = WithCloses(Enumerable.Repeat(50m, 30));
        Assert.Equal(RangeFitStatus.FitFailed, NonlinearRegressionCurveValues.Calculate(flat, 0, 29, NonlinearRegressionModel.Logistic, 0).Status);

        var closes = new List<decimal>();
        for (int x = 1; x <= 60; x++)
        {
            closes.Add(Math.Round(100m + (40m / (1m + (decimal)Math.Exp(-0.2 * (x - 30)))), 6));
        }

        NonlinearRegressionFit fit = NonlinearRegressionCurveValues.Calculate(WithCloses(closes), 0, 59, NonlinearRegressionModel.Logistic, 20);

        Assert.True(fit.IsOk);
        Assert.True(fit.RSquared > 0.99);
        // Back in price: the curve stays between the minimum and the maximum of the window (the mapping inverse), also in the extension.
        decimal min = closes.Min();
        decimal max = closes.Max();
        Assert.All(fit.Curve.Prices, p => Assert.InRange(p, min - 0.5m, max + 0.5m));
        Assert.Equal(60 + 20, fit.Curve.Count);
    }

    [Fact]
    public void Nonlinear_NeedsFiveBars_AndLongRangesAreCut()
    {
        var candles = Noisy(NonlinearRegressionCurveValues.MaxBars + 60);

        Assert.Equal(RangeFitStatus.TooFewBars, NonlinearRegressionCurveValues.Calculate(candles, 0, 3, NonlinearRegressionModel.Logarithmic, 0).Status);
        Assert.Equal("Insufficient bars (< 5)", NonlinearRegressionCurveValues.StatusText(NonlinearRegressionCurveValues.Calculate(candles, 0, 3, NonlinearRegressionModel.Logarithmic, 0)));
        Assert.Equal(RangeFitStatus.Ok, NonlinearRegressionCurveValues.Calculate(candles, 0, 4, NonlinearRegressionModel.Logarithmic, 0).Status);

        int last = candles.Count - 1;
        NonlinearRegressionFit full = NonlinearRegressionCurveValues.Calculate(candles, 0, last, NonlinearRegressionModel.Logarithmic, 0);
        NonlinearRegressionFit newest = NonlinearRegressionCurveValues.Calculate(candles, last - 511, last, NonlinearRegressionModel.Logarithmic, 0);
        Assert.Equal(512, full.EffectiveBars);
        Assert.True(full.IsTruncated);
        Assert.Equal(newest.Curve.Prices, full.Curve.Prices);
        Assert.Equal(newest.ShapeParameter, full.ShapeParameter);
    }

    [Fact]
    public void Nonlinear_ExtendBarsAreClamped_AndValuesCarryTheFit()
    {
        var candles = Noisy(80);

        Assert.Equal(NonlinearRegressionCurveValues.MaxExtendBars, NonlinearRegressionCurveValues.Calculate(candles, 0, 79, NonlinearRegressionModel.Exponential, 999).ExtendBars);
        Assert.Equal(0, NonlinearRegressionCurveValues.Calculate(candles, 0, 79, NonlinearRegressionModel.Exponential, -4).ExtendBars);
        Assert.Equal(RangeFitStatus.NoRange, NonlinearRegressionCurveValues.Calculate(candles, 40, 80, NonlinearRegressionModel.Exponential, 0).Status);
        Assert.Empty(NonlinearRegressionCurveValues.Calculate(NonlinearRegressionFit.Empty, IndicatorColor.Gray));

        NonlinearRegressionFit fit = NonlinearRegressionCurveValues.Calculate(candles, 0, 79, NonlinearRegressionModel.Power, 10);
        IReadOnlyList<DrawingCalculatedValue> values = NonlinearRegressionCurveValues.Calculate(fit, IndicatorColor.Gray);

        Assert.Equal(new[] { "NlModel", "NlFitted", "NlProjected", "NlParameterB", "NlRSquared", "RangeBars", "EffectiveBars" }, values.Select(v => v.Key).ToArray());
        Assert.Equal("Power", values[0].FormattedText);
        Assert.Equal(fit.FittedLast, values.Single(v => v.Key == "NlFitted").NumericValue);
        Assert.Equal(fit.Curve.Prices[^1], values.Single(v => v.Key == "NlProjected").NumericValue);
        Assert.Equal(fit.ShapeParameter, values.Single(v => v.Key == "NlParameterB").NumericValue);
        Assert.Equal(80, values.Single(v => v.Key == "RangeBars").NumericValue);

        var tooShort = NonlinearRegressionCurveValues.Calculate(NonlinearRegressionCurveValues.Calculate(candles, 0, 2, NonlinearRegressionModel.Power, 0), IndicatorColor.Gray);
        Assert.Equal(new[] { "State", "RangeBars", "EffectiveBars" }, tooShort.Select(v => v.Key).ToArray());
    }

    // ---------------------------------------------------------------- shared curve kernel

    [Theory]
    [InlineData(NonlinearRegressionModel.Exponential)]
    [InlineData(NonlinearRegressionModel.Logarithmic)]
    [InlineData(NonlinearRegressionModel.Power)]
    [InlineData(NonlinearRegressionModel.Logistic)]
    public void CurveFit_ValueAt_ReproducesTheFittedCurveOfTheKernel(NonlinearRegressionModel model)
    {
        const int n = 35;
        var y = new double[n];
        var x = new double[n];
        for (int j = 0; j < n; j++)
        {
            x[j] = j + 1;
            y[j] = 50.0 + (j * 0.6) + (Math.Sin(j * 0.5) * 2.0);
        }

        var fitted = new double[n];
        var mapped = new double[n];
        Assert.True(NonlinearCurveFitMath.TryFit(model, x, y, fitted, mapped, out NonlinearCurveFit fit, out double fittedLast, out double rSquared));

        for (int j = 0; j < n; j++)
        {
            Assert.Equal(fitted[j], fit.ValueAt(x[j]));
        }

        Assert.Equal(fitted[n - 1], fittedLast);
        Assert.True(double.IsFinite(rSquared));
        Assert.Equal(model, fit.Model);
    }

    [Fact]
    public void RangeCurve_CutsTheProjectionOnlyAndKeepsTheFittedPart()
    {
        var times = new[] { Start, Start.AddDays(1), Start.AddDays(2), Start.AddDays(3) };
        var prices = new[] { 1m, 2m, 3m, 4m };

        RangeCurve whole = RangeCurve.Create(times, prices, 4, 3, 0.5m);
        RangeCurve cut = RangeCurve.Create(times, prices, 3, 3, 0.5m);

        Assert.Same(times, whole.Times);
        Assert.Equal(4, whole.Count);
        Assert.Equal(3, cut.Count);
        Assert.Equal(new[] { 1m, 2m, 3m }, cut.Prices);
        Assert.Equal(3, cut.FittedCount);
        Assert.Equal(0.5m, cut.BandHalfWidth);
        Assert.Equal(0, RangeCurve.Empty.Count);
    }
}
