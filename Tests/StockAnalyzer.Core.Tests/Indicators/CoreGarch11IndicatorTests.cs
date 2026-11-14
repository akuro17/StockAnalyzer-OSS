using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// WebAiExpansion plan 04 T3: GARCH(1,1), Gaussian QMLE with variance targeting on percent log returns.
/// Main at bar j = sqrt(sigma^2_(j+1)) from returns up to bar j; first value at bar Period; refit at bars Period + n x RefitInterval.
/// </summary>
public class CoreGarch11IndicatorTests
{
    private const double Tolerance = 1e-9;
    private const int SimulationSeed = 12345;
    private const double SimulatedAlpha = 0.1;
    private const double SimulatedBeta = 0.8;
    private const double SimulatedOmega = 0.1;

    private readonly ITestOutputHelper _output;

    public CoreGarch11IndicatorTests(ITestOutputHelper output) => _output = output;

    private static CoreCandleData Bar(int index, decimal close) =>
        new(new DateTime(2020, 1, 1).AddDays(index), close, close, close, close, 1000);

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) => closes.Select((c, i) => Bar(i, c)).ToList();

    /// <summary>Percent returns of a GARCH(1,1) with the simulation constants (Box-Muller normal shocks from a fixed seed).</summary>
    private static double[] SimulateReturns(int count)
    {
        var random = new Random(SimulationSeed);
        var returns = new double[count];
        double variance = SimulatedOmega / (1.0 - SimulatedAlpha - SimulatedBeta);
        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double r = Math.Sqrt(variance) * z;
            returns[i] = r;
            variance = SimulatedOmega + SimulatedAlpha * r * r + SimulatedBeta * variance;
        }

        return returns;
    }

    /// <summary>Candles whose percent log returns are exactly <paramref name="returns"/> (returns[i] is the return of bar i + 1).</summary>
    private static List<CoreCandleData> CandlesFromReturns(double[] returns)
    {
        var candles = new List<CoreCandleData> { Bar(0, 100m) };
        double price = 100.0;
        for (int i = 0; i < returns.Length; i++)
        {
            price *= Math.Exp(returns[i] / 100.0);
            candles.Add(Bar(i + 1, (decimal)price));
        }

        return candles;
    }

    private static IReadOnlyList<decimal?> Run(IReadOnlyList<CoreCandleData> candles, int period = 250, int refit = 20, bool annualize = false, int periodsPerYear = 252) =>
        new CoreGarch11Indicator { Period = period, RefitInterval = refit, Annualize = annualize, PeriodsPerYear = periodsPerYear }.Calculate(candles).MainValues;

    // ----- Garch11Math -----

    [Fact]
    public void Recursion_TwoStepsMatchTheHandComputation()
    {
        // alpha = 0.1, beta = 0.8, omega = 0.1, sigma^2 = 1: step 1 (eps^2 = 1) = 0.1 + 0.1 + 0.8 = 1.0; step 2 (eps^2 = 4) = 0.1 + 0.4 + 0.8 = 1.3.
        double first = Garch11Math.NextVarianceStep(0.1, 0.1, 0.8, 1.0, 1.0);
        double second = Garch11Math.NextVarianceStep(0.1, 0.1, 0.8, 4.0, first);

        Assert.Equal(1.0, first, Tolerance);
        Assert.Equal(1.3, second, Tolerance);
    }

    [Fact]
    public void NegativeLogLikelihood_MatchesTheHandComputation_AndReturnsTheNextVariance()
    {
        // Returns {1, 2}, mean 0, s^2 = 1 (so omega = 1 x (1 - 0.9) = 0.1), alpha 0.1, beta 0.8: sigma^2_1 = 1, sigma^2_2 = 1.0, next = 1.3.
        // NLL = 0.5 (ln 2pi + 0 + 1) + 0.5 (ln 2pi + 0 + 4) = ln 2pi + 2.5.
        double nll = Garch11Math.NegativeLogLikelihood(new[] { 1.0, 2.0 }, 0, 2, 0.0, 1.0, 0.1, 0.8, out double next);

        Assert.Equal(Math.Log(2.0 * Math.PI) + 2.5, nll, Tolerance);
        Assert.Equal(1.3, next, Tolerance);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(0.1, -0.01)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.2, 0.8)]
    [InlineData(double.NaN, 0.5)]
    public void NegativeLogLikelihood_RejectsAPointOutsideTheConstraints(double alpha, double beta)
    {
        double nll = Garch11Math.NegativeLogLikelihood(new[] { 1.0, 2.0 }, 0, 2, 0.0, 1.0, alpha, beta, out _);

        Assert.Equal(double.MaxValue, nll);
    }

    [Fact]
    public void NegativeLogLikelihood_AcceptsAPersistenceOfExactly0999()
    {
        double nll = Garch11Math.NegativeLogLikelihood(new[] { 1.0, 2.0 }, 0, 2, 0.0, 1.0, 0.0, Garch11Math.MaxPersistence, out _);

        Assert.True(nll < double.MaxValue);
    }

    [Fact]
    public void TryFit_RejectsAWindowWithAnInvalidReturnOrNoVariance()
    {
        Assert.False(Garch11Math.TryFit(new[] { 1.0, double.NaN, 2.0 }, 0, 3, 1000, out _));
        Assert.False(Garch11Math.TryFit(new[] { 0.5, 0.5, 0.5, 0.5 }, 0, 4, 1000, out _));
    }

    [Fact]
    public void TryFit_AnOptimizerResultThatDidNotConvergeFallsBackToTheInitialPoint()
    {
        double[] returns = SimulateReturns(300);

        Assert.True(Garch11Math.TryFit(returns, 0, 300, 1, out Garch11Fit fit));

        // A rejected optimizer result is replaced by exactly the initial coefficients (the model's fixed starting point).
        Assert.Equal(Garch11Math.InitialAlpha, fit.Alpha);
        Assert.Equal(Garch11Math.InitialBeta, fit.Beta);
    }

    [Fact]
    public void TryFit_OnSimulatedGarchData_GivesAPersistenceInsideTheAllowedRange()
    {
        double[] returns = SimulateReturns(1500);

        Assert.True(Garch11Math.TryFit(returns, 0, 1500, 1000, out Garch11Fit fit));

        double persistence = fit.Alpha + fit.Beta;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"alpha = {fit.Alpha}, beta = {fit.Beta}, omega = {fit.Omega}"));
        Assert.NotEqual(Garch11Math.InitialAlpha, fit.Alpha);   // an optimised point, not the fallback
        Assert.InRange(persistence, 0.5 + double.Epsilon, Garch11Math.MaxPersistence);
        Assert.True(fit.Alpha >= 0.0 && fit.Beta >= 0.0);
        Assert.True(fit.NextVariance > 0.0);
    }

    // ----- indicator -----

    [Fact]
    public void EmptyInput_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreGarch11Indicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void FewerBarsThanPeriodPlusOne_AreAllNullAndKeepTheLength()
    {
        // The first value is at bar index Period, so Period bars (indices 0..Period-1) give none.
        var values = Run(CandlesFromReturns(SimulateReturns(49)), period: 50);

        Assert.Equal(50, values.Count);
        Assert.All(values, v => Assert.Null(v));
    }

    [Fact]
    public void TheFirstValueIsAtBarPeriod_AndEveryLaterBarOfAValidSeriesHasAPositiveValue()
    {
        var values = Run(CandlesFromReturns(SimulateReturns(400)), period: 50, refit: 7);

        Assert.Equal(401, values.Count);
        Assert.All(values.Take(50), v => Assert.Null(v));
        Assert.All(values.Skip(50), v =>
        {
            Assert.NotNull(v);
            Assert.True(v > 0m);
        });
    }

    [Fact]
    public void AConstantPrice_GivesNoValueAtAll()
    {
        var values = Run(FromCloses(Enumerable.Repeat(100m, 120)), period: 50, refit: 10);

        Assert.Equal(120, values.Count);
        Assert.All(values, v => Assert.Null(v));
    }

    [Fact]
    public void OnSimulatedGarchData_TheForecastIsFinitePositiveAndNearTheTrueScale()
    {
        // Unconditional sigma of the simulation = sqrt(omega / (1 - alpha - beta)) = 1 percent per bar.
        var values = Run(CandlesFromReturns(SimulateReturns(1500)), period: 250, refit: 20);

        var defined = values.Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        Assert.Equal(1500 + 1 - 250, defined.Count);
        Assert.All(defined, v => Assert.True(double.IsFinite(v) && v > 0.0));
        double mean = defined.Average();
        _output.WriteLine($"mean forecast = {mean}");
        Assert.InRange(mean, 0.5, 2.0);
    }

    [Fact]
    public void TheValueAtABarDoesNotChangeWhenLaterBarsAreRemoved()
    {
        var all = CandlesFromReturns(SimulateReturns(600));

        var full = Run(all, period: 100, refit: 13);
        var truncated = Run(all.Take(350).ToList(), period: 100, refit: 13);

        for (int i = 0; i < truncated.Count; i++) Assert.Equal(full[i], truncated[i]);
    }

    [Fact]
    public void AnUnusableReturnBetweenRefits_GivesNoValueUntilTheNextRefitWithACleanWindow()
    {
        // Period 50, refit every 5 bars: refit bars are 50, 55, 60, ... A zero close at bar 57 (not a refit bar) makes r_57 and r_58 unusable.
        var candles = CandlesFromReturns(SimulateReturns(200));
        candles[57] = Bar(57, 0m);

        var values = Run(candles, period: 50, refit: 5);

        Assert.NotNull(values[56]);
        for (int i = 57; i <= 109; i++) Assert.Null(values[i]);
        // Bar 110 is the first refit bar whose window (r_61 .. r_110) holds no unusable return.
        Assert.NotNull(values[110]);
        Assert.NotNull(values[111]);
    }

    [Fact]
    public void Annualize_MultipliesBySqrtPeriodsPerYear_AndPeriodsPerYearIsIgnoredWhileItIsOff()
    {
        var candles = CandlesFromReturns(SimulateReturns(300));

        var perBar = Run(candles, period: 100, refit: 20);
        var annual = Run(candles, period: 100, refit: 20, annualize: true, periodsPerYear: 52);
        var perBarOtherYear = Run(candles, period: 100, refit: 20, annualize: false, periodsPerYear: 12);

        Assert.Equal(perBar, perBarOtherYear);
        for (int i = 100; i < perBar.Count; i++)
        {
            Assert.Equal((double)perBar[i]!.Value * Math.Sqrt(52.0), (double)annual[i]!.Value, 1e-6);
        }
    }

    [Fact]
    public void FiveThousandBarsWithDefaults_FinishesQuickly()
    {
        var candles = CandlesFromReturns(SimulateReturns(5000));
        var indicator = new CoreGarch11Indicator();

        var stopwatch = Stopwatch.StartNew();
        var result = indicator.Calculate(candles);
        stopwatch.Stop();

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"5000 bars, defaults: {stopwatch.ElapsedMilliseconds} ms"));
        Assert.True(result.IsSuccessful);
        // The plan's reporting threshold is 3 s; the assertion is looser so that a loaded machine does not make the test flaky.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void InvalidProperties_GiveAFailureResult()
    {
        var result = new CoreGarch11Indicator { Period = 10 }.Calculate(CandlesFromReturns(SimulateReturns(100)));

        Assert.False(result.IsSuccessful);
    }

    // ----- parameters and registration -----

    [Fact]
    public void Parameters_DefaultsAndRanges()
    {
        var p = new CoreGarch11Parameter();

        Assert.Equal(250, p.Period);
        Assert.Equal(20, p.RefitInterval);
        Assert.Equal(1000, p.MaxIterations);
        Assert.False(p.Annualize);
        Assert.Equal(252, p.PeriodsPerYear);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarch11Parameter { Period = 49 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarch11Parameter { Period = 2001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarch11Parameter { RefitInterval = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarch11Parameter { RefitInterval = 251 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarch11Parameter { MaxIterations = 49 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarch11Parameter { MaxIterations = 20001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarch11Parameter { PeriodsPerYear = 367 }.Validate());
    }

    [Fact]
    public void Registration_TheTypeIsCreatedWithItsParameter()
    {
        var indicator = IndicatorFactory.CreateStatic(IndicatorType.Garch11);
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.Garch11);

        Assert.NotNull(indicator);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.IsType<CoreGarch11Parameter>(settings.ParameterObject);
    }
}
