using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Backtest.Engine.StrictEvidence;

namespace StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence;

internal readonly record struct StrictMatchEvaluation(
    StrictFillConditions Conditions,
    StrictReducerPreview ReducerPreview,
    bool HasReducerPreview,
    bool ActivateStop,
    bool HasOrderingAmbiguity,
    ImmutableArray<StrictRunIssueCode> MissingEvidenceCodes);

internal static class StrictOrderMatcher
{
    public static StrictMatchEvaluation Evaluate(
        StrictOrderSnapshot order,
        StrictMarketEvent marketEvent,
        long remainingQuantity,
        StrictAccountState state,
        StrictBacktestConfiguration configuration,
        StrictEvidenceProviderContract providerContract)
    {
        (StrictConditionProof active, bool activationAmbiguity) = Active(order, marketEvent, providerContract);

        (StrictConditionProof reachable, bool activateStop, bool triggerAmbiguity, StrictRunIssueCode? reachIssue) =
            Reachability(order, marketEvent, providerContract);
        activateStop &= active.Verdict == StrictEvidenceVerdict.True;

        (StrictConditionProof liquidity, ImmutableArray<StrictRunIssueCode> liquidityIssues) =
            Liquidity(order, marketEvent, remainingQuantity, providerContract);

        StrictReducerPreview preview = default;
        bool hasPreview = false;
        StrictConditionProof capital;
        StrictConditionProof margin;
        if (active.Verdict == StrictEvidenceVerdict.True && reachable.Verdict == StrictEvidenceVerdict.True)
        {
            preview = StrictAccountReducer.Preview(state, order, marketEvent.Price, configuration);
            hasPreview = true;
            capital = Proof(preview.CapitalAvailable, "account-before", preview.CapitalAvailable == StrictEvidenceVerdict.True
                ? "The pre-fill account satisfies the entry capital rule or the order is reduce-only."
                : "The pre-fill account does not satisfy C >= M + fee.");
            margin = Proof(preview.MarginSatisfied, "account-preview", preview.MarginSatisfied switch
            {
                StrictEvidenceVerdict.True => "The reducer preview satisfies maintenance margin or is reduce-only.",
                StrictEvidenceVerdict.False => "The reducer preview does not satisfy maintenance margin.",
                _ => "Margin cannot be evaluated because entry capital is insufficient.",
            });
        }
        else
        {
            capital = Unknown("account-not-evaluated", "Account evidence is not evaluated for a non-reachable candidate.");
            margin = Unknown("account-not-evaluated", "Margin evidence is not evaluated for a non-reachable candidate.");
        }

        var conditions = new StrictFillConditions(active, reachable, liquidity, capital, margin);
        ImmutableArray<StrictRunIssueCode> issues = new[] { reachIssue }
            .Where(issue => issue.HasValue)
            .Select(issue => issue!.Value)
            .Concat(liquidityIssues)
            .Distinct()
            .Order()
            .ToImmutableArray();
        return new StrictMatchEvaluation(
            conditions,
            preview,
            hasPreview,
            activateStop,
            activationAmbiguity || triggerAmbiguity,
            issues);
    }

    private static (StrictConditionProof Proof, bool OrderingAmbiguity) Active(
        StrictOrderSnapshot order,
        StrictMarketEvent marketEvent,
        StrictEvidenceProviderContract providerContract)
    {
        if (marketEvent.EventTimeUtc < order.EligibleFromUtc)
        {
            return (False("order-time", "The event occurred before EligibleFromUtc."), false);
        }
        if (order.ActivationEvidence is null)
        {
            return (True("order-time", "The pending order is not before EligibleFromUtc."), false);
        }

        StrictCausalOrder causalOrder = CompareAfter(order.ActivationEvidence, marketEvent, providerContract);
        return causalOrder switch
        {
            StrictCausalOrder.After => (True(order.ActivationEvidence.EventId, "The event is proven after order activation."), false),
            StrictCausalOrder.NotAfter => (False(order.ActivationEvidence.EventId, "The event is not after order activation."), false),
            _ => (Unknown(order.ActivationEvidence.EventId, "Same-time order activation has no complete provider sequence proof."), true),
        };
    }

