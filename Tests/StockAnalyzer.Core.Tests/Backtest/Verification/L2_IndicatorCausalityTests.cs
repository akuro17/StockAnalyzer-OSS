using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L2 look-ahead check for EVERY registered indicator (default parameters): calculate on candles, then on the same candles with the tail
/// (index &gt;= <see cref="VerificationParameters.IndicatorPoisonStartIndex"/>) replaced by absurd-but-valid bars; every series value below the poison
/// start must be identical. CLAUDE.md: "Indicators MUST NOT reference future bars unless explicitly defined as non-causal" - the explicit definition is
/// <see cref="BacktestIndicatorEligibility"/> (the backtest refuses those outputs). The test fails on a NEW violator, on a listed non-causal
/// indicator/series that has become causal, and on a listed synchronously-uncomputable indicator that has become computable, so the definition can neither
/// grow nor rot silently.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L2_IndicatorCausalityTests
{
    private enum Outcome { Causal, NonCausal, NotEvaluable }

    private static CoreCandleData[] ToCore(ImmutableArray<CandleData> bars)
        => bars.Select((b, index) => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume + index)).ToArray();

    private static (Outcome Outcome, string Detail, ISet<string> ViolatingSeries) Classify(IndicatorFactory factory, IndicatorType type, CoreCandleData[] original, CoreCandleData[] poisoned)
    {
        var violating = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var notEvaluable = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ICoreIndicator? a = factory.Create(type);
            ICoreIndicator? b = factory.Create(type);
            if (a is null || b is null) return (Outcome.NotEvaluable, "factory returned null", violating);

            IIndicatorResult ra = a.Calculate(original);
            if (!ra.IsSuccessful) return (Outcome.NotEvaluable, $"original run unsuccessful: {ra.ErrorMessage}", violating);
            IIndicatorResult rb = b.Calculate(poisoned);
            if (!rb.IsSuccessful) return (Outcome.NotEvaluable, $"poisoned run unsuccessful: {rb.ErrorMessage}", violating);

            var detail = new StringBuilder();
            foreach (string displayName in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
            {
                string name = IndicatorOutputSeriesResolver.Normalize(type, displayName, indicatorFactory: factory);
                if (!ra.HasSeries(name)) { notEvaluable.Add(displayName); detail.Append($"'{displayName}' is not emitted; "); continue; }
                IReadOnlyList<decimal?> sa = ra.GetSeries(name);
                if (!rb.HasSeries(name)) { violating.Add(name); detail.Append($"'{name}' missing after poisoning; "); continue; }
                IReadOnlyList<decimal?> sb = rb.GetSeries(name);
                int limit = Math.Min(VerificationParameters.IndicatorPoisonStartIndex, sa.Count);
                if (sb.Count < limit) { violating.Add(name); detail.Append($"'{name}' shorter after poisoning; "); continue; }
                if (limit == 0 || !sa.Take(limit).Any(value => value.HasValue) || !sb.Take(limit).Any(value => value.HasValue))
                {
                    notEvaluable.Add(name);
                    detail.Append($"'{name}' has no non-null value in the compared prefix; ");
                    continue;
                }
                for (int i = 0; i < limit; i++)
                {
                    if (sa[i] != sb[i])
                    {
                        violating.Add(name);
                        detail.Append($"'{name}' bar {i}: {sa[i]} -> {sb[i]}; ");
                        break;
                    }
                }
            }
            if (violating.Count > 0) return (Outcome.NonCausal, detail.ToString(), violating);
            if (notEvaluable.Count > 0) return (Outcome.NotEvaluable, detail.ToString(), violating);
            return (Outcome.Causal, string.Empty, violating);
        }
        catch (Exception ex)
        {
            return (Outcome.NotEvaluable, $"{ex.GetType().Name}: {ex.Message}", violating);
        }
    }

    [Fact]
    public void EveryRegisteredIndicator_IsCausal_OrRefusedByTheBacktestEligibilityDefinition()
    {
        var factory = new IndicatorFactory();
        ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(VerificationParameters.PrimarySeed, VerificationParameters.IndicatorBarCount, VerificationParameters.FuzzStartPrice);
        CoreCandleData[] original = ToCore(bars);
        CoreCandleData[] poisoned = ToCore(SyntheticBars.Poison(bars, VerificationParameters.IndicatorPoisonStartIndex,
            VerificationParameters.IndicatorPoisonUpFactor, VerificationParameters.IndicatorPoisonDownFactor));

        var problems = new List<string>();
        int causal = 0;
        foreach (IndicatorType type in factory.GetRegisteredTypes().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            (Outcome outcome, string detail, ISet<string> violating) = Classify(factory, type, original, poisoned);
            bool typeBlockedAsNonCausal = BacktestIndicatorEligibility.NonCausalIndicatorTypes.Contains(type);
            bool typeBlockedAsSyncUnsupported = BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes.Contains(type);
            var blockedSeries = BacktestIndicatorEligibility.NonCausalSeries
                .Where(x => x.Type == type).Select(x => x.OutputName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            switch (outcome)
            {
                case Outcome.Causal:
                    causal++;
                    if (typeBlockedAsNonCausal) problems.Add($"{type}: listed as NON-CAUSAL but is now causal - remove it from BacktestIndicatorEligibility");
                    if (blockedSeries.Count > 0) problems.Add($"{type}: series {string.Join(",", blockedSeries)} listed as non-causal but the indicator is now causal");
                    if (typeBlockedAsSyncUnsupported) problems.Add($"{type}: listed as synchronously unsupported but now computes");
                    break;
                case Outcome.NonCausal:
                    if (typeBlockedAsNonCausal) break;                                   // whole type refused - every series covered
                    if (blockedSeries.Count > 0 && violating.SetEquals(blockedSeries)) break; // exactly the refused series violate
                    problems.Add($"{type}: NON-CAUSAL series not covered by BacktestIndicatorEligibility: {string.Join(",", violating.Except(blockedSeries, StringComparer.OrdinalIgnoreCase))} ({detail})");
                    break;
                default:
                    if (!typeBlockedAsSyncUnsupported)
                        problems.Add($"{type}: NOT EVALUABLE and not refused by the production eligibility definition ({detail})");
                    break;
            }
        }

        Assert.True(causal > 0, "no indicator could be evaluated - the harness itself is broken");
        Assert.True(problems.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Harness_DetectsARealPastChange_WhenThePoisonStartsInsideTheComparedRange()
    {
        // Positive control with a real indicator (no stub): SMA over the poisoned range MUST differ when the compared range covers poisoned bars.
        var factory = new IndicatorFactory();
        ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(VerificationParameters.PrimarySeed, VerificationParameters.IndicatorBarCount, VerificationParameters.FuzzStartPrice);
        CoreCandleData[] original = ToCore(bars);
        CoreCandleData[] early = ToCore(SyntheticBars.Poison(bars, VerificationParameters.IndicatorPoisonStartIndex - 50,
            VerificationParameters.IndicatorPoisonUpFactor, VerificationParameters.IndicatorPoisonDownFactor));

        (Outcome outcome, _, _) = Classify(factory, IndicatorType.SMA, original, early);

        Assert.Equal(Outcome.NonCausal, outcome);
    }
}
