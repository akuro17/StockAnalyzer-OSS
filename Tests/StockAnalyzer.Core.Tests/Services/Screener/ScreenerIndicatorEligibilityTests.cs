using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Screener;

/// <summary>
/// <see cref="ScreenerIndicatorEligibility"/> is the Screener's single entry point for "may this output be evaluated". Its rule differs from
/// <see cref="BacktestIndicatorEligibility"/> in exactly one way: an indicator that compares two symbols (<see cref="ICrossTickerIndicator"/>) is NOT refused for being
/// "synchronously unsupported" (that refusal only existed because the L2 causality test could not evaluate it without a comparison symbol; the two-ticker L2 test now proves
/// it causal in both symbols). Everything else answers exactly what the Backtest rule answers, for every indicator type and every output name. The types and names come from the
/// registry, not from a list in this file.
/// </summary>
public sealed class ScreenerIndicatorEligibilityTests
{
    private static bool IsTwoTicker(IndicatorType type) => IndicatorFactory.CreateStatic(type) is ICrossTickerIndicator;

    private static IEnumerable<(IndicatorType Type, string? Output)> Cases()
    {
        foreach (IndicatorType type in Enum.GetValues<IndicatorType>().Distinct())
        {
            yield return (type, null);
            yield return (type, IndicatorResult.MainSeriesName);
            yield return (type, "   ");
            foreach (string name in IndicatorOutputSeriesResolver.GetDisplayNames(type, IndicatorFactory.Default))
            {
                yield return (type, name);
            }
        }

        // The series-level rule: Ichimoku's ChikouSpan carries a future close.
        yield return (IndicatorType.Ichimoku, "ChikouSpan");
        yield return (IndicatorType.Ichimoku, "chikouspan");
    }

