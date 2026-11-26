using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// Parity, causality, boundary, and compatibility tests for split <see cref="IndicatorType.Skewness"/> and
/// <see cref="IndicatorType.Kurtosis"/> alongside preserved <see cref="IndicatorType.SkewnessKurtosis"/>.
/// </summary>
public sealed class CoreSkewnessKurtosisSplitTests
{
    private static List<CoreCandleData> BuildCandles(int count, int seed = 29)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double next = close * (1.0 + (0.025 * (rng.NextDouble() - 0.5)) + (i % 13 == 0 ? 0.035 : 0.0));
            decimal c = (decimal)Math.Round(next, 4);
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), c - 0.2m, c + 0.6m, c - 0.5m, c, 1000L + i));
            close = next;
        }

        return candles;
    }

    [Theory]
    [InlineData(true, PriceType.Close)]
    [InlineData(false, PriceType.Close)]
    [InlineData(true, PriceType.Typical)]
    [InlineData(false, PriceType.Median)]
    public void SplitIndicators_MatchLegacyCombinedSeries_BarByBar(bool useLogReturn, PriceType priceSource)
    {
        var candles = BuildCandles(120);
        const int period = 30;

        var combined = new CoreSkewnessKurtosisIndicator { Period = period, UseLogReturn = useLogReturn, PriceSource = priceSource };
        var skew = new CoreSkewnessIndicator { Period = period, UseLogReturn = useLogReturn, PriceSource = priceSource };
        var kurt = new CoreKurtosisIndicator { Period = period, UseLogReturn = useLogReturn, PriceSource = priceSource };

        IIndicatorResult combinedRes = combined.Calculate(candles);
        IIndicatorResult skewRes = skew.Calculate(candles);
        IIndicatorResult kurtRes = kurt.Calculate(candles);

        Assert.True(combinedRes.IsSuccessful);
        Assert.True(skewRes.IsSuccessful);
        Assert.True(kurtRes.IsSuccessful);

        IReadOnlyList<decimal?> legacyKurt = combinedRes.GetSeries(CoreSkewnessKurtosisIndicator.ExcessKurtosisSeriesName);
        Assert.Equal(combinedRes.MainValues, skewRes.MainValues);
        Assert.Equal(legacyKurt, kurtRes.MainValues);
        Assert.Contains(skewRes.MainValues, v => v.HasValue);
        Assert.Contains(kurtRes.MainValues, v => v.HasValue);
    }

    [Fact]
    public void SplitIndicators_AreCausal_AppendingFutureBarsDoesNotMutatePastBars()
    {
        var full = BuildCandles(110);
        var prefix = full.Take(75).ToList();

        foreach (IndicatorType type in new[] { IndicatorType.Skewness, IndicatorType.Kurtosis })
        {
            ICoreIndicator indPrefix = IndicatorFactory.CreateStatic(type)!;
            ICoreIndicator indFull = IndicatorFactory.CreateStatic(type)!;

            var rPrefix = indPrefix.Calculate(prefix);
            var rFull = indFull.Calculate(full);

            Assert.True(rPrefix.IsSuccessful);
            Assert.True(rFull.IsSuccessful);
            for (int i = 0; i < prefix.Count; i++)
            {
                Assert.Equal(rPrefix.MainValues[i], rFull.MainValues[i]);
            }
        }
    }

    [Fact]
    public void SplitIndicators_BoundaryConditions_ReturnNullWithoutThrowing()
    {
        foreach (IndicatorType type in new[] { IndicatorType.Skewness, IndicatorType.Kurtosis })
        {
            ICoreIndicator ind = IndicatorFactory.CreateStatic(type)!;

            // Empty
            Assert.Empty(ind.Calculate(Array.Empty<CoreCandleData>()).MainValues);

            // Warmup shortage (< Period + 1 bars)
            var shortCandles = BuildCandles(25);
            var shortResult = ind.Calculate(shortCandles);
            Assert.True(shortResult.IsSuccessful);
            Assert.All(shortResult.MainValues, Assert.Null);

            // Flat window
            var flat = Enumerable.Range(0, 60)
                .Select(i => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), 50m, 50m, 50m, 50m, 100L))
                .ToList();
            var flatResult = ind.Calculate(flat);
            Assert.True(flatResult.IsSuccessful);
            Assert.All(flatResult.MainValues, Assert.Null);

            // Non-positive price inside log-return window
            var withZero = BuildCandles(60);
            withZero[40] = new CoreCandleData(withZero[40].Timestamp, 0m, 0m, 0m, 0m, 100L);
            var zeroLogResult = ind.Calculate(withZero);
            Assert.True(zeroLogResult.IsSuccessful);
            Assert.Null(zeroLogResult.MainValues[40]);
            Assert.Null(zeroLogResult.MainValues[41]);

            // Simple return with zero prior price
            var simpleParam = type == IndicatorType.Skewness
                ? (CoreIndicatorParameterBase)new CoreSkewnessParameter { Period = 30, UseLogReturn = false }
                : new CoreKurtosisParameter { Period = 30, UseLogReturn = false };
            ICoreIndicator simpleInd = IndicatorFactory.CreateStatic(type, simpleParam)!;
            var zeroSimpleResult = simpleInd.Calculate(withZero);
            Assert.True(zeroSimpleResult.IsSuccessful);
            Assert.Null(zeroSimpleResult.MainValues[41]);

            // Out-of-range period
            var badParam = type == IndicatorType.Skewness
                ? (CoreIndicatorParameterBase)new CoreSkewnessParameter { Period = 9 }
                : new CoreKurtosisParameter { Period = 501 };
            Assert.Throws<ArgumentOutOfRangeException>(() => badParam.Validate());
        }
    }

    [Fact]
    public void LegacySkewnessKurtosis_RemainsRegisteredAndRoundTripsPolymorphicJson()
    {
        Assert.True(IndicatorFactory.IsRegisteredStatic(IndicatorType.SkewnessKurtosis));
        Assert.True(IndicatorFactory.IsRegisteredStatic(IndicatorType.Skewness));
        Assert.True(IndicatorFactory.IsRegisteredStatic(IndicatorType.Kurtosis));

        Assert.False(BacktestIndicatorEligibility.IsTypeBlocked(IndicatorType.Skewness));
        Assert.False(BacktestIndicatorEligibility.IsTypeBlocked(IndicatorType.Kurtosis));
        Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(IndicatorType.Skewness));
        Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(IndicatorType.Kurtosis));

        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.Skewness));
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.Kurtosis));

        var mathGroup = ScreenerIndicatorGroup.GetDefaultGroups().Single(g => g.Name == "Math" && g.CategoryType == "Indicator");
        Assert.Contains(IndicatorType.SkewnessKurtosis, mathGroup.IndicatorTypes);
        Assert.Contains(IndicatorType.Skewness, mathGroup.IndicatorTypes);
        Assert.Contains(IndicatorType.Kurtosis, mathGroup.IndicatorTypes);

        var options = new JsonSerializerOptions { TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver() };
        var legacy = new CoreIndicatorSettings { TypeEnum = IndicatorType.SkewnessKurtosis, ParameterObject = new CoreSkewnessKurtosisParameter { Period = 45, UseLogReturn = false } };
        var skew = new CoreIndicatorSettings { TypeEnum = IndicatorType.Skewness, ParameterObject = new CoreSkewnessParameter { Period = 45, UseLogReturn = false } };
        var kurt = new CoreIndicatorSettings { TypeEnum = IndicatorType.Kurtosis, ParameterObject = new CoreKurtosisParameter { Period = 45, UseLogReturn = false } };

        var legacyRestored = JsonSerializer.Deserialize<CoreIndicatorSettings>(JsonSerializer.Serialize(legacy, options), options)!;
        var skewRestored = JsonSerializer.Deserialize<CoreIndicatorSettings>(JsonSerializer.Serialize(skew, options), options)!;
        var kurtRestored = JsonSerializer.Deserialize<CoreIndicatorSettings>(JsonSerializer.Serialize(kurt, options), options)!;

        Assert.IsType<CoreSkewnessKurtosisParameter>(legacyRestored.ParameterObject);
        Assert.IsType<CoreSkewnessParameter>(skewRestored.ParameterObject);
        Assert.IsType<CoreKurtosisParameter>(kurtRestored.ParameterObject);
        Assert.Equal(45, ((CoreSkewnessParameter)skewRestored.ParameterObject!).Period);
        Assert.False(((CoreKurtosisParameter)kurtRestored.ParameterObject!).UseLogReturn);
    }
}