    private static (StrictConditionProof Proof, bool ActivateStop, bool OrderingAmbiguity, StrictRunIssueCode? Issue) Reachability(
        StrictOrderSnapshot order,
        StrictMarketEvent marketEvent,
        StrictEvidenceProviderContract providerContract)
    {
        if (order.Type == OrderType.MarketOnClose)
        {
            if (marketEvent.Kind != StrictMarketEventKind.CloseAuction)
            {
                return (False("event-kind", "The event is not close-auction evidence."), false, false, null);
            }
            if (!providerContract.SupportsCloseAuctionEvidence)
            {
                return (Unknown("provider-auction-capability", "The provider contract does not prove close-auction execution."), false, false, StrictRunIssueCode.MissingExecutionEvidence);
            }
        }
        else if (marketEvent.Kind != StrictMarketEventKind.Execution)
        {
            return (False("event-kind", "The event is not executable market evidence."), false, false, null);
        }

        if (marketEvent.ExecutableOrderSide is null)
        {
            return (Unknown("execution-side", "The event does not prove which incoming order side can execute."), false, false, StrictRunIssueCode.MissingExecutionEvidence);
        }
        if (marketEvent.ExecutableOrderSide.Value != order.Side)
        {
            return (False("execution-side", "The event is executable only for the opposite incoming order side."), false, false, null);
        }

        switch (order.Type)
        {
            case OrderType.Market:
            case OrderType.MarketOnClose:
                return (True(marketEvent.EventId, "Direction-specific executable price evidence is present."), false, false, null);

            case OrderType.Limit:
                bool limitReached = order.Side == OrderSide.Buy
                    ? marketEvent.Price <= order.LimitPrice!.Value
                    : marketEvent.Price >= order.LimitPrice!.Value;
                return (limitReached
                    ? True(marketEvent.EventId, "The executable price satisfies the limit.")
                    : False(marketEvent.EventId, "The executable price does not satisfy the limit."), false, false, null);

            case OrderType.Stop:
                if (!order.StopActivated)
                {
                    bool stopReached = StopReached(order, marketEvent.Price);
                    return (False(marketEvent.EventId, stopReached
                        ? "The stop is triggered; a later execution event is required."
                        : "The stop trigger is not reached."), stopReached, false, null);
                }
                return PostTriggerReachability(order, marketEvent, providerContract);

            case OrderType.StopLimit:
                if (!order.StopActivated)
                {
                    bool stopReached = StopReached(order, marketEvent.Price);
                    return (False(marketEvent.EventId, stopReached
                        ? "The stop-limit trigger is observed; a later event must satisfy the limit."
                        : "The stop-limit trigger is not reached."), stopReached, false, null);
                }
                var postTriggerEvaluation = PostTriggerReachability(order, marketEvent, providerContract);
                StrictConditionProof postTrigger = postTriggerEvaluation.Proof;
                if (postTrigger.Verdict != StrictEvidenceVerdict.True)
                {
                    return (postTrigger, false, postTriggerEvaluation.OrderingAmbiguity, postTriggerEvaluation.Issue);
                }
                bool stopLimitReached = order.Side == OrderSide.Buy
                    ? marketEvent.Price <= order.LimitPrice!.Value
                    : marketEvent.Price >= order.LimitPrice!.Value;
                return (stopLimitReached
                    ? True(marketEvent.EventId, "The post-trigger executable price satisfies the limit.")
                    : False(marketEvent.EventId, "The post-trigger executable price does not satisfy the limit."), false, false, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(order), order.Type, "Unknown OrderType.");
        }
    }

