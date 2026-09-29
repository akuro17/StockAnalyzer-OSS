using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Backtest.Engine.StrictEvidence;

namespace StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence;

/// <summary>
/// Stateless evidence-driven engine. Every run variable is method-local so one DI singleton can execute
/// independent runs concurrently without sharing account, order, evidence, or cancellation state.
/// </summary>
public sealed class StrictBacktestEngine : IStrictBacktestEngine
{
    private const int FillAuditSchemaVersion = 2;

    public StrictBacktestResult Run(
        StrictBacktestInput input,
        StrictBacktestConfiguration configuration,
        IStrictBacktestStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(strategy);
        string strategyName = strategy.Name;
        if (string.IsNullOrWhiteSpace(strategyName)) throw new ArgumentException("Strategy Name must not be blank.", nameof(strategy));

        var phases = new List<StrictRunPhase> { StrictRunPhase.Created, StrictRunPhase.Validating };
        StrictAccountState state = StrictAccountState.Initial(configuration.InitialCapital);
        var orders = new List<RuntimeOrder>();
        var fills = new List<StrictFill>();

        if (!input.ValidationIssues.IsDefaultOrEmpty)
        {
            return Terminal(StrictRunStatus.Failed, state, orders, fills, phases, strategyName,
                input.ValidationIssues.ToArray());
        }
        try
        {
            configuration.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return Terminal(StrictRunStatus.Failed, state, orders, fills, phases, strategyName,
                Issue(StrictRunIssueCode.InvalidInput, ex.Message));
        }

        var unsupportedIssues = new List<StrictRunIssue>(4);
        if (input.RequiresCorporateActions)
        {
            unsupportedIssues.Add(Issue(StrictRunIssueCode.UnsupportedCorporateAction, "StrictEvidence v1 does not apply corporate-action accounting."));
        }
        if (input.RequiresExternalCashTransfers)
        {
            unsupportedIssues.Add(Issue(StrictRunIssueCode.UnsupportedCashTransfer, "StrictEvidence v1 does not apply external cash transfers."));
        }
        if (input.RequiresBorrowing)
        {
            unsupportedIssues.Add(Issue(StrictRunIssueCode.UnsupportedBorrowing, "StrictEvidence v1 does not infer borrow or locate availability."));
        }
        if (input.Events.Any(item => item.RevisionOf is not null) && !input.ProviderContract.SupportsPointInTimeRevisions)
        {
            unsupportedIssues.Add(Issue(StrictRunIssueCode.UnsupportedPointInTimeRevision, "The provider contract does not support the supplied point-in-time revisions."));
        }
        if (unsupportedIssues.Count > 0)
        {
            return Terminal(StrictRunStatus.Unsupported, state, orders, fills, phases, strategyName,
                unsupportedIssues.ToArray());
        }
        if (input.Events.IsEmpty)
        {
            return Terminal(StrictRunStatus.EvidenceMissing, state, orders, fills, phases, strategyName,
                Issue(StrictRunIssueCode.NoMarketData, "StrictEvidence requires at least one explicit market event."));
        }

        phases.Add(StrictRunPhase.Running);
        var visibleById = new Dictionary<string, StrictMarketEvent>(StringComparer.Ordinal);
        var allById = input.Events.ToDictionary(item => item.EventId, StringComparer.Ordinal);
        var remainingByEvidenceId = new Dictionary<string, long>(StringComparer.Ordinal);
        var clientOrderIds = new HashSet<string>(StringComparer.Ordinal);
        var processedEventIds = new HashSet<string>(StringComparer.Ordinal);
        var evaluationBuffer = new List<(RuntimeOrder Order, StrictMatchEvaluation Match)>();
        var potentialEventBuffer = new List<StrictMarketEvent>();
        Dictionary<DateTime, StrictMarketEvent[]> eventsByEventTime = input.Events
            .GroupBy(item => item.EventTimeUtc)
            .ToDictionary(group => group.Key, group => group.ToArray());
        long nextOrderId = 1L;
        long nextFillId = 1L;
        long nextOcoGroupId = 1L;

        ImmutableArray<StrictMarketEvent> orderedEvents = input.Events
            .OrderBy(EffectiveAvailability)
            .ThenBy(item => item.EventTimeUtc)
            .ThenBy(item => item.SourceSequence ?? long.MaxValue)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .ToImmutableArray();

        try
        {
            int groupStart = 0;
            while (groupStart < orderedEvents.Length)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Terminal(StrictRunStatus.Cancelled, state, orders, fills, phases, strategyName);
                }

                DateTime simulationTime = EffectiveAvailability(orderedEvents[groupStart]);
                int groupEnd = groupStart + 1;
                while (groupEnd < orderedEvents.Length &&
                       EffectiveAvailability(orderedEvents[groupEnd]) == simulationTime)
                {
                    groupEnd++;
                }

                for (int eventIndex = groupStart; eventIndex < groupEnd; eventIndex++)
                {
                    StrictMarketEvent marketEvent = orderedEvents[eventIndex];
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Terminal(StrictRunStatus.Cancelled, state, orders, fills, phases, strategyName);
                    }

                    StrictBacktestResult? ambiguity = DetectCurrentBoundaryAmbiguity(
                        eventsByEventTime[marketEvent.EventTimeUtc], processedEventIds,
                        state, configuration, input.ProviderContract, remainingByEvidenceId,
                        orders, fills, phases, strategyName, evaluationBuffer, potentialEventBuffer);
                    if (ambiguity is not null) return ambiguity;

                    EvaluatePendingOrders(
                        orders, marketEvent, state, configuration, input.ProviderContract,
                        remainingByEvidenceId, evaluationBuffer);
                    RuntimeOrder? candidateOrder = null;
                    StrictMatchEvaluation candidateMatch = default;
                    int candidateCount = 0;
                    foreach ((RuntimeOrder order, StrictMatchEvaluation match) in evaluationBuffer)
                    {
                        if (match.Conditions.HasFalse) continue;
                        candidateCount++;
                        if (candidateCount == 1)
                        {
                            candidateOrder = order;
                            candidateMatch = match;
                        }
                    }

                    if (candidateCount > 1)
                    {
                        return Terminal(StrictRunStatus.Ambiguous, state, orders, fills, phases, strategyName,
                            Issue(StrictRunIssueCode.AmbiguousExecutionOrder,
                                "More than one active order can consume the same execution evidence.", marketEvent.EventId));
                    }

                    if (candidateCount == 1)
                    {
                        RuntimeOrder runtimeOrder = candidateOrder!;
                        StrictMatchEvaluation match = candidateMatch;
                        if (match.HasOrderingAmbiguity)
                        {
                            return Terminal(StrictRunStatus.Ambiguous, state, orders, fills, phases, strategyName,
                                Issue(StrictRunIssueCode.AmbiguousExecutionOrder,
                                    "The candidate event has no proven order after activation or trigger evidence.",
                                    runtimeOrder.ClientOrderId, marketEvent.EventId));
                        }
                        if (match.Conditions.HasUnknown)
                        {
                            ImmutableArray<StrictRunIssueCode> issueCodes = match.MissingEvidenceCodes.IsDefaultOrEmpty
                                ? ImmutableArray.Create(StrictRunIssueCode.MissingExecutionEvidence)
                                : match.MissingEvidenceCodes;
                            return Terminal(StrictRunStatus.EvidenceMissing, state, orders, fills, phases, strategyName,
                                issueCodes.Select(issueCode => Issue(
                                    issueCode,
                                    "A required fill condition is Unknown.",
                                    runtimeOrder.ClientOrderId,
                                    marketEvent.EventId)).ToArray());
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        CommitCandidate(
                            runtimeOrder,
                            marketEvent,
                            EffectiveAvailability(marketEvent),
                            match,
                            input.ProviderContract,
                            allById,
                            remainingByEvidenceId,
                            orders,
                            fills,
                            ref state,
                            ref nextOrderId,
                            ref nextFillId,
                            ref nextOcoGroupId);

                        if (state.IsFlat && state.Cash <= 0m)
                        {
                            return Terminal(StrictRunStatus.Insolvent, state, orders, fills, phases, strategyName);
                        }
                    }
                    else
                    {
                        foreach ((RuntimeOrder runtimeOrder, StrictMatchEvaluation match) in evaluationBuffer)
                        {
                            if (match.ActivateStop && runtimeOrder.Status == StrictOrderStatus.Pending)
                            {
                                runtimeOrder.TriggerEvidence = CausalRef(marketEvent);
                            }
                        }
                    }

                    if (!state.IsFlat && !orders.Any(order => order.Status == StrictOrderStatus.Pending && order.Intent == StrictOrderIntent.LiquidatePosition))
                    {
                        decimal equity = StrictAccountReducer.Equity(state, marketEvent.Price);
                        decimal maintenance = StrictAccountReducer.MaintenanceRequirement(state, marketEvent.Price, configuration);
                        if (equity <= maintenance)
                        {
                            RuntimeOrder liquidation = CreateLiquidationOrder(
                                nextOrderId++, marketEvent, EffectiveAvailability(marketEvent), state);
                            orders.Add(liquidation);
                            clientOrderIds.Add(liquidation.ClientOrderId);
                        }
                    }

                    processedEventIds.Add(marketEvent.EventId);
                }

                for (int eventIndex = groupStart; eventIndex < groupEnd; eventIndex++)
                {
                    StrictMarketEvent marketEvent = orderedEvents[eventIndex];
                    visibleById[marketEvent.EventId] = marketEvent;
                }

                bool liquidationPending = orders.Any(order => order.Status == StrictOrderStatus.Pending && order.Intent == StrictOrderIntent.LiquidatePosition);
                if (simulationTime >= input.TradingStartUtc && !liquidationPending)
                {
                    var context = new StrictStrategyContext(
                        simulationTime,
                        new StrictMarketEventView(orderedEvents, groupEnd),
                        state);
                    StrictOrderRequest? request;
                    try
                    {
                        request = strategy.Evaluate(context);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                    {
                        return Terminal(StrictRunStatus.Failed, state, orders, fills, phases, strategyName,
                            Issue(StrictRunIssueCode.StrategyFailure, ex.Message));
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Terminal(StrictRunStatus.Cancelled, state, orders, fills, phases, strategyName);
                    }
                    if (request is not null)
                    {
                        OrderCreationResult creation = CreateStrategyOrder(
                            request, simulationTime, state, visibleById, clientOrderIds, orders, nextOrderId);
                        if (creation.Status is { } terminalStatus)
                        {
                            return Terminal(terminalStatus, state, orders, fills, phases, strategyName, creation.Issue!);
                        }
                        orders.Add(creation.Order!);
                        clientOrderIds.Add(creation.Order!.ClientOrderId);
                        nextOrderId++;
                    }
                }

                groupStart = groupEnd;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Terminal(StrictRunStatus.Cancelled, state, orders, fills, phases, strategyName);
        }
        catch (OverflowException ex)
        {
            return Terminal(StrictRunStatus.Failed, state, orders, fills, phases, strategyName,
                Issue(StrictRunIssueCode.NumericFailure, ex.Message));
        }

        RuntimeOrder? unfilledLiquidation = orders.FirstOrDefault(order =>
            order.Status == StrictOrderStatus.Pending && order.Intent == StrictOrderIntent.LiquidatePosition);
        if (unfilledLiquidation is not null)
        {
            return Terminal(StrictRunStatus.EvidenceMissing, state, orders, fills, phases, strategyName,
                Issue(StrictRunIssueCode.MissingExecutionEvidence,
                    "A liquidation requirement exists, but no later evidence-backed fill was available.", unfilledLiquidation.ClientOrderId));
        }

        foreach (RuntimeOrder order in orders.Where(item => item.Status == StrictOrderStatus.Pending))
        {
            order.Status = StrictOrderStatus.Expired;
        }
        return Terminal(StrictRunStatus.Completed, state, orders, fills, phases, strategyName);
    }

