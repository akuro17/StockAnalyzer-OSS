using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Backtest.Engine.StrictEvidence;
using StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.StrictEvidence;

public sealed class StrictBacktestEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Input_OwnsEvents_AndRecordsInvalidEvidenceBoundaries()
    {
        var source = new List<StrictMarketEvent> { Valuation("v0", T0, 100m) };
        StrictBacktestInput input = Input(source);
        source.Clear();
        Assert.Single(input.Events);

        Assert.Contains(Input(new[]
        {
            Valuation("dup", T0, 100m),
            Valuation("dup", T0.AddMinutes(1), 101m),
        }).ValidationIssues, issue => issue.Code == StrictRunIssueCode.InvalidInput);
        Assert.Contains(
            Input(new[] { Valuation("bad-price", T0, 0m) }).ValidationIssues,
            issue => issue.Code == StrictRunIssueCode.InvalidInput);
        Assert.Contains(Input(new[]
        {
            Valuation("local-time", DateTime.SpecifyKind(T0, DateTimeKind.Local), 100m),
        }).ValidationIssues, issue => issue.Code == StrictRunIssueCode.InvalidInput);
        Assert.Contains(Input(new[]
        {
            Event("late-before-event", T0.AddMinutes(2), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, "liq"),
        }).ValidationIssues, issue => issue.Code == StrictRunIssueCode.InvalidInput);
    }

    [Fact]
    public void StrategyView_HidesUnavailableAndFutureKnowledgeVersions()
    {
        var recorder = new RecordingStrategy();
        StrictBacktestInput input = Input(new[]
        {
            Valuation("now", T0, 100m),
            Valuation("delayed", T0.AddMinutes(1), 101m, available: T0.AddMinutes(3)),
            Valuation("revision", T0.AddMinutes(1), 102m, available: T0.AddMinutes(1), knowledge: T0.AddMinutes(4), revisionOf: "now"),
            Valuation("clock", T0.AddMinutes(2), 103m),
        }, supportsPointInTime: true);

        StrictBacktestResult result = Engine().Run(input, Config(), recorder);

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "now" }, recorder.VisibleAt[T0]);
        Assert.DoesNotContain("delayed", recorder.VisibleAt[T0.AddMinutes(2)]);
        Assert.DoesNotContain("revision", recorder.VisibleAt[T0.AddMinutes(2)]);
        Assert.Contains("delayed", recorder.VisibleAt[T0.AddMinutes(3)]);
        Assert.Contains("revision", recorder.VisibleAt[T0.AddMinutes(4)]);
    }

    [Fact]
    public void RetainedStrategyContexts_KeepStableImmutablePrefixes()
    {
        var strategy = new RetainingStrategy();
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("first", T0, 100m),
                Valuation("second", T0.AddMinutes(1), 101m),
                Valuation("third", T0.AddMinutes(2), 102m),
            }),
            Config(),
            strategy);

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Equal(3, strategy.Contexts.Count);
        Assert.Equal(new[] { 1, 2, 3 }, strategy.Contexts.Select(context => context.VisibleEvents.Count));
        Assert.Equal("first", strategy.Contexts[0].VisibleEvents[0].EventId);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = strategy.Contexts[0].VisibleEvents[1]);
        Assert.Equal(new[] { "first", "second", "third" },
            strategy.Contexts[2].VisibleEvents.Select(item => item.EventId));
    }

    [Fact]
    public void SharedDefaultsAndCommissionOwner_PreserveStrictAccounting()
    {
        var legacy = new BacktestConfiguration
        {
            InitialCapital = 1_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
        };
        var strict = new StrictBacktestConfiguration { InitialCapital = 1_000m };

        Assert.Equal(BacktestDefaults.InitialMarginRatio, legacy.InitialMarginRatio);
        Assert.Equal(BacktestDefaults.InitialMarginRatio, strict.InitialMarginRatio);
        Assert.Equal(BacktestDefaults.MaintenanceMarginRatio, legacy.MaintenanceMarginRatio);
        Assert.Equal(BacktestDefaults.MaintenanceMarginRatio, strict.MaintenanceMarginRatio);

        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("fill", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 3L, OrderSide.Buy, "liq"),
            }),
            new StrictBacktestConfiguration
            {
                InitialCapital = 1_000m,
                FlatCommission = 2m,
                PerUnitCommission = 0.5m,
            },
            OneShot(context => MarketOrder(context, "entry", StrictOrderIntent.EnterLong, 3L, T0.AddMinutes(1))));

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Equal(3.5m, Assert.Single(result.Fills).Commission);
    }

    [Fact]
    public void MarketEntry_RequiresEligibleFutureEvent_AndNeverReusesDecisionEvent()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        StrictMarketEvent tooEarly = Event("early", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 10L, OrderSide.Buy, "liq-early");
        StrictMarketEvent eligible = Event("eligible", T0.AddMinutes(2), T0.AddMinutes(2), 101m, 10L, OrderSide.Buy, "liq-eligible");
        var strategy = OneShot(ctx => MarketOrder(ctx, "entry", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(2)));

        StrictBacktestResult result = Engine().Run(Input(new[] { decision, tooEarly, eligible }), Config(), strategy);

        StrictFill fill = Assert.Single(result.Fills);
        Assert.Equal("eligible", fill.WhyWasThisFillPossible.ExecutionEventId);
        Assert.Equal(T0.AddMinutes(2), fill.FillTimeUtc);
        Assert.Equal(StrictRunStatus.Completed, result.Status);
    }

    [Fact]
    public void Stop_DoesNotActivateFromEvidenceBeforeEligibleFrom()
    {
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("early-trigger", T0.AddMinutes(1), T0.AddMinutes(1), 120m, 1L, OrderSide.Buy, "liq-early"),
                Event("eligible-but-below-stop", T0.AddMinutes(2), T0.AddMinutes(2), 100m, 1L, OrderSide.Buy, "liq-later"),
            }),
            Config(),
            OneShot(_ => Request(
                "stop", StrictOrderIntent.EnterLong, OrderType.Stop, 1L,
                T0, T0.AddMinutes(2), "decision", stopPrice: 110m)));

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Empty(result.Fills);
        StrictOrderSnapshot order = Assert.Single(result.Orders);
        Assert.False(order.StopActivated);
        Assert.Null(order.TriggerEvidence);
    }

    [Fact]
    public void Stop_DoesNotFillFromAnEventThatPrecedesItsTriggerButArrivesLater()
    {
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("trigger", T0.AddMinutes(2), T0.AddMinutes(2), 120m, 1L, OrderSide.Buy, "liq-trigger"),
                Event("late-past", T0.AddMinutes(1), T0.AddMinutes(3), 100m, 1L, OrderSide.Buy, "liq-late"),
            }),
            Config(),
            OneShot(_ => Request(
                "stop", StrictOrderIntent.EnterLong, OrderType.Stop, 1L,
                T0, T0.AddMinutes(1), "decision", stopPrice: 110m)));

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Empty(result.Fills);
        StrictOrderSnapshot order = Assert.Single(result.Orders);
        Assert.True(order.StopActivated);
        Assert.Equal("trigger", order.TriggerEvidence?.EventId);
    }

    [Fact]
    public void LatePublishedPastEvent_CannotFillRetroactively()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        StrictMarketEvent latePast = Event("late", T0.AddMinutes(1), T0.AddMinutes(3), 100m, 10L, OrderSide.Buy, "liq-late");
        var strategy = OneShot(ctx => MarketOrder(ctx, "entry", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(2)));

        StrictBacktestResult result = Engine().Run(Input(new[] { decision, latePast }), Config(), strategy);

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Empty(result.Fills);
        Assert.Equal(StrictOrderStatus.Expired, Assert.Single(result.Orders).Status);
    }

    [Fact]
    public void UnknownLiquidity_StopsEvidenceMissing_ButProvenInsufficientLiquidityIsFalse()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        var strategy1 = OneShot(ctx => MarketOrder(ctx, "entry-unknown", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(1)));
        StrictMarketEvent unknown = Event("unknown", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 10L, OrderSide.Buy, "liq-unknown", complete: false);

        StrictBacktestResult missing = Engine().Run(Input(new[] { decision, unknown }), Config(), strategy1);

        Assert.Equal(StrictRunStatus.EvidenceMissing, missing.Status);
        Assert.Empty(missing.Fills);
        Assert.Contains(missing.Issues, issue => issue.Code == StrictRunIssueCode.MissingLiquidityEvidence);

        var strategy2 = OneShot(ctx => MarketOrder(ctx, "entry-false", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(1)));
        StrictMarketEvent insufficient = Event("insufficient", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 0L, OrderSide.Buy, "liq-zero", complete: true);
        StrictBacktestResult noFill = Engine().Run(Input(new[] { decision, insufficient }), Config(), strategy2);

        Assert.Equal(StrictRunStatus.Completed, noFill.Status);
        Assert.Empty(noFill.Fills);
        Assert.Empty(noFill.Issues);
    }

    [Fact]
    public void UnresolvedLiquidityEvidence_StopsWithoutFill()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        StrictMarketEvent execution = Event("execution", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 10L, OrderSide.Buy, "not-in-contract");
        var contract = Contract(Array.Empty<string>());
        var input = new StrictBacktestInput(new[] { decision, execution }, contract, T0, T0);

        StrictBacktestResult result = Engine().Run(input, Config(), OneShot(ctx => MarketOrder(ctx, "entry", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(1))));

        Assert.Equal(StrictRunStatus.EvidenceMissing, result.Status);
        Assert.Empty(result.Fills);
        Assert.Equal(StrictRunIssueCode.UnresolvedEvidenceId, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void MultipleUnknownConditions_PreserveAllReasonsInCanonicalOrder()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        var incomplete = new StrictMarketEvent(
            "fixture-provider", "data-v1", "unknowns", T0.AddMinutes(1), T0.AddMinutes(1), null,
            100m, 10L, "coverage-unknowns", null, null, StrictMarketEventKind.Execution,
            null, null, false);

        StrictBacktestResult result = Engine().Run(
            Input(new[] { decision, incomplete }),
            Config(),
            OneShot(ctx => MarketOrder(ctx, "entry", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(1))));

        Assert.Equal(StrictRunStatus.EvidenceMissing, result.Status);
        Assert.Equal(
            new[] { StrictRunIssueCode.MissingExecutionEvidence, StrictRunIssueCode.MissingLiquidityEvidence },
            result.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void IncompleteAndUnresolvedLiquidity_PreservesBothReasons()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        StrictMarketEvent incomplete = Event(
            "unknown", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 10L,
            OrderSide.Buy, "not-resolvable", complete: false);
        StrictBacktestInput input = new(
            new[] { decision, incomplete }, Contract(Array.Empty<string>()), T0, T0);

        StrictBacktestResult result = Engine().Run(
            input, Config(),
            OneShot(ctx => MarketOrder(ctx, "entry", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(1))));

        Assert.Equal(StrictRunStatus.EvidenceMissing, result.Status);
        Assert.Equal(
            new[] { StrictRunIssueCode.MissingLiquidityEvidence, StrictRunIssueCode.UnresolvedEvidenceId },
            result.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void LimitAndAuctionConditions_DoNotManufactureFills()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        StrictMarketEvent aboveBuyLimit = Event("limit-miss", T0.AddMinutes(1), T0.AddMinutes(1), 101m, 1L, OrderSide.Buy, "liq-limit");
        StrictOrderRequest limit = Request(
            "limit", StrictOrderIntent.EnterLong, OrderType.Limit, 1L, T0, T0.AddMinutes(1), "decision", limitPrice: 100m);

        StrictBacktestResult noFill = Engine().Run(Input(new[] { decision, aboveBuyLimit }), Config(), OneShot(_ => limit));
        Assert.Equal(StrictRunStatus.Completed, noFill.Status);
        Assert.Empty(noFill.Fills);

        StrictMarketEvent auction = Event(
            "auction", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, "liq-auction",
            kind: StrictMarketEventKind.CloseAuction);
        StrictOrderRequest moc = Request("moc", StrictOrderIntent.EnterLong, OrderType.MarketOnClose, 1L, T0, T0.AddMinutes(1), "decision");
        StrictBacktestResult missing = Engine().Run(
            Input(new[] { decision, auction }, supportsAuction: false), Config(), OneShot(_ => moc));
        Assert.Equal(StrictRunStatus.EvidenceMissing, missing.Status);
        Assert.Empty(missing.Fills);
    }

    [Fact]
    public void CapitalBoundaryAndReduceOnlyExit_UseDefinedAccounting()
    {
        StrictMarketEvent decision = Valuation("decision", T0, 100m);
        StrictMarketEvent entryFill = Event("entry-fill", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 10L, OrderSide.Buy, "liq-entry");
        StrictMarketEvent exitFill = Event("exit-fill", T0.AddMinutes(2), T0.AddMinutes(2), 90m, 10L, OrderSide.Sell, "liq-exit");
        var strategy = new EntryThenExitStrategy();
        var configuration = new StrictBacktestConfiguration
        {
            InitialCapital = 501m,
            FlatCommission = 1m,
            PerUnitCommission = 0m,
            InitialMarginRatio = 0.5m,
            MaintenanceMarginRatio = 0.2m,
        };

        StrictBacktestResult result = Engine().Run(Input(new[] { decision, entryFill, exitFill }), configuration, strategy);

        Assert.Equal(2, result.Fills.Length);
        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Equal(399m, result.FinalState.Cash);
        Assert.Equal(0m, result.FinalState.HeldMargin);
        Assert.Equal(0L, result.FinalState.PositionQuantity);
        Assert.All(result.Fills, fill => Assert.Equal(StrictEvidenceVerdict.True, fill.WhyWasThisFillPossible.Conditions.CapitalAvailable.Verdict));

        // At magnitude 501, decimal's 29-significant-digit representation can express 1e-25,
        // while 1e-28 would round back to 501 and would not actually test the boundary.
        decimal oneUnitLess = 501m - 0.0000000000000000000000001m;
        StrictBacktestResult insufficient = Engine().Run(
            Input(new[] { decision, entryFill }),
            WithInitialCapital(oneUnitLess),
            OneShot(ctx => MarketOrder(ctx, "entry", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(1))));
        Assert.Empty(insufficient.Fills);
    }

    [Fact]
    public void EveryFillAudit_ReplaysExactlyFromInitialState()
    {
        StrictBacktestConfiguration configuration = Config();
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("entry", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 10L, OrderSide.Buy, "liq-entry"),
                Event("exit", T0.AddMinutes(2), T0.AddMinutes(2), 110m, 10L, OrderSide.Sell, "liq-exit"),
            }),
            configuration,
            new EntryThenExitStrategy());

        StrictAccountState replay = StrictAccountState.Initial(configuration.InitialCapital);
        foreach (StrictFill fill in result.Fills)
        {
            StrictFillAudit audit = fill.WhyWasThisFillPossible;
            Assert.Equal(replay.Cash, audit.AvailableCashBefore);
            Assert.Equal(replay.HeldMargin, audit.HeldMarginBefore);
            Assert.Equal(replay.PositionQuantity, audit.PositionBefore);
            Assert.Equal(replay.EntryPrice, audit.EntryPriceBefore);
            Assert.Equal(5, CountTrue(audit.Conditions));

            decimal fee = configuration.FlatCommission + configuration.PerUnitCommission * fill.Quantity;
            if (replay.IsFlat)
            {
                decimal margin = fill.Quantity * fill.Price * configuration.InitialMarginRatio;
                long signed = fill.Side == OrderSide.Buy ? fill.Quantity : -fill.Quantity;
                replay = new StrictAccountState(replay.Cash - margin - fee, margin, signed, fill.Price);
            }
            else
            {
                replay = new StrictAccountState(
                    replay.Cash + replay.HeldMargin + replay.PositionQuantity * (fill.Price - replay.EntryPrice) - fee,
                    0m, 0L, 0m);
            }
            Assert.Equal(replay, fill.StateAfter);
        }
        Assert.Equal(replay, result.FinalState);
    }

    [Fact]
    public void ProtectiveOrders_ActivateAfterParent_AndSequencedOcoCommitsOnlyFirstFill()
    {
        StrictBacktestInput input = Input(new[]
        {
            Valuation("decision", T0, 100m),
            Event("parent", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, "liq-parent", sequence: 1),
            Event("stop-trigger", T0.AddMinutes(2), T0.AddMinutes(2), 90m, 1L, OrderSide.Sell, "liq-trigger", sequence: 1),
            Event("stop-fill", T0.AddMinutes(2), T0.AddMinutes(2), 89m, 1L, OrderSide.Sell, "liq-stop", sequence: 2),
            Event("take-profit", T0.AddMinutes(2), T0.AddMinutes(2), 110m, 1L, OrderSide.Sell, "liq-tp", sequence: 3),
        }, supportsSequence: true);
        StrictOrderRequest entry = Request(
            "protected", StrictOrderIntent.EnterLong, OrderType.Market, 1L, T0, T0.AddMinutes(1), "decision",
            stopLoss: 0.10m, takeProfit: 0.10m);

        StrictBacktestResult result = Engine().Run(input, Config(), OneShot(_ => entry));

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Equal(2, result.Fills.Length);
        Assert.Equal("parent", result.Fills[0].WhyWasThisFillPossible.ExecutionEventId);
        Assert.Equal("stop-fill", result.Fills[1].WhyWasThisFillPossible.ExecutionEventId);
        Assert.Contains(result.Orders, order => order.ClientOrderId.Contains(":TP:", StringComparison.Ordinal) && order.Status == StrictOrderStatus.Cancelled);
        Assert.True(result.Fills[1].FillTimeUtc >= result.Fills[0].FillTimeUtc);

        StrictFill protectiveFill = result.Fills[1];
        Assert.Equal(2, protectiveFill.WhyWasThisFillPossible.SchemaVersion);
        Assert.Equal("fixture-v1", protectiveFill.WhyWasThisFillPossible.ProviderContractVersion);
        Assert.Equal("parent", protectiveFill.WhyWasThisFillPossible.OrderContext.ActivationEvidence?.EventId);
        Assert.Equal("stop-trigger", protectiveFill.WhyWasThisFillPossible.OrderContext.TriggerEvidence?.EventId);
        Assert.Contains(
            protectiveFill.WhyWasThisFillPossible.CausalEvidence,
            item => item.EventId == "stop-trigger" && item.SourceId == "fixture-provider" && item.DataVersion == "data-v1");
    }

    [Fact]
    public void SameTimestampParentAndProtectiveFillWithoutSequence_IsAmbiguousAfterParentPrefix()
    {
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("a-parent", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, "liq-parent"),
                Event("b-take-profit", T0.AddMinutes(1), T0.AddMinutes(1), 110m, 1L, OrderSide.Sell, "liq-tp"),
            }),
            Config(),
            OneShot(_ => Request(
                "protected", StrictOrderIntent.EnterLong, OrderType.Market, 1L,
                T0, T0.AddMinutes(1), "decision", takeProfit: 0.10m)));

        Assert.Equal(StrictRunStatus.Ambiguous, result.Status);
        StrictFill parent = Assert.Single(result.Fills);
        Assert.Equal("a-parent", parent.WhyWasThisFillPossible.ExecutionEventId);
        Assert.Equal(StrictOrderStatus.Pending,
            Assert.Single(result.Orders.Where(order => order.ParentFillId == parent.FillId)).Status);
    }

    [Fact]
    public void EarliestUniqueEventCommitsBeforeLaterUnsequencedEventsBecomeIrrelevant()
    {
        DateTime availability = T0.AddMinutes(3);
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("first", T0.AddMinutes(1), availability, 100m, 1L, OrderSide.Buy, "liq-first"),
                Event("later-a", T0.AddMinutes(2), availability, 101m, 1L, OrderSide.Buy, "liq-later-a"),
                Event("later-b", T0.AddMinutes(2), availability, 102m, 1L, OrderSide.Buy, "liq-later-b"),
            }),
            Config(),
            OneShot(_ => Request(
                "entry", StrictOrderIntent.EnterLong, OrderType.Market, 1L,
                T0, T0.AddMinutes(1), "decision")));

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        Assert.Equal("first", Assert.Single(result.Fills).WhyWasThisFillPossible.ExecutionEventId);
    }

    [Fact]
    public void ContradictoryReuseOfLiquidityEvidence_IsInvalidInput()
    {
        StrictBacktestInput input = Input(new[]
        {
            Valuation("decision", T0, 100m),
            Event("buy-record", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 100L, OrderSide.Buy, "shared"),
            Event("sell-record", T0.AddMinutes(2), T0.AddMinutes(2), 100m, 0L, OrderSide.Sell, "shared"),
        });

        StrictBacktestResult result = Engine().Run(input, Config(), new RecordingStrategy());

        Assert.Equal(StrictRunStatus.Failed, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == StrictRunIssueCode.InvalidInput);
        Assert.Equal(new[] { StrictRunPhase.Created, StrictRunPhase.Validating, StrictRunPhase.Failed }, result.StateTransitions);
    }

    [Fact]
    public void UnsequencedProtectiveCompetition_IsAmbiguousAndCommitsNoCompetingFill()
    {
        StrictBacktestInput input = Input(new[]
        {
            Valuation("decision", T0, 100m),
            Event("parent", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, "liq-parent"),
            Event("stop-trigger", T0.AddMinutes(2), T0.AddMinutes(2), 90m, 1L, OrderSide.Sell, "liq-trigger"),
            Event("stop-fill", T0.AddMinutes(2), T0.AddMinutes(2), 89m, 1L, OrderSide.Sell, "liq-stop"),
            Event("take-profit", T0.AddMinutes(2), T0.AddMinutes(2), 110m, 1L, OrderSide.Sell, "liq-tp"),
        });
        StrictOrderRequest entry = Request(
            "protected", StrictOrderIntent.EnterLong, OrderType.Market, 1L, T0, T0.AddMinutes(1), "decision",
            stopLoss: 0.10m, takeProfit: 0.10m);

        StrictBacktestResult result = Engine().Run(input, Config(), OneShot(_ => entry));

        Assert.Equal(StrictRunStatus.Ambiguous, result.Status);
        Assert.Single(result.Fills);
        Assert.Equal(950m, result.FinalState.Cash);
        Assert.Contains(result.Issues, issue => issue.Code == StrictRunIssueCode.AmbiguousExecutionOrder);
    }

    [Fact]
    public void NumericFailureBeforeAtomicCommit_LeavesFillStateAndLiquidityUnchanged()
    {
        StrictBacktestConfiguration configuration = new()
        {
            InitialCapital = decimal.MaxValue,
            InitialMarginRatio = 1m,
            MaintenanceMarginRatio = 0.5m,
        };
        StrictOrderRequest entry = Request(
            "overflow-protection", StrictOrderIntent.EnterLong, OrderType.Market, 1L, T0, T0.AddMinutes(1), "decision",
            takeProfit: 0.5m);
        StrictBacktestInput input = Input(new[]
        {
            Valuation("decision", T0, 1m),
            Event("execution", T0.AddMinutes(1), T0.AddMinutes(1), decimal.MaxValue, 1L, OrderSide.Buy, "liq-max"),
        });

        StrictBacktestResult result = Engine().Run(input, configuration, OneShot(_ => entry));

        Assert.Equal(StrictRunStatus.Failed, result.Status);
        Assert.Empty(result.Fills);
        Assert.Equal(StrictAccountState.Initial(decimal.MaxValue), result.FinalState);
        Assert.Equal(StrictOrderStatus.Pending, Assert.Single(result.Orders).Status);
        Assert.Equal(StrictRunIssueCode.NumericFailure, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task SameEngineInstance_RunsOneHundredIndependentRunsConcurrently()
    {
        StrictBacktestEngine engine = Engine();
        Task<StrictBacktestResult>[] tasks = Enumerable.Range(0, 100)
            .Select(index => Task.Run(() => engine.Run(
                Input(new[]
                {
                    Valuation($"decision-{index}", T0, 100m),
                    Event($"fill-{index}", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, $"liq-{index}"),
                }),
                Config(),
                OneShot(ctx => MarketOrder(ctx, $"entry-{index}", StrictOrderIntent.EnterLong, 1L, T0.AddMinutes(1))))))
            .ToArray();

        StrictBacktestResult[] results = await Task.WhenAll(tasks);

        Assert.All(results, result =>
        {
            Assert.Equal(StrictRunStatus.Completed, result.Status);
            Assert.Single(result.Fills);
            Assert.Equal(950m, result.FinalState.Cash);
            Assert.Equal(50m, result.FinalState.HeldMargin);
        });
    }

    [Fact]
    public void EmptyAndUnsupportedInputs_ReturnExplicitTerminalStates()
    {
        StrictBacktestResult empty = Engine().Run(Input(Array.Empty<StrictMarketEvent>()), Config(), new RecordingStrategy());
        Assert.Equal(StrictRunStatus.EvidenceMissing, empty.Status);
        Assert.Equal(StrictRunIssueCode.NoMarketData, Assert.Single(empty.Issues).Code);

        StrictBacktestInput unsupported = new(
            new[] { Valuation("v0", T0, 100m) },
            Contract(Array.Empty<string>()),
            T0,
            T0,
            requiresCorporateActions: true);
        StrictBacktestResult result = Engine().Run(unsupported, Config(), new RecordingStrategy());
        Assert.Equal(StrictRunStatus.Unsupported, result.Status);
        Assert.Equal(StrictRunIssueCode.UnsupportedCorporateAction, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void InvalidMarketInput_ReturnsFailedInvalidInputInsteadOfThrowing()
    {
        StrictBacktestInput input = Input(new[] { Valuation("invalid", T0, 0m) });

        StrictBacktestResult result = Engine().Run(input, Config(), new RecordingStrategy());

        Assert.Equal(StrictRunStatus.Failed, result.Status);
        Assert.Equal(StrictRunIssueCode.InvalidInput, Assert.Single(result.Issues).Code);
        Assert.Equal(new[] { StrictRunPhase.Created, StrictRunPhase.Validating, StrictRunPhase.Failed }, result.StateTransitions);
    }

    [Fact]
    public void InputBoundaryErrors_AreCollectedBeforeTheRunStarts()
    {
        StrictMarketEvent first = Event(
            "first", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L,
            OrderSide.Buy, "liq-first", sequence: 1);
        StrictMarketEvent duplicateSequence = Event(
            "second", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L,
            OrderSide.Buy, "liq-second", sequence: 1);
        StrictEvidenceProviderContract oneEventContract = new(
            "fixture-provider", "fixture-v1", true, false, true, 1,
            new[] { "liq-first", "liq-second" });

        StrictBacktestInput overCapacity = new(
            new[] { first, duplicateSequence }, oneEventContract, T0, T0);
        StrictBacktestInput duplicate = new(
            new[] { first, duplicateSequence },
            Contract(new[] { "liq-first", "liq-second" }, supportsSequence: true), T0, T0);

        Assert.Contains(overCapacity.ValidationIssues, issue => issue.Code == StrictRunIssueCode.InvalidInput);
        Assert.Throws<ArgumentException>(() => new StrictBacktestInput(
            new StrictMarketEvent[] { null! }, Contract(Array.Empty<string>()), T0, T0));
        Assert.Contains(duplicate.ValidationIssues, issue => issue.Message.Contains("Duplicate SourceSequence", StringComparison.Ordinal));
    }

    [Fact]
    public void StopLimit_FillsOnlyOnAProvenPostTriggerEventThatSatisfiesTheLimit()
    {
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("trigger", T0.AddMinutes(1), T0.AddMinutes(1), 110m, 1L, OrderSide.Buy, "liq-trigger", sequence: 1),
                Event("limit-miss", T0.AddMinutes(1), T0.AddMinutes(1), 109m, 1L, OrderSide.Buy, "liq-miss", sequence: 2),
                Event("limit-fill", T0.AddMinutes(2), T0.AddMinutes(2), 108m, 1L, OrderSide.Buy, "liq-fill", sequence: 1),
            }, supportsSequence: true),
            Config(),
            OneShot(_ => Request(
                "stop-limit", StrictOrderIntent.EnterLong, OrderType.StopLimit, 1L,
                T0, T0.AddMinutes(1), "decision", limitPrice: 108m, stopPrice: 110m)));

        Assert.Equal(StrictRunStatus.Completed, result.Status);
        StrictFill fill = Assert.Single(result.Fills);
        Assert.Equal("limit-fill", fill.WhyWasThisFillPossible.ExecutionEventId);
        Assert.Equal("trigger", fill.WhyWasThisFillPossible.OrderContext.TriggerEvidence?.EventId);
    }

    [Fact]
    public void CancellationAfterACommittedFill_PreservesTheCommittedPrefix()
    {
        using var cancellation = new CancellationTokenSource();
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("entry", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, "liq-entry"),
            }),
            Config(),
            new CancelAfterEntryStrategy(cancellation),
            cancellation.Token);

        Assert.Equal(StrictRunStatus.Cancelled, result.Status);
        Assert.Single(result.Fills);
        Assert.Equal("entry", result.Fills[0].WhyWasThisFillPossible.ExecutionEventId);
    }

    [Fact]
    public void UnsupportedInput_PreservesEveryIndependentReasonInCanonicalOrder()
    {
        StrictBacktestInput input = new(
            new[] { Valuation("v0", T0, 100m) },
            Contract(Array.Empty<string>()), T0, T0,
            requiresCorporateActions: true,
            requiresExternalCashTransfers: true,
            requiresBorrowing: true);

        StrictBacktestResult result = Engine().Run(input, Config(), new RecordingStrategy());

        Assert.Equal(StrictRunStatus.Unsupported, result.Status);
        Assert.Equal(
            new[]
            {
                StrictRunIssueCode.UnsupportedCorporateAction,
                StrictRunIssueCode.UnsupportedCashTransfer,
                StrictRunIssueCode.UnsupportedBorrowing,
            },
            result.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void StrategyFailureAfterCommittedFill_ReturnsFailedWithTheCommittedPrefix()
    {
        StrictBacktestResult result = Engine().Run(
            Input(new[]
            {
                Valuation("decision", T0, 100m),
                Event("entry", T0.AddMinutes(1), T0.AddMinutes(1), 100m, 1L, OrderSide.Buy, "liq-entry"),
                Valuation("next", T0.AddMinutes(2), 100m),
            }),
            Config(),
            new ThrowAfterEntryStrategy());

        Assert.Equal(StrictRunStatus.Failed, result.Status);
        Assert.Single(result.Fills);
        Assert.Equal(StrictRunIssueCode.StrategyFailure, Assert.Single(result.Issues).Code);
    }

    private static StrictBacktestEngine Engine() => new();

    private static StrictBacktestConfiguration Config() => new()
    {
        InitialCapital = 1_000m,
        FlatCommission = 0m,
        PerUnitCommission = 0m,
        InitialMarginRatio = 0.5m,
        MaintenanceMarginRatio = 0.2m,
    };

    private static StrictBacktestConfiguration WithInitialCapital(decimal capital) => new()
    {
        InitialCapital = capital,
        FlatCommission = 1m,
        PerUnitCommission = 0m,
        InitialMarginRatio = 0.5m,
        MaintenanceMarginRatio = 0.2m,
    };

    private static StrictBacktestInput Input(
        IEnumerable<StrictMarketEvent> events,
        bool supportsSequence = false,
        bool supportsPointInTime = false,
        bool supportsAuction = true)
    {
        StrictMarketEvent[] owned = events.ToArray();
        string[] evidenceIds = owned
            .Select(item => item.LiquidityEvidenceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new StrictBacktestInput(
            owned,
            Contract(evidenceIds, supportsSequence, supportsPointInTime, supportsAuction),
            T0,
            T0);
    }

    private static StrictEvidenceProviderContract Contract(
        IEnumerable<string> evidenceIds,
        bool supportsSequence = false,
        bool supportsPointInTime = false,
        bool supportsAuction = true)
        => new("fixture-provider", "fixture-v1", supportsSequence, supportsPointInTime, supportsAuction, 10_000, evidenceIds);

    private static StrictMarketEvent Valuation(
        string id,
        DateTime eventTime,
        decimal price,
        DateTime? available = null,
        DateTime? knowledge = null,
        string? revisionOf = null)
        => new(
            "fixture-provider", "data-v1", id, eventTime, available ?? eventTime, null,
            price, 0L, $"coverage-{id}", revisionOf, knowledge, StrictMarketEventKind.Valuation,
            null, null, false);

    private static StrictMarketEvent Event(
        string id,
        DateTime eventTime,
        DateTime available,
        decimal price,
        long quantity,
        OrderSide side,
        string evidenceId,
        bool complete = true,
        long? sequence = null,
        StrictMarketEventKind kind = StrictMarketEventKind.Execution)
        => new(
            "fixture-provider", "data-v1", id, eventTime, available, sequence,
            price, quantity, $"coverage-{id}", null, null, kind, side, evidenceId, complete);

    private static StrictOrderRequest MarketOrder(
        StrictStrategyContext context,
        string id,
        StrictOrderIntent intent,
        long quantity,
        DateTime eligibleFrom)
        => Request(id, intent, OrderType.Market, quantity, context.SimulationTimeUtc, eligibleFrom, context.VisibleEvents[^1].EventId);

    private static StrictOrderRequest Request(
        string id,
        StrictOrderIntent intent,
        OrderType type,
        long quantity,
        DateTime decision,
        DateTime eligible,
        string decisionEventId,
        decimal? limitPrice = null,
        decimal? stopPrice = null,
        decimal? stopLoss = null,
        decimal? takeProfit = null)
        => new(
            id, intent, type, quantity, limitPrice, stopPrice, decision, decision, eligible,
            ImmutableArray.Create(decisionEventId), stopLoss, takeProfit);

    private static OneShotStrategy OneShot(Func<StrictStrategyContext, StrictOrderRequest> factory) => new(factory);

    private static int CountTrue(StrictFillConditions conditions)
        => new[]
        {
            conditions.Active.Verdict,
            conditions.Reachable.Verdict,
            conditions.LiquidityAvailable.Verdict,
            conditions.CapitalAvailable.Verdict,
            conditions.MarginSatisfied.Verdict,
        }.Count(value => value == StrictEvidenceVerdict.True);

    private sealed class OneShotStrategy : IStrictBacktestStrategy
    {
        private readonly Func<StrictStrategyContext, StrictOrderRequest> _factory;
        private bool _used;

        public OneShotStrategy(Func<StrictStrategyContext, StrictOrderRequest> factory) => _factory = factory;

        public string Name => "OneShot";

        public StrictOrderRequest? Evaluate(StrictStrategyContext context)
        {
            if (_used) return null;
            _used = true;
            return _factory(context);
        }
    }

    private sealed class EntryThenExitStrategy : IStrictBacktestStrategy
    {
        private bool _entrySubmitted;
        private bool _exitSubmitted;

        public string Name => "EntryThenExit";

        public StrictOrderRequest? Evaluate(StrictStrategyContext context)
        {
            if (!_entrySubmitted)
            {
                _entrySubmitted = true;
                return MarketOrder(context, "entry-order", StrictOrderIntent.EnterLong, 10L, T0.AddMinutes(1));
            }
            if (!context.AccountState.IsFlat && !_exitSubmitted)
            {
                _exitSubmitted = true;
                return MarketOrder(context, "exit-order", StrictOrderIntent.ExitPosition, 10L, T0.AddMinutes(2));
            }
            return null;
        }
    }

    private sealed class RecordingStrategy : IStrictBacktestStrategy
    {
        public string Name => "Recorder";
        public ConcurrentDictionary<DateTime, string[]> VisibleAt { get; } = new();

        public StrictOrderRequest? Evaluate(StrictStrategyContext context)
        {
            VisibleAt[context.SimulationTimeUtc] = context.VisibleEvents.Select(item => item.EventId).ToArray();
            return null;
        }
    }

    private sealed class RetainingStrategy : IStrictBacktestStrategy
    {
        public string Name => "Retaining";
        public List<StrictStrategyContext> Contexts { get; } = new();

        public StrictOrderRequest? Evaluate(StrictStrategyContext context)
        {
            Contexts.Add(context);
            return null;
        }
    }

    private sealed class ThrowAfterEntryStrategy : IStrictBacktestStrategy
    {
        private int _calls;

        public string Name => "ThrowAfterEntry";

        public StrictOrderRequest? Evaluate(StrictStrategyContext context)
        {
            _calls++;
            if (_calls == 1)
            {
                return MarketOrder(context, "entry", StrictOrderIntent.EnterLong, 1L, T0.AddMinutes(1));
            }
            throw new InvalidOperationException("strategy failed");
        }
    }

    private sealed class CancelAfterEntryStrategy : IStrictBacktestStrategy
    {
        private readonly CancellationTokenSource _cancellation;
        private int _calls;

        public CancelAfterEntryStrategy(CancellationTokenSource cancellation) => _cancellation = cancellation;

        public string Name => "CancelAfterEntry";

        public StrictOrderRequest? Evaluate(StrictStrategyContext context)
        {
            _calls++;
            if (_calls == 1)
            {
                return MarketOrder(context, "entry", StrictOrderIntent.EnterLong, 1L, T0.AddMinutes(1));
            }
            _cancellation.Cancel();
            return null;
        }
    }
}