    /// <summary>The indicator types the Screener enables although the Backtest refuses them: the two-ticker types in the Backtest's synchronous-unsupported list.</summary>
    private static IndicatorType[] EnabledByTheScreenerOnly()
        => BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes
            .Where(IsTwoTicker)
            .OrderBy(type => type.ToString(), StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void EveryOtherTypeAndOutputGetsTheAnswerOfTheBacktestRule()
    {
        IndicatorType[] enabled = EnabledByTheScreenerOnly();
        int compared = 0;
        foreach ((IndicatorType type, string? output) in Cases())
        {
            if (enabled.Contains(type))
            {
                continue;
            }

            Assert.True(
                BacktestIndicatorEligibility.Check(type, output) == ScreenerIndicatorEligibility.Check(type, output),
                $"Check({type}, {output ?? "null"})");
            Assert.True(
                BacktestIndicatorEligibility.IsAllowed(type, output) == ScreenerIndicatorEligibility.IsAllowed(type, output),
                $"IsAllowed({type}, {output ?? "null"})");
            compared++;
        }

        Assert.True(compared > 100, $"only {compared} cases were compared");
    }

    [Fact]
    public void TheTwoTickerTypesTheBacktestRefusesAsSynchronouslyUnsupported_AreAllowedInTheScreener_EverySeries()
    {
        IndicatorType[] enabled = EnabledByTheScreenerOnly();

        // The seven types of the plan (derived from the registry, then named once as the expectation of the derivation).
        Assert.Equal(
            new[]
            {
                IndicatorType.CointegrationSpread, IndicatorType.PairRatio, IndicatorType.PairSpread, IndicatorType.Ratiocator,
                IndicatorType.RollingAlpha, IndicatorType.RollingBeta, IndicatorType.RollingRSquared,
            }.OrderBy(type => type.ToString(), StringComparer.Ordinal),
            enabled);

        foreach (IndicatorType type in enabled)
        {
            Assert.Equal(BacktestIndicatorViolationReason.SynchronousCalculationUnsupported, BacktestIndicatorEligibility.Check(type, null));
            Assert.True(ScreenerIndicatorEligibility.IsAllowed(type), type.ToString());
            Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(type), type.ToString());
            foreach (string name in IndicatorOutputSeriesResolver.GetDisplayNames(type, IndicatorFactory.Default))
            {
                Assert.True(ScreenerIndicatorEligibility.IsAllowed(type, name), $"{type}.{name}");
            }

            // The Backtest, the training export and the prediction validation still refuse them.
            Assert.False(BacktestIndicatorEligibility.IsAllowed(type), type.ToString());
            Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(type), type.ToString());
        }
    }

    [Fact]
    public void TheTwoTickerTypesTheScreenerAlreadyEvaluated_StayAllowed()
    {
        Assert.True(ScreenerIndicatorEligibility.IsAllowed(IndicatorType.Correlation));
        Assert.True(ScreenerIndicatorEligibility.IsAllowed(IndicatorType.CrossCorrelation));
        Assert.True(BacktestIndicatorEligibility.IsAllowed(IndicatorType.Correlation));
    }

    [Fact]
    public void ThePythonAndPlaceholderTypesThatAreNotTwoTicker_StayRefused()
    {
        foreach (IndicatorType type in BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes.Where(type => !IsTwoTicker(type)))
        {
            Assert.False(ScreenerIndicatorEligibility.IsAllowed(type), type.ToString());
            Assert.True(ScreenerIndicatorEligibility.IsTypeBlocked(type), type.ToString());
            Assert.Equal(BacktestIndicatorViolationReason.SynchronousCalculationUnsupported, ScreenerIndicatorEligibility.Check(type, null));
        }

        // The three the plan names, by name: they are not two-ticker, so the new rule never touches them.
        foreach (IndicatorType type in new[] { IndicatorType.Entropy, IndicatorType.Garch11, IndicatorType.HurstExponent })
        {
            Assert.False(IsTwoTicker(type), type.ToString());
            Assert.False(ScreenerIndicatorEligibility.IsAllowed(type), type.ToString());
        }
    }

    [Fact]
    public void ANonCausalTypeOrSeriesIsRefusedWhateverItsKind()
    {
        foreach (IndicatorType type in BacktestIndicatorEligibility.NonCausalIndicatorTypes)
        {
            Assert.Equal(BacktestIndicatorViolationReason.NonCausal, ScreenerIndicatorEligibility.Check(type, null));
            Assert.True(ScreenerIndicatorEligibility.IsTypeBlocked(type), type.ToString());
        }

        foreach ((IndicatorType type, string series) in BacktestIndicatorEligibility.NonCausalSeries)
        {
            Assert.Equal(BacktestIndicatorViolationReason.NonCausal, ScreenerIndicatorEligibility.Check(type, series));
        }
    }

    [Fact]
    public void TheTypeLevelBlockDiffersFromTheBacktestsOnlyForTheEnabledTwoTickerTypes()
    {
        IndicatorType[] enabled = EnabledByTheScreenerOnly();
        foreach (IndicatorType type in Enum.GetValues<IndicatorType>().Distinct())
        {
            bool expected = BacktestIndicatorEligibility.IsTypeBlocked(type) && !enabled.Contains(type);
            Assert.True(expected == ScreenerIndicatorEligibility.IsTypeBlocked(type), type.ToString());
        }
    }

    [Fact]
    public void TheComparisonIsNotVacuousAllowedAndRefusedAnswersBothOccur()
    {
        var answers = Cases().Select(item => ScreenerIndicatorEligibility.Check(item.Type, item.Output)).Distinct().ToArray();

        Assert.Contains(BacktestIndicatorViolationReason.None, answers);
        Assert.Contains(BacktestIndicatorViolationReason.NonCausal, answers);
        Assert.Contains(BacktestIndicatorViolationReason.SynchronousCalculationUnsupported, answers);
    }

    [Fact]
    public void TheOptionalOutputNameDefaultsToTheMainSeries()
    {
        Assert.True(ScreenerIndicatorEligibility.IsAllowed(IndicatorType.SMA));
        Assert.False(ScreenerIndicatorEligibility.IsAllowed(IndicatorType.ZigZag));
    }

    [Fact]
    public void TheAnswerIsTheSameOnEveryCall_TheTypeCheckIsCached()
    {
        // The scan asks per side per symbol; the cache of the two-ticker check must never change an answer.
        for (int round = 0; round < 3; round++)
        {
            foreach (IndicatorType type in EnabledByTheScreenerOnly())
            {
                Assert.True(ScreenerIndicatorEligibility.IsAllowed(type), type.ToString());
            }
        }
    }

    [Fact]
    public void ScreenerOnlyTypes_AreExactlyTheSevenTwoSymbolTypesTheBacktestRefuses()
    {
        IndicatorType[] expected =
        {
            IndicatorType.RollingBeta, IndicatorType.RollingAlpha, IndicatorType.RollingRSquared, IndicatorType.CointegrationSpread,
            IndicatorType.PairRatio, IndicatorType.PairSpread, IndicatorType.Ratiocator
        };

        IndicatorType[] actual = Enum.GetValues<IndicatorType>().Distinct().Where(IndicatorFactory.Default.IsRegistered).Where(ScreenerIndicatorEligibility.IsScreenerOnlyType).ToArray();

        Assert.Equal(expected.OrderBy(t => t.ToString(), StringComparer.Ordinal), actual.OrderBy(t => t.ToString(), StringComparer.Ordinal));
        // Correlation is offered by the Backtest catalog too, Entropy is refused by both: neither is Screener-only.
        Assert.False(ScreenerIndicatorEligibility.IsScreenerOnlyType(IndicatorType.Correlation));
        Assert.False(ScreenerIndicatorEligibility.IsScreenerOnlyType(IndicatorType.Entropy));
    }
}
