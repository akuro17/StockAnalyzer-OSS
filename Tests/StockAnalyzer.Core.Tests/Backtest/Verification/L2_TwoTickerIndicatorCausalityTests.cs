using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L2 look-ahead check for EVERY registered indicator that compares two symbols (<see cref="ICrossTickerIndicator"/>, enumerated from the factory, no fixed list),
/// with a comparison symbol set: calculate on (A, B), then on (A with a poisoned tail, B) and on (A, B with a poisoned tail); every series value below the poison
/// start must be identical in both comparisons. The ordinary <see cref="L2_IndicatorCausalityTests"/> calculates with default parameters, which have no comparison
/// symbol, so a two-ticker indicator cannot be evaluated there: that is the only reason these indicators were listed in
/// <c>BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes</c>. This test is the evidence the Screener rule (<c>ScreenerIndicatorEligibility</c>)
/// rests on: a type that is not causal in BOTH symbols must not be enabled in the Screener.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L2_TwoTickerIndicatorCausalityTests
{
    private const string ComparisonSymbolName = "BENCH";

    private enum Outcome { Causal, NonCausal, NotEvaluable }

    private static CoreCandleData[] ToCore(ImmutableArray<CandleData> bars)
        => bars.Select((b, index) => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume + index)).ToArray();

    private static CoreCandleData?[] ToCoreNullable(ImmutableArray<CandleData> bars)
        => ToCore(bars).Select(c => (CoreCandleData?)c).ToArray();

    private static IEnumerable<IndicatorType> TwoTickerTypes(IndicatorFactory factory)
        => factory.GetRegisteredTypes()
            .Where(type => factory.Create(type) is ICrossTickerIndicator)
            .OrderBy(type => type.ToString(), StringComparer.Ordinal);

    /// <summary>One calculation: default parameters with a non-empty comparison symbol, the given comparison bars.</summary>
    private static IIndicatorResult Calculate(IndicatorFactory factory, IndicatorType type, CoreCandleData[] primary, CoreCandleData?[] secondary)
    {
        ICoreIndicator indicator = factory.Create(type) ?? throw new InvalidOperationException($"{type}: the factory returned null.");
        CoreIndicatorParameterBase parameters = indicator.GetDefaultSettings().ParameterObject
            ?? throw new InvalidOperationException($"{type}: no default parameter object.");
        ((ICrossTickerParameter)parameters).ComparisonSymbol = ComparisonSymbolName;
        indicator.Configure(parameters);
        ((ICrossTickerIndicator)indicator).SetSecondaryCandles(secondary);
        return indicator.Calculate(primary);
    }

    private static (Outcome Outcome, string Detail) Classify(
        IndicatorFactory factory,
        IndicatorType type,
        CoreCandleData[] primary,
        CoreCandleData?[] secondary,
        CoreCandleData[] poisonedPrimary,
        CoreCandleData?[] poisonedSecondary,
        int poisonStartIndex = VerificationParameters.IndicatorPoisonStartIndex)
    {
        try
        {
            IIndicatorResult baseline = Calculate(factory, type, primary, secondary);
            if (!baseline.IsSuccessful) return (Outcome.NotEvaluable, $"baseline run unsuccessful: {baseline.ErrorMessage}");
            IIndicatorResult primaryPoisoned = Calculate(factory, type, poisonedPrimary, secondary);
            if (!primaryPoisoned.IsSuccessful) return (Outcome.NotEvaluable, $"primary-poisoned run unsuccessful: {primaryPoisoned.ErrorMessage}");
            IIndicatorResult secondaryPoisoned = Calculate(factory, type, primary, poisonedSecondary);
            if (!secondaryPoisoned.IsSuccessful) return (Outcome.NotEvaluable, $"secondary-poisoned run unsuccessful: {secondaryPoisoned.ErrorMessage}");

            var violating = new StringBuilder();
            var notEvaluable = new StringBuilder();
            foreach (string displayName in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
            {
                string name = IndicatorOutputSeriesResolver.Normalize(type, displayName, indicatorFactory: factory);
                if (!baseline.HasSeries(name)) { notEvaluable.Append($"'{displayName}' is not emitted; "); continue; }
                IReadOnlyList<decimal?> reference = baseline.GetSeries(name);
                int limit = Math.Min(poisonStartIndex, reference.Count);
                if (limit == 0 || !reference.Take(limit).Any(value => value.HasValue))
                {
                    notEvaluable.Append($"'{name}' has no non-null value in the compared prefix; ");
                    continue;
                }

                foreach ((string label, IIndicatorResult variant) in new[] { ("primary poisoned", primaryPoisoned), ("secondary poisoned", secondaryPoisoned) })
                {
                    if (!variant.HasSeries(name)) { violating.Append($"'{name}' missing when the {label}; "); continue; }
                    IReadOnlyList<decimal?> series = variant.GetSeries(name);
                    if (series.Count < limit) { violating.Append($"'{name}' shorter when the {label}; "); continue; }
                    for (int i = 0; i < limit; i++)
                    {
                        if (reference[i] != series[i])
                        {
                            violating.Append($"'{name}' bar {i} when the {label}: {reference[i]} -> {series[i]}; ");
                            break;
                        }
                    }
                }
            }

            if (violating.Length > 0) return (Outcome.NonCausal, violating.ToString());
            if (notEvaluable.Length > 0) return (Outcome.NotEvaluable, notEvaluable.ToString());
            return (Outcome.Causal, string.Empty);
        }
        catch (Exception ex)
        {
            return (Outcome.NotEvaluable, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (CoreCandleData[] Primary, CoreCandleData?[] Secondary, ImmutableArray<CandleData> PrimaryBars, ImmutableArray<CandleData> SecondaryBars) Series()
    {
        ImmutableArray<CandleData> a = SyntheticBars.SeededRandomWalk(VerificationParameters.PrimarySeed, VerificationParameters.IndicatorBarCount, VerificationParameters.FuzzStartPrice);
        ImmutableArray<CandleData> b = SyntheticBars.SeededRandomWalk(VerificationParameters.SecondarySeed, VerificationParameters.IndicatorBarCount, VerificationParameters.FuzzStartPrice);
        return (ToCore(a), ToCoreNullable(b), a, b);
    }

    private static CoreCandleData[] PoisonedPrimary(ImmutableArray<CandleData> bars, int start)
        => ToCore(SyntheticBars.Poison(bars, start, VerificationParameters.IndicatorPoisonUpFactor, VerificationParameters.IndicatorPoisonDownFactor));

    private static CoreCandleData?[] PoisonedSecondary(ImmutableArray<CandleData> bars, int start)
        => ToCoreNullable(SyntheticBars.Poison(bars, start, VerificationParameters.IndicatorPoisonUpFactor, VerificationParameters.IndicatorPoisonDownFactor));

    [Fact]
    public void TheCalendarsOfTheTwoSyntheticSeries_AreTheSame_AndTheSeriesDiffer()
    {
        var series = Series();

        Assert.Equal(series.PrimaryBars.Select(b => b.Timestamp), series.SecondaryBars.Select(b => b.Timestamp));
        Assert.NotEqual(series.PrimaryBars.Select(b => b.Close), series.SecondaryBars.Select(b => b.Close));
    }

    [Fact]
    public void EveryRegisteredTwoTickerIndicator_IsCausalInBothSymbols()
    {
        var factory = new IndicatorFactory();
        var series = Series();
        CoreCandleData[] poisonedPrimary = PoisonedPrimary(series.PrimaryBars, VerificationParameters.IndicatorPoisonStartIndex);
        CoreCandleData?[] poisonedSecondary = PoisonedSecondary(series.SecondaryBars, VerificationParameters.IndicatorPoisonStartIndex);

        IndicatorType[] types = TwoTickerTypes(factory).ToArray();
        var problems = new List<string>();
        foreach (IndicatorType type in types)
        {
            (Outcome outcome, string detail) = Classify(factory, type, series.Primary, series.Secondary, poisonedPrimary, poisonedSecondary);
            if (outcome != Outcome.Causal)
            {
                problems.Add($"{type}: {outcome} ({detail})");
            }
        }

        // The two types the Screener already evaluates are among them (the factory is the source of the list, not this test).
        Assert.Contains(IndicatorType.Correlation, types);
        Assert.Contains(IndicatorType.CrossCorrelation, types);
        Assert.True(types.Length >= 9, $"expected at least the nine two-ticker indicators, found {types.Length}");
        Assert.True(problems.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Harness_DetectsARealPastChange_WhenTheSecondarySeriesIsPoisonedInsideTheComparedRange()
    {
        var factory = new IndicatorFactory();
        var series = Series();
        int earlyStart = VerificationParameters.IndicatorPoisonStartIndex - 50;

        (Outcome outcome, string detail) = Classify(
            factory, IndicatorType.Correlation, series.Primary, series.Secondary,
            series.Primary, PoisonedSecondary(series.SecondaryBars, earlyStart));

        Assert.Equal(Outcome.NonCausal, outcome);
        Assert.Contains("secondary poisoned", detail);
        Assert.DoesNotContain("primary poisoned", detail);
    }

    [Fact]
    public void Harness_DetectsARealPastChange_WhenThePrimarySeriesIsPoisonedInsideTheComparedRange()
    {
        var factory = new IndicatorFactory();
        var series = Series();
        int earlyStart = VerificationParameters.IndicatorPoisonStartIndex - 50;

        (Outcome outcome, string detail) = Classify(
            factory, IndicatorType.Correlation, series.Primary, series.Secondary,
            PoisonedPrimary(series.PrimaryBars, earlyStart), series.Secondary);

        Assert.Equal(Outcome.NonCausal, outcome);
        Assert.Contains("primary poisoned", detail);
        Assert.DoesNotContain("secondary poisoned", detail);
    }

    [Fact]
    public void Harness_SeesTheComparisonSymbolAtAll_TheValueDependsOnTheSecondarySeries()
    {
        // Guards against a vacuous pass: with the comparison bars of another walk the Correlation prefix is different, so SetSecondaryCandles really reaches the indicator.
        var factory = new IndicatorFactory();
        var series = Series();
        CoreCandleData?[] other = ToCoreNullable(SyntheticBars.SeededRandomWalk(VerificationParameters.SecondarySeed + 1, VerificationParameters.IndicatorBarCount, VerificationParameters.FuzzStartPrice));

        IReadOnlyList<decimal?> withB = Calculate(factory, IndicatorType.Correlation, series.Primary, series.Secondary).GetSeries(IndicatorResult.MainSeriesName);
        IReadOnlyList<decimal?> withOther = Calculate(factory, IndicatorType.Correlation, series.Primary, other).GetSeries(IndicatorResult.MainSeriesName);

        Assert.NotEqual(withB.Take(VerificationParameters.IndicatorPoisonStartIndex), withOther.Take(VerificationParameters.IndicatorPoisonStartIndex));
    }
}