    private static (StrictConditionProof Proof, bool ActivateStop, bool OrderingAmbiguity, StrictRunIssueCode? Issue) PostTriggerReachability(
        StrictOrderSnapshot order,
        StrictMarketEvent marketEvent,
        StrictEvidenceProviderContract providerContract)
    {
        if (order.TriggerEvidence is null)
        {
            return (Unknown("stop-trigger", "The activated stop has no immutable trigger evidence."), false, false, StrictRunIssueCode.MissingExecutionEvidence);
        }

        StrictCausalOrder causalOrder = CompareAfter(order.TriggerEvidence, marketEvent, providerContract);
        return causalOrder switch
        {
            StrictCausalOrder.After => (True(order.TriggerEvidence.EventId, "The execution event is proven after the stop trigger."), false, false, null),
            StrictCausalOrder.NotAfter => (False(order.TriggerEvidence.EventId, "The execution event is not after the stop trigger."), false, false, null),
            _ => (Unknown(order.TriggerEvidence.EventId, "Same-time stop trigger and execution have no complete provider sequence proof."), false, true, null),
        };
    }

    private static (StrictConditionProof Proof, ImmutableArray<StrictRunIssueCode> Issues) Liquidity(
        StrictOrderSnapshot order,
        StrictMarketEvent marketEvent,
        long remainingQuantity,
        StrictEvidenceProviderContract providerContract)
    {
        var issues = ImmutableArray.CreateBuilder<StrictRunIssueCode>(2);
        if (!marketEvent.HasCompleteCoverage)
        {
            issues.Add(StrictRunIssueCode.MissingLiquidityEvidence);
        }
        if (string.IsNullOrWhiteSpace(marketEvent.LiquidityEvidenceId))
        {
            if (!issues.Contains(StrictRunIssueCode.MissingLiquidityEvidence)) issues.Add(StrictRunIssueCode.MissingLiquidityEvidence);
            return (Unknown(marketEvent.CoverageId, "Complete liquidity evidence is not attached."), issues.ToImmutable());
        }
        if (!providerContract.ResolvableEvidenceIds.Contains(marketEvent.LiquidityEvidenceId))
        {
            issues.Add(StrictRunIssueCode.UnresolvedEvidenceId);
        }
        if (issues.Count > 0)
        {
            return (Unknown(marketEvent.LiquidityEvidenceId, "The provider contract does not establish complete resolvable liquidity."), issues.ToImmutable());
        }
        if (remainingQuantity < order.Quantity)
        {
            return (False(marketEvent.LiquidityEvidenceId, "Complete coverage proves insufficient unconsumed quantity."), ImmutableArray<StrictRunIssueCode>.Empty);
        }
        return (True(marketEvent.LiquidityEvidenceId, "Complete provider coverage proves sufficient unconsumed quantity."), ImmutableArray<StrictRunIssueCode>.Empty);
    }

    private static StrictCausalOrder CompareAfter(
        StrictCausalEventRef predecessor,
        StrictMarketEvent candidate,
        StrictEvidenceProviderContract providerContract)
    {
        int timeOrder = candidate.EventTimeUtc.CompareTo(predecessor.EventTimeUtc);
        if (timeOrder > 0) return StrictCausalOrder.After;
        if (timeOrder < 0) return StrictCausalOrder.NotAfter;
        if (!providerContract.SupportsSourceSequence ||
            !predecessor.SourceSequence.HasValue ||
            !candidate.SourceSequence.HasValue)
        {
            return StrictCausalOrder.Unknown;
        }
        return candidate.SourceSequence.Value > predecessor.SourceSequence.Value
            ? StrictCausalOrder.After
            : StrictCausalOrder.NotAfter;
    }

    private static bool StopReached(StrictOrderSnapshot order, decimal price)
        => order.Side == OrderSide.Buy
            ? price >= order.StopPrice!.Value
            : price <= order.StopPrice!.Value;

    private static StrictConditionProof Proof(StrictEvidenceVerdict verdict, string evidenceId, string explanation)
        => new(verdict, evidenceId, explanation);

    private static StrictConditionProof True(string evidenceId, string explanation)
        => Proof(StrictEvidenceVerdict.True, evidenceId, explanation);

    private static StrictConditionProof False(string evidenceId, string explanation)
        => Proof(StrictEvidenceVerdict.False, evidenceId, explanation);

    private static StrictConditionProof Unknown(string evidenceId, string explanation)
        => Proof(StrictEvidenceVerdict.Unknown, evidenceId, explanation);

    private enum StrictCausalOrder { NotAfter, After, Unknown }
}
