using System;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Golden values below come from the former Python handler's algorithm (numpy/scipy) run on the same closed-form series with the same volatility
/// vector; only the volatility model (native EGARCH instead of arch) was changed by the port, and it is an input here.
/// </summary>
public class StructuralDtwMathTests
{
    private const int Length = 300;

    private static double[] Closes() => Enumerable.Range(0, Length)
        .Select(i => 100 + 5 * Math.Sin(2 * Math.PI * i / 24) + 0.02 * i + 1.5 * Math.Sin(2 * Math.PI * i / 7.3)).ToArray();

    private static double[] Volatility() => Enumerable.Range(0, Length).Select(i => 1.5 + 0.5 * Math.Sin(i / 9.0)).ToArray();

    private static double[] MidPrices(double[] closes) => closes.ToArray(); // (close + 0.5 + close - 0.5) / 2

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void Calculate_ReproducesTheReferenceImplementation(int warpingRadius)
    {
        var closes = Closes();

        var result = StructuralDtwMath.Calculate(closes, MidPrices(closes), Volatility(), topK: 5, threshold: 0.3, futureSteps: 20, warpingRadius);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(22, result.DominantPeriod);
        Assert.Equal(22, result.DtwWindow);
        Assert.Equal(1.7296, result.QueryVolatility);
        Assert.Equal(new[] { 110, 15, 230, 205, 135 }, result.Matches.Select(m => m.StartIndex));
        Assert.Equal(new[] { 131, 36, 251, 226, 156 }, result.Matches.Select(m => m.EndIndex));
        Assert.Equal(new[] { 0.0266, 0.2062, 0.2281, 0.2333, 0.2402 }, result.Matches.Select(m => m.Distance));
        Assert.Equal(new[] { 0.9737, 0.8137, 0.7961, 0.7919, 0.7865 }, result.Matches.Select(m => m.Probability));
        Assert.All(result.Matches, m => Assert.Equal(20, m.FuturePath.Count));
        Assert.Equal(new[] { -0.0263, -0.5512, -1.9818 }, result.Matches[0].FuturePath.Take(3));
        Assert.Equal(new[] { -2.301, -4.4767, -5.7567 }, result.Matches[2].FuturePath.Take(3));
    }

    [Fact]
    public void Calculate_TopKAndThreshold_LimitTheMatches()
    {
        var closes = Closes();

        var top2 = StructuralDtwMath.Calculate(closes, MidPrices(closes), Volatility(), 2, 0.3, 20, 3);
        var strict = StructuralDtwMath.Calculate(closes, MidPrices(closes), Volatility(), 5, 0.99, 20, 3);
        var none = StructuralDtwMath.Calculate(closes, MidPrices(closes), Volatility(), 0, 0.3, 20, 3);

        Assert.Equal(new[] { 110, 15 }, top2.Matches.Select(m => m.StartIndex));
        Assert.Empty(strict.Matches);
        Assert.Empty(none.Matches);
    }

    [Fact]
    public void Calculate_MatchesAreSortedAndStayBeforeTheQueryWithRoomForTheFuture()
    {
        var closes = Closes();

        var result = StructuralDtwMath.Calculate(closes, MidPrices(closes), Volatility(), 50, 0.0, 20, 3);

        Assert.True(result.Matches.Count > 5);
        Assert.Equal(result.Matches.Select(m => m.Distance).OrderBy(d => d), result.Matches.Select(m => m.Distance));
        int queryStart = Length - result.DtwWindow;
        Assert.All(result.Matches, m => Assert.True(m.EndIndex + 20 < queryStart));
        Assert.All(result.Matches, m => Assert.InRange(m.Probability, 0.0, 1.0));
    }

    [Fact]
    public void Calculate_FlatHistory_ReturnsNoMatchesWithoutFailing()
    {
        var flat = Enumerable.Repeat(100.0, Length).ToArray();

        var result = StructuralDtwMath.Calculate(flat, flat, Volatility(), 5, 0.3, 20, 3);

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Calculate_ShortHistoryOrMismatchedSeries_Fail()
    {
        var closes = Closes();

        var tooShort = StructuralDtwMath.Calculate(closes.AsSpan(0, 59), closes.AsSpan(0, 59), Volatility().AsSpan(0, 59), 5, 0.3, 20, 3);
        var mismatched = StructuralDtwMath.Calculate(closes, closes, Volatility().AsSpan(0, 100), 5, 0.3, 20, 3);

        Assert.False(tooShort.IsSuccessful);
        Assert.Contains("Insufficient data", tooShort.ErrorMessage);
        Assert.False(mismatched.IsSuccessful);
    }

    [Fact]
    public void EstimateDominantPeriod_ReproducesTheReferenceImplementation()
    {
        var sine = Enumerable.Range(0, Length).Select(i => 100 + 5 * Math.Sin(2 * Math.PI * i / 24)).ToArray();

        Assert.Equal(19, StructuralDtwMath.EstimateDominantPeriod(sine));
        Assert.Equal(22, StructuralDtwMath.EstimateDominantPeriod(Closes()));
    }

    [Fact]
    public void EstimateDominantPeriod_IsClampedToTheAllowedRange()
    {
        var fastAlternation = Enumerable.Range(0, Length).Select(i => i % 2 == 0 ? 101.0 : 99.0).ToArray();

        int period = StructuralDtwMath.EstimateDominantPeriod(fastAlternation);

        Assert.InRange(period, IndicatorDefaultConstants.StructuralDtwMinDominantPeriod, Length / IndicatorDefaultConstants.StructuralDtwMaxDominantPeriodDivisor);
    }

    [Fact]
    public void EstimateDominantPeriod_TooFewPrices_Throws()
    {
        Assert.Throws<ArgumentException>(() => StructuralDtwMath.EstimateDominantPeriod(new double[10]));
    }

    [Fact]
    public void RollingStdVolatility_ReproducesTheReferenceFallback()
    {
        var volatility = StructuralDtwMath.RollingStdVolatility(Closes());

        Assert.Equal(Length, volatility.Length);
        Assert.Equal(0.0, volatility[0]);
        Assert.Equal(volatility[0], volatility[1]);
        Assert.Equal(1.2354175093937505, volatility[50], 9);
        Assert.Equal(1.1883289232432706, volatility[299], 9);
    }
}