    private static StrictBacktestResult? DetectCurrentBoundaryAmbiguity(
        IReadOnlyList<StrictMarketEvent> marketEvents,
        IReadOnlySet<string> processedEventIds,
        StrictAccountState state,
        StrictBacktestConfiguration configuration,
        StrictEvidenceProviderContract providerContract,
        Dictionary<string, long> remainingByEvidenceId,
        List<RuntimeOrder> orders,
        List<StrictFill> fills,
        List<StrictRunPhase> phases,
        string strategyName,
        List<(RuntimeOrder Order, StrictMatchEvaluation Match)> evaluationBuffer,
        List<StrictMarketEvent> potentialEventBuffer)
    {
        int unprocessedCount = 0;
        bool allUnprocessedEventsHaveSequence = true;
        foreach (StrictMarketEvent marketEvent in marketEvents)
        {
            if (processedEventIds.Contains(marketEvent.EventId)) continue;
            unprocessedCount++;
            allUnprocessedEventsHaveSequence &= marketEvent.SourceSequence.HasValue;
        }
        if (unprocessedCount <= 1 ||
            (providerContract.SupportsSourceSequence && allUnprocessedEventsHaveSequence))
        {
            return null;
        }

        potentialEventBuffer.Clear();
        foreach (StrictMarketEvent marketEvent in marketEvents)
        {
            if (processedEventIds.Contains(marketEvent.EventId)) continue;
            EvaluatePendingOrders(
                orders, marketEvent, state, configuration, providerContract,
                remainingByEvidenceId, evaluationBuffer);
            int potentialCount = 0;
            foreach ((RuntimeOrder _, StrictMatchEvaluation match) in evaluationBuffer)
            {
                if (match.ActivateStop || !match.Conditions.HasFalse) potentialCount++;
            }
            if (potentialCount > 1)
            {
                return Terminal(StrictRunStatus.Ambiguous, state, orders, fills, phases, strategyName,
                    Issue(StrictRunIssueCode.AmbiguousExecutionOrder,
                        "Multiple orders can consume one evidence event without an order-level precedence proof.", marketEvent.EventId));
            }
            if (potentialCount == 1) potentialEventBuffer.Add(marketEvent);
        }

        if (potentialEventBuffer.Count > 1)
        {
            return Terminal(StrictRunStatus.Ambiguous, state, orders, fills, phases, strategyName,
                Issue(StrictRunIssueCode.AmbiguousExecutionOrder,
                    "Multiple executable events share a timestamp without a complete provider sequence.",
                    potentialEventBuffer.Select(item => item.EventId).ToArray()));
        }
        return null;
    }

    private static void EvaluatePendingOrders(
        List<RuntimeOrder> orders,
        StrictMarketEvent marketEvent,
        StrictAccountState state,
        StrictBacktestConfiguration configuration,
        StrictEvidenceProviderContract providerContract,
        Dictionary<string, long> remainingByEvidenceId,
        List<(RuntimeOrder Order, StrictMatchEvaluation Match)> evaluationBuffer)
    {
        evaluationBuffer.Clear();
        foreach (RuntimeOrder order in orders)
        {
            if (order.Status != StrictOrderStatus.Pending) continue;
            long remaining = RemainingQuantity(marketEvent, remainingByEvidenceId);
            StrictMatchEvaluation match = StrictOrderMatcher.Evaluate(
                order.Snapshot(), marketEvent, remaining, state, configuration, providerContract);
            evaluationBuffer.Add((order, match));
        }
    }

    private static void CommitCandidate(
        RuntimeOrder order,
        StrictMarketEvent marketEvent,
        DateTime evidenceAvailableTime,
        StrictMatchEvaluation match,
        StrictEvidenceProviderContract providerContract,
        IReadOnlyDictionary<string, StrictMarketEvent> allById,
        Dictionary<string, long> remainingByEvidenceId,
        List<RuntimeOrder> orders,
        List<StrictFill> fills,
        ref StrictAccountState state,
        ref long nextOrderId,
        ref long nextFillId,
        ref long nextOcoGroupId)
    {
        if (!match.Conditions.IsFillable || !match.HasReducerPreview)
        {
            throw new InvalidOperationException("Only an all-True candidate can be committed.");
        }

        checked
        {
            long remainingBefore = RemainingQuantity(marketEvent, remainingByEvidenceId);
            long remainingAfter = remainingBefore - order.Quantity;
            long fillId = nextFillId;
            StrictOrderSnapshot orderBefore = order.Snapshot();
            StrictCausalEventRef executionEvidence = CausalRef(marketEvent);
            ImmutableArray<StrictCausalEventRef> causalEvidence = order.DecisionDataEventIds
                .Select(id => CausalRef(allById[id]))
                .Concat(order.ActivationEvidence is null ? [] : [order.ActivationEvidence])
                .Concat(order.TriggerEvidence is null ? [] : [order.TriggerEvidence])
                .Append(executionEvidence)
                .DistinctBy(item => item.EventId, StringComparer.Ordinal)
                .OrderBy(item => item.EventTimeUtc)
                .ThenBy(item => item.SourceSequence ?? long.MaxValue)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToImmutableArray();
            ImmutableArray<StrictSourceVersionRef> observedVersions = causalEvidence
                .Select(item => new StrictSourceVersionRef(item.SourceId, item.DataVersion))
                .Distinct()
                .OrderBy(item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(item => item.DataVersion, StringComparer.Ordinal)
                .ToImmutableArray();

            string liquidityEvidenceId = marketEvent.LiquidityEvidenceId!;
            var audit = new StrictFillAudit(
                FillAuditSchemaVersion,
                providerContract.ContractVersion,
                observedVersions,
                order.DecisionDataEventIds,
                causalEvidence,
                order.InformationTimeUtc,
                order.DecisionTimeUtc,
                order.CreationTimeUtc,
                order.EligibleFromUtc,
                state.Cash,
                state.HeldMargin,
                state.PositionQuantity,
                state.EntryPrice,
                orderBefore,
                match.Conditions,
                marketEvent.EventId,
                liquidityEvidenceId,
                order.Quantity,
                remainingAfter);
            var fill = new StrictFill(
                fillId,
                order.OrderId,
                marketEvent.EventTimeUtc,
                marketEvent.SourceSequence,
                order.Side,
                marketEvent.Price,
                order.Quantity,
                match.ReducerPreview.Commission,
                audit,
                match.ReducerPreview.StateAfter);

            long proposedNextOrderId = nextOrderId;
            long proposedNextOcoGroupId = nextOcoGroupId;
            List<RuntimeOrder> protectiveChildren = BuildProtectiveChildren(
                order, fill, evidenceAvailableTime, executionEvidence,
                ref proposedNextOrderId, ref proposedNextOcoGroupId);

            // Atomic commit boundary: all calculations and immutable snapshots above succeeded.
            state = match.ReducerPreview.StateAfter;
            order.Status = StrictOrderStatus.Filled;
            fills.Add(fill);
            remainingByEvidenceId[liquidityEvidenceId] = remainingAfter;
            orders.AddRange(protectiveChildren);
            nextOrderId = proposedNextOrderId;
            nextOcoGroupId = proposedNextOcoGroupId;
            nextFillId++;

            if (order.OcoGroupId is { } filledGroup)
            {
                foreach (RuntimeOrder sibling in orders.Where(item =>
                    item.Status == StrictOrderStatus.Pending && item.OcoGroupId == filledGroup && item.OrderId != order.OrderId))
                {
                    sibling.Status = StrictOrderStatus.Cancelled;
                }
            }
            if (state.IsFlat)
            {
                foreach (RuntimeOrder competingExit in orders.Where(item =>
                    item.Status == StrictOrderStatus.Pending &&
                    item.Intent is StrictOrderIntent.ExitPosition or StrictOrderIntent.LiquidatePosition))
                {
                    competingExit.Status = StrictOrderStatus.Cancelled;
                }
            }
        }
    }

    private static List<RuntimeOrder> BuildProtectiveChildren(
        RuntimeOrder parent,
        StrictFill fill,
        DateTime evidenceAvailableTime,
        StrictCausalEventRef activationEvidence,
        ref long nextOrderId,
        ref long nextOcoGroupId)
    {
        var children = new List<RuntimeOrder>(2);
        if (parent.Intent is not (StrictOrderIntent.EnterLong or StrictOrderIntent.EnterShort)) return children;
        if (parent.StopLossRatio is null && parent.TakeProfitRatio is null) return children;

        long? ocoGroupId = parent.StopLossRatio.HasValue && parent.TakeProfitRatio.HasValue ? nextOcoGroupId++ : null;
        DateTime activationTime = evidenceAvailableTime > fill.FillTimeUtc ? evidenceAvailableTime : fill.FillTimeUtc;
        bool isLong = fill.StateAfter.PositionQuantity > 0L;
        OrderSide side = isLong ? OrderSide.Sell : OrderSide.Buy;
        ImmutableArray<string> decisionRefs = ImmutableArray.Create(fill.WhyWasThisFillPossible.ExecutionEventId);

        checked
        {
            if (parent.StopLossRatio is { } stopLoss)
            {
                decimal stopPrice = isLong
                    ? fill.Price * (1m - stopLoss)
                    : fill.Price * (1m + stopLoss);
                if (stopPrice <= 0m) throw new OverflowException("The protective stop price is not positive.");
                children.Add(new RuntimeOrder(
                    nextOrderId++, $"{parent.ClientOrderId}:SL:{fill.FillId}", StrictOrderIntent.ExitPosition,
                    side, OrderType.Stop, fill.Quantity, null, stopPrice, activationTime, activationTime,
                    activationTime, activationTime, decisionRefs, activationEvidence, null,
                    null, null, ocoGroupId, fill.FillId));
            }
            if (parent.TakeProfitRatio is { } takeProfit)
            {
                decimal limitPrice = isLong
                    ? fill.Price * (1m + takeProfit)
                    : fill.Price * (1m - takeProfit);
                if (limitPrice <= 0m) throw new OverflowException("The protective take-profit price is not positive.");
                children.Add(new RuntimeOrder(
                    nextOrderId++, $"{parent.ClientOrderId}:TP:{fill.FillId}", StrictOrderIntent.ExitPosition,
                    side, OrderType.Limit, fill.Quantity, limitPrice, null, activationTime, activationTime,
                    activationTime, activationTime, decisionRefs, activationEvidence, null,
                    null, null, ocoGroupId, fill.FillId));
            }
        }
        return children;
    }

    private static RuntimeOrder CreateLiquidationOrder(
        long orderId,
        StrictMarketEvent marketEvent,
        DateTime decisionTime,
        StrictAccountState state)
    {
        OrderSide side = state.PositionQuantity > 0L ? OrderSide.Sell : OrderSide.Buy;
        return new RuntimeOrder(
            orderId,
            $"liquidation:{orderId}",
            StrictOrderIntent.LiquidatePosition,
            side,
            OrderType.Market,
            StrictAccountReducer.AbsPosition(state.PositionQuantity),
            null,
            null,
            decisionTime,
            decisionTime,
            decisionTime,
            decisionTime,
            ImmutableArray.Create(marketEvent.EventId),
            CausalRef(marketEvent),
            null,
            null,
            null,
            null,
            null);
    }

    private static OrderCreationResult CreateStrategyOrder(
        StrictOrderRequest request,
        DateTime simulationTime,
        StrictAccountState state,
        IReadOnlyDictionary<string, StrictMarketEvent> visibleById,
        HashSet<string> clientOrderIds,
        List<RuntimeOrder> existingOrders,
        long orderId)
    {
        if (string.IsNullOrWhiteSpace(request.ClientOrderId) || !clientOrderIds.Add(request.ClientOrderId))
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.InvalidOrder, "ClientOrderId must be non-blank and unique.", request.ClientOrderId ?? string.Empty));
        }
        clientOrderIds.Remove(request.ClientOrderId); // Added only after the complete order passes validation.

        if (!Enum.IsDefined(request.Intent) || request.Intent == StrictOrderIntent.LiquidatePosition || !Enum.IsDefined(request.Type))
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.InvalidOrder, "The strategy supplied an unsupported order intent or type.", request.ClientOrderId));
        }
        if (request.Quantity <= 0L)
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.InvalidOrder, "Order quantity must be > 0.", request.ClientOrderId));
        }
        if (request.DecisionDataEventIds.IsDefaultOrEmpty)
        {
            return OrderCreationResult.Failed(StrictRunStatus.EvidenceMissing,
                Issue(StrictRunIssueCode.InsufficientDecisionEvidence, "An executable order must identify its decision evidence.", request.ClientOrderId));
        }
        if (request.DecisionDataEventIds.Any(string.IsNullOrWhiteSpace) ||
            request.DecisionDataEventIds.Distinct(StringComparer.Ordinal).Count() != request.DecisionDataEventIds.Length)
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.InvalidOrder, "DecisionDataEventIds must be non-blank and unique.", request.ClientOrderId));
        }

        DateTime informationTime = DateTime.MinValue;
        foreach (string eventId in request.DecisionDataEventIds)
        {
            if (!visibleById.TryGetValue(eventId, out StrictMarketEvent? evidence))
            {
                return OrderCreationResult.Failed(StrictRunStatus.Failed,
                    Issue(StrictRunIssueCode.CausalityViolation, "The strategy referenced data that is not visible at its decision time.", request.ClientOrderId, eventId));
            }
            DateTime effective = EffectiveAvailability(evidence);
            if (effective > informationTime) informationTime = effective;
        }

        if (request.DecisionTimeUtc.Kind != DateTimeKind.Utc || request.CreationTimeUtc.Kind != DateTimeKind.Utc || request.EligibleFromUtc.Kind != DateTimeKind.Utc ||
            request.DecisionTimeUtc != simulationTime || informationTime > request.DecisionTimeUtc ||
            request.DecisionTimeUtc > request.CreationTimeUtc || request.CreationTimeUtc > request.EligibleFromUtc)
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.CausalityViolation,
                    "The order violates InformationTime <= DecisionTime <= CreationTime <= EligibleFrom.", request.ClientOrderId));
        }

        string? priceError = ValidateOrderPrices(request);
        if (priceError is not null)
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.InvalidOrder, priceError, request.ClientOrderId));
        }
        if (!ValidProtectionRatio(request.StopLossRatio) || !ValidProtectionRatio(request.TakeProfitRatio))
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.InvalidOrder, "Protective ratios must be null or satisfy 0 <= ratio < 1.", request.ClientOrderId));
        }

        OrderSide side;
        switch (request.Intent)
        {
            case StrictOrderIntent.EnterLong when state.IsFlat:
                side = OrderSide.Buy;
                break;
            case StrictOrderIntent.EnterShort when state.IsFlat:
                side = OrderSide.Sell;
                break;
            case StrictOrderIntent.ExitPosition when !state.IsFlat && request.Quantity == StrictAccountReducer.AbsPosition(state.PositionQuantity):
                side = state.PositionQuantity > 0L ? OrderSide.Sell : OrderSide.Buy;
                break;
            default:
                return OrderCreationResult.Failed(StrictRunStatus.Failed,
                    Issue(StrictRunIssueCode.InvalidOrder,
                        "Entry requires a flat account; reduce-only exit must close the complete position.", request.ClientOrderId));
        }

        bool requestIsEntry = request.Intent is StrictOrderIntent.EnterLong or StrictOrderIntent.EnterShort;
        bool sameRolePending = existingOrders.Any(order => order.Status == StrictOrderStatus.Pending &&
            ((requestIsEntry && (order.Intent is StrictOrderIntent.EnterLong or StrictOrderIntent.EnterShort)) ||
             (request.Intent == StrictOrderIntent.ExitPosition && order.Intent == StrictOrderIntent.ExitPosition && order.ParentFillId is null)));
        if (sameRolePending)
        {
            return OrderCreationResult.Failed(StrictRunStatus.Failed,
                Issue(StrictRunIssueCode.InvalidOrder, "A pending order already occupies the same strict order role.", request.ClientOrderId));
        }

        var order = new RuntimeOrder(
            orderId,
            request.ClientOrderId,
            request.Intent,
            side,
            request.Type,
            request.Quantity,
            request.LimitPrice,
            request.StopPrice,
            informationTime,
            request.DecisionTimeUtc,
            request.CreationTimeUtc,
            request.EligibleFromUtc,
            ImmutableArray.CreateRange(request.DecisionDataEventIds),
            null,
            null,
            request.StopLossRatio,
            request.TakeProfitRatio,
            null,
            null);
        return OrderCreationResult.Succeeded(order);
    }

    private static string? ValidateOrderPrices(StrictOrderRequest request)
        => request.Type switch
        {
            OrderType.Market or OrderType.MarketOnClose when request.LimitPrice is null && request.StopPrice is null => null,
            OrderType.Limit when request.LimitPrice > 0m && request.StopPrice is null => null,
            OrderType.Stop when request.StopPrice > 0m && request.LimitPrice is null => null,
            OrderType.StopLimit when request.StopPrice > 0m && request.LimitPrice > 0m => null,
            _ => "Order price fields do not match the selected order type or are not positive.",
        };

    private static bool ValidProtectionRatio(decimal? ratio)
        => ratio is null || ratio.Value >= 0m && ratio.Value < 1m;

    private static long RemainingQuantity(
        StrictMarketEvent marketEvent,
        IReadOnlyDictionary<string, long> remainingByEvidenceId)
    {
        if (marketEvent.LiquidityEvidenceId is { } evidenceId && remainingByEvidenceId.TryGetValue(evidenceId, out long remaining))
        {
            return remaining;
        }
        return marketEvent.Quantity;
    }

    private static DateTime EffectiveAvailability(StrictMarketEvent marketEvent)
        => marketEvent.KnowledgeTimeUtc is { } knowledgeTime && knowledgeTime > marketEvent.AvailableTimeUtc
            ? knowledgeTime
            : marketEvent.AvailableTimeUtc;

    private static StrictCausalEventRef CausalRef(StrictMarketEvent marketEvent)
        => new(
            marketEvent.EventId,
            marketEvent.SourceId,
            marketEvent.DataVersion,
            marketEvent.EventTimeUtc,
            marketEvent.AvailableTimeUtc,
            EffectiveAvailability(marketEvent),
            marketEvent.SourceSequence);

    private static StrictRunIssue Issue(StrictRunIssueCode code, string message, params string[] relatedIds)
        => new(code, message, relatedIds.ToImmutableArray());

    private static StrictBacktestResult Terminal(
        StrictRunStatus status,
        StrictAccountState state,
        List<RuntimeOrder> orders,
        List<StrictFill> fills,
        List<StrictRunPhase> phases,
        string strategyName,
        params StrictRunIssue[] issues)
    {
        phases.Add(status switch
        {
            StrictRunStatus.Completed => StrictRunPhase.Completed,
            StrictRunStatus.Cancelled => StrictRunPhase.Cancelled,
            StrictRunStatus.Ambiguous => StrictRunPhase.Ambiguous,
            StrictRunStatus.EvidenceMissing => StrictRunPhase.EvidenceMissing,
            StrictRunStatus.Failed => StrictRunPhase.Failed,
            StrictRunStatus.Insolvent => StrictRunPhase.Insolvent,
            StrictRunStatus.Unsupported => StrictRunPhase.Unsupported,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown StrictRunStatus."),
        });
        return new StrictBacktestResult(
            status,
            state,
            orders.Select(order => order.Snapshot()).ToImmutableArray(),
            fills.ToImmutableArray(),
            issues.ToImmutableArray(),
            phases.ToImmutableArray(),
            strategyName);
    }

    private sealed class RuntimeOrder
    {
        public long OrderId { get; }
        public string ClientOrderId { get; }
        public StrictOrderIntent Intent { get; }
        public OrderSide Side { get; }
        public OrderType Type { get; }
        public long Quantity { get; }
        public decimal? LimitPrice { get; }
        public decimal? StopPrice { get; }
        public DateTime InformationTimeUtc { get; }
        public DateTime DecisionTimeUtc { get; }
        public DateTime CreationTimeUtc { get; }
        public DateTime EligibleFromUtc { get; }
        public ImmutableArray<string> DecisionDataEventIds { get; }
        public StrictCausalEventRef? ActivationEvidence { get; }
        public StrictCausalEventRef? TriggerEvidence { get; set; }
        public decimal? StopLossRatio { get; }
        public decimal? TakeProfitRatio { get; }
        public long? OcoGroupId { get; }
        public long? ParentFillId { get; }
        public StrictOrderStatus Status { get; set; } = StrictOrderStatus.Pending;
        public bool StopActivated => TriggerEvidence is not null;

        public RuntimeOrder(
            long orderId,
            string clientOrderId,
            StrictOrderIntent intent,
            OrderSide side,
            OrderType type,
            long quantity,
            decimal? limitPrice,
            decimal? stopPrice,
            DateTime informationTimeUtc,
            DateTime decisionTimeUtc,
            DateTime creationTimeUtc,
            DateTime eligibleFromUtc,
            ImmutableArray<string> decisionDataEventIds,
            StrictCausalEventRef? activationEvidence,
            StrictCausalEventRef? triggerEvidence,
            decimal? stopLossRatio,
            decimal? takeProfitRatio,
            long? ocoGroupId,
            long? parentFillId)
        {
            OrderId = orderId;
            ClientOrderId = clientOrderId;
            Intent = intent;
            Side = side;
            Type = type;
            Quantity = quantity;
            LimitPrice = limitPrice;
            StopPrice = stopPrice;
            InformationTimeUtc = informationTimeUtc;
            DecisionTimeUtc = decisionTimeUtc;
            CreationTimeUtc = creationTimeUtc;
            EligibleFromUtc = eligibleFromUtc;
            DecisionDataEventIds = decisionDataEventIds;
            ActivationEvidence = activationEvidence;
            TriggerEvidence = triggerEvidence;
            StopLossRatio = stopLossRatio;
            TakeProfitRatio = takeProfitRatio;
            OcoGroupId = ocoGroupId;
            ParentFillId = parentFillId;
        }

        public StrictOrderSnapshot Snapshot() => new(
            OrderId,
            ClientOrderId,
            Intent,
            Side,
            Type,
            Quantity,
            LimitPrice,
            StopPrice,
            InformationTimeUtc,
            DecisionTimeUtc,
            CreationTimeUtc,
            EligibleFromUtc,
            DecisionDataEventIds,
            ActivationEvidence,
            TriggerEvidence,
            StopLossRatio,
            TakeProfitRatio,
            Status,
            StopActivated,
            OcoGroupId,
            ParentFillId);
    }

    private sealed record OrderCreationResult(RuntimeOrder? Order, StrictRunStatus? Status, StrictRunIssue? Issue)
    {
        public static OrderCreationResult Succeeded(RuntimeOrder order) => new(order, null, null);
        public static OrderCreationResult Failed(StrictRunStatus status, StrictRunIssue issue) => new(null, status, issue);
    }
}
