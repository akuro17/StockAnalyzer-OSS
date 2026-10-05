using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public sealed record RlFinancialEpisodeEvidence(string BlockId, IReadOnlyList<RlFinancialErrorRow> Decisions);
public sealed record RlFinancialParityEvidence(string SpecificationId, string SourceRevision, string ContractHash,
    string ModelSha256, int SameInputObservations, int SameRawTransitions, int OwnObservationTransitions,
    IReadOnlyList<RlFinancialEpisodeEvidence> Episodes);
internal sealed record RlFinancialParityWork(int Transitions, int EpisodeStarts, RlFinancialParityEvidence Evidence);

internal static partial class RlContinuousParity
{
    internal static RlFinancialParityWork ValidateFinancialDirectory(string directory, RlSourceSnapshot source,
        BacktestConfiguration costs, CancellationToken ct = default, Action<bool, bool>? accountWork = null)
    {
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Parity)));
        using var actor = new RlQModel(Path.Combine(directory, RlFiles.Model), continuous: true);
        using var direction = new RlQModel(Path.Combine(directory, RlContinuousContract.DirectionModel));
        if (job.RootElement.GetProperty("financial_parity_specification_id").GetString() != RlFinancialParityContract.SpecificationId
            || actor.Metadata.GetValueOrDefault("financial_parity_specification_id") != RlFinancialParityContract.SpecificationId
            || doc.RootElement.GetProperty("financial_parity_specification_id").GetString() != RlFinancialParityContract.SpecificationId)
            throw new InvalidDataException("Unsupported financial parity evidence version.");
        var episodes = doc.RootElement.GetProperty("paired_episodes");
        if (episodes.ValueKind != JsonValueKind.Array || episodes.GetArrayLength() != source.Catalog.Validation.Length)
            throw new InvalidDataException("All fixed financial parity blocks are required.");
        int index = 0, transitions = 0, starts = 0, decisions = 0;
        var evidence = new List<RlFinancialEpisodeEvidence>();
        foreach (var episode in episodes.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var block = source.Catalog.Validation[index++];
            if (episode.GetProperty("block_id").GetString() != block.Id) throw new InvalidDataException("Financial parity block order/identity differs.");
            var input = source.CreateInput(block);
            var engine = new BacktestEngine(IndicatorFactory.Default);
            using var native = engine.CreateRlContinuousSession(input, costs);
            using var sameRaw = engine.CreateRlContinuousSession(input, costs);
            using var exported = engine.CreateRlContinuousSession(input, costs);
            RlSessionSnapshot Reset(IRlTradingSession session)
            {
                accountWork?.Invoke(true, false); starts++;
                var state = session.Reset(ct); accountWork?.Invoke(true, true); return state;
            }
            RlStepResult Step(IRlContinuousTradingSession session, RlAction action, float[] raw)
            {
                accountWork?.Invoke(false, false); transitions++;
                var step = session.Step(action, raw[0], raw[1], ct); accountWork?.Invoke(false, true); return step;
            }
            var referenceState = Reset(native); var identicalState = Reset(sameRaw); var actualState = Reset(exported);
            var envelope = new RlFinancialEnvelope(costs);
            var errors = new List<RlFinancialErrorRow>();
            var stopIntervals = new Dictionary<long, RlDecimalInterval>();
            int fills = 0;
            var rows = episode.GetProperty("steps");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > RlContract.EpisodeHorizon)
                throw new InvalidDataException("Financial parity decision bound exceeded.");
            foreach (var row in rows.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (referenceState.Terminated || identicalState.Terminated || actualState.Terminated) throw new InvalidDataException("Post-terminal financial action.");
                var referenceObservation = row.GetProperty("observation").Deserialize<float[]>() ?? throw new InvalidDataException("Missing native observation.");
                var canonical = RlObservationBuilder.Build(referenceState, direction.Scaler);
                if (!referenceObservation.SequenceEqual(canonical)) throw new InvalidDataException("Native observation differs from exact replay.");
                var raw = row.GetProperty("actions").Deserialize<float[]>() ?? throw new InvalidDataException("Missing native raw action.");
                if (raw.Length != RlContinuousContract.ActionCount || raw.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid native raw action shape/value.");
                var sameInput = actor.Infer(referenceObservation);
                for (int channel = 0; channel < raw.Length; channel++)
                    if (Math.Abs((double)sameInput[channel] - raw[channel]) > Tolerance(raw[channel])) throw new InvalidDataException("Layer A same-observation raw parity failed.");
                var actualObservation = RlObservationBuilder.Build(actualState, direction.Scaler);
                var predicted = actor.Infer(actualObservation);
                var action = RlContract.SelectAction(direction.Infer(canonical));
                var actualAction = RlContract.SelectAction(direction.Infer(actualObservation));
                if (action.ToString() != row.GetProperty("direction").GetString() || action != actualAction) throw new InvalidDataException("Frozen direction event changed across inputs.");
                var expected = Step(native, action, raw);
                var identical = Step(sameRaw, action, raw);
                var actual = Step(exported, actualAction, predicted);
                var referenceResult = native.GetResult(); var identicalResult = sameRaw.GetResult(); var actualResult = exported.GetResult();
                var expectedTrace = RlFinancialTrace.Capture(expected, referenceResult);
                RequireExactJson(expectedTrace.ToWire(), row.GetProperty("financial_trace"), "Stored same-raw financial trace");
                if (job.RootElement.TryGetProperty(RlConstraintContract.MetadataKey, out _))
                    RequireExactJson(RlConstraintEvaluation.ToWire(expected.EntryExecution), row.GetProperty("entry_execution"), "Stored entry execution diagnostic");
                RequireExactJson(expectedTrace.ToWire(), RlFinancialTrace.Capture(identical, identicalResult).ToWire(), "Layer B identical raw ledger");
                // Keep the pre-R03 saved account/fill projection checked as well; redundant fields cannot contradict the trace.
                RequireSavedAccount(row.GetProperty("account"), expected.Snapshot);
                RequireSavedFills(row.GetProperty("fills"), referenceResult.Fills.Skip(fills).ToArray());
                if (row.GetProperty("outcome").GetString() != expected.Outcome.ToString()) throw new InvalidDataException("Saved action outcome differs.");
                RequireLifecycle(expected, actual);
                if (referenceState.Quantity == 0m && !referenceState.PendingEntry && action == RlAction.Buy && expected.Outcome == RlActionOutcome.Submitted)
                {
                    double tolerance = Tolerance(raw[1]);
                    decimal lower = RlContinuousContract.Map(raw[0], (float)Math.Clamp(raw[1] - tolerance, RlContinuousContract.ActionMinimum, RlContinuousContract.ActionMaximum)).StopDistance;
                    decimal upper = RlContinuousContract.Map(raw[0], (float)Math.Clamp(raw[1] + tolerance, RlContinuousContract.ActionMinimum, RlContinuousContract.ActionMaximum)).StopDistance;
                    envelope.RetainStopInterval(expected.Snapshot.EntryPrice, lower, upper);
                    foreach (var order in referenceResult.Orders.Where(o => o.Type == OrderType.Stop && !stopIntervals.ContainsKey(o.OrderId)))
                        stopIntervals.Add(order.OrderId, new(expected.Snapshot.EntryPrice * (1m - upper), expected.Snapshot.EntryPrice * (1m - lower)));
                }
                RequireOrders(referenceResult, actualResult, stopIntervals);
                decimal fillError = 0m, fillBound = 0m;
                if (referenceResult.Fills.Length != actualResult.Fills.Length) throw new InvalidDataException("Layer C fill events changed.");
                decimal quantity = referenceState.Quantity, entry = referenceState.EntryPrice, margin = referenceState.EquityPoint.HeldMargin;
                for (int fill = fills; fill < referenceResult.Fills.Length; fill++)
                {
                    var a = referenceResult.Fills[fill]; var b = actualResult.Fills[fill];
                    var order = referenceResult.Orders.Single(o => o.OrderId == a.OrderId);
                    bool forced = referenceResult.Trades.Any(t => t.ExitBar == a.BarIndex && t.IsForcedLiquidation);
                    fillBound = Math.Max(fillBound, envelope.ApplyFill(a, b, order.Type, input.Bars[a.BarIndex], quantity, entry, margin, forced));
                    fillError = Math.Max(fillError, Math.Abs(a.Price - b.Price));
                    if (a.Side == OrderSide.Buy) { quantity = a.Quantity; entry = a.Price; margin = quantity * entry * costs.InitialMarginRatio; }
                    else { quantity = 0m; entry = 0m; margin = 0m; }
                }
                RequireTrades(referenceResult, actualResult, input.Bars);
                errors.Add(envelope.Verify(expected.Snapshot, actual.Snapshot, referenceResult.Trades.Sum(t => t.ClosedGross), actualResult.Trades.Sum(t => t.ClosedGross),
                    referenceResult.Trades.Sum(t => t.ClosedNet), actualResult.Trades.Sum(t => t.ClosedNet), referenceResult.Fills.Sum(f => f.Commission), actualResult.Fills.Sum(f => f.Commission),
                    referenceResult.Fills.Sum(f => f.SlippageAmount), actualResult.Fills.Sum(f => f.SlippageAmount), fillError, fillBound));
                fills = referenceResult.Fills.Length;
                referenceState = expected.Snapshot; identicalState = identical.Snapshot; actualState = actual.Snapshot;
                decisions++;
            }
            if (!referenceState.Terminated || !identicalState.Terminated || !actualState.Terminated) throw new InvalidDataException("Incomplete financial parity episode.");
            evidence.Add(new(block.Id, errors));
        }
        return new(transitions, starts, new(RlFinancialParityContract.SpecificationId, source.Revision, RlContinuousContract.ContractHash,
            ContentHash.OfFile(Path.Combine(directory, RlFiles.Model)), decisions, decisions, decisions, evidence));
    }

    internal static void RequireExactJson(JsonElement expected, JsonElement actual, string context)
    {
        if (expected.ValueKind != actual.ValueKind) throw new InvalidDataException(context + ": value type differs.");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (expected.EnumerateObject().Count() != actual.EnumerateObject().Count()) throw new InvalidDataException(context + ": missing/extra field.");
            foreach (var field in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(field.Name, out var value)) throw new InvalidDataException(context + ": missing " + field.Name);
                RequireExactJson(field.Value, value, context + "." + field.Name);
            }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength()) throw new InvalidDataException(context + ": array length differs.");
            for (int i = 0; i < expected.GetArrayLength(); i++) RequireExactJson(expected[i], actual[i], context);
        }
        else if (expected.ValueKind == JsonValueKind.String ? expected.GetString() != actual.GetString() : expected.GetRawText() != actual.GetRawText())
            throw new InvalidDataException(context + ": exact value differs.");
    }

    internal static void RequireLifecycle(RlStepResult expected, RlStepResult actual)
    {
        var a = expected.Snapshot; var b = actual.Snapshot;
        if (expected.Action != actual.Action || expected.Outcome != actual.Outcome || expected.RejectionReason != actual.RejectionReason
            || expected.Terminated != actual.Terminated || expected.Truncated != actual.Truncated || a.TerminalReason != b.TerminalReason || a.Status != b.Status
            || a.MarketAnchorBarIndex != b.MarketAnchorBarIndex || a.MarketAnchor != b.MarketAnchor || a.TerminalEventBarIndex != b.TerminalEventBarIndex
            || a.CompletedDecisions != b.CompletedDecisions || a.RemainingDecisions != b.RemainingDecisions || a.HeldBars != b.HeldBars
            || a.PendingEntry != b.PendingEntry || a.PendingExit != b.PendingExit || a.DeferredMarketExit != b.DeferredMarketExit
            || a.Quantity != b.Quantity || a.EntryPrice != b.EntryPrice
            || a.EquityPoint.BarIndex != b.EquityPoint.BarIndex || a.EquityPoint.Timestamp != b.EquityPoint.Timestamp || a.EquityPoint.MarketValue != b.EquityPoint.MarketValue)
            throw new InvalidDataException("Layer C event/admission/terminal lifecycle differs.");
    }

    private static void RequireOrders(BacktestResult expected, BacktestResult actual, IReadOnlyDictionary<long, RlDecimalInterval> stops)
    {
        if (expected.Orders.Length != actual.Orders.Length) throw new InvalidDataException("Actor changed order count.");
        for (int i = 0; i < expected.Orders.Length; i++)
        {
            var a = expected.Orders[i]; var b = actual.Orders[i];
            if (a.Type == OrderType.Stop)
            {
                if (!stops.TryGetValue(a.OrderId, out var range) || !a.StopPrice.HasValue || !b.StopPrice.HasValue
                    || !range.Contains(a.StopPrice.Value) || !range.Contains(b.StopPrice.Value)) throw new InvalidDataException("Stop order price escaped its entry interval.");
                b = b with { StopPrice = a.StopPrice };
            }
            if (a != b || a.Reason != b.Reason) throw new InvalidDataException("Actor changed order lifecycle/quantity/admission.");
        }
    }

    internal static void RequireTrades(BacktestResult expected, BacktestResult actual, IReadOnlyList<CandleData> bars)
    {
        if (expected.Trades.Length != actual.Trades.Length) throw new InvalidDataException("Actor changed trade events.");
        for (int i = 0; i < expected.Trades.Length; i++)
        {
            var a = expected.Trades[i]; var b = actual.Trades[i];
            if (a.TradeId != b.TradeId || a.Side != b.Side || a.EntryBar != b.EntryBar || a.EntryTime != b.EntryTime || a.EntryPrice != b.EntryPrice
                || a.ExitBar != b.ExitBar || a.ExitTime != b.ExitTime || a.Quantity != b.Quantity || a.EntryFee != b.EntryFee || a.ExitFee != b.ExitFee
                || a.HoldingBars != b.HoldingBars || a.IsForcedLiquidation != b.IsForcedLiquidation || a.EntrySignal != b.EntrySignal || a.ExitReason != b.ExitReason)
                throw new InvalidDataException("Actor changed trade admission/time/fees/events.");
            // Every trade must agree with its individually bounded fills; cumulative sums alone can hide offsetting corruption.
            RequireTradeLedger(a, expected, bars);
            RequireTradeLedger(b, actual, bars);
        }
    }

    private static void RequireTradeLedger(BacktestTrade trade, BacktestResult result, IReadOnlyList<CandleData> bars)
    {
        var entry = result.Fills.Single(f => f.BarIndex == trade.EntryBar && f.Side == OrderSide.Buy);
        var exit = result.Fills.Single(f => f.BarIndex == trade.ExitBar && f.Side == OrderSide.Sell);
        decimal gross = trade.Quantity * (exit.Price - entry.Price);
        decimal high = entry.Price, low = entry.Price;
        // T16 enters at Open. The market exit observes its fill only; a stop exit observes that bar's extremes too.
        for (int i = trade.EntryBar; i < trade.ExitBar; i++)
        { high = Math.Max(high, bars[i].High); low = Math.Min(low, bars[i].Low); }
        if (trade.IsForcedLiquidation || result.Orders.Single(o => o.OrderId == exit.OrderId).Type != OrderType.Market)
        { high = Math.Max(high, bars[trade.ExitBar].High); low = Math.Min(low, bars[trade.ExitBar].Low); }
        high = Math.Max(high, exit.Price); low = Math.Min(low, exit.Price);
        if (trade.EntryTime != entry.FillTime || trade.ExitTime != exit.FillTime
            || trade.Quantity != entry.Quantity || trade.Quantity != exit.Quantity
            || trade.EntryPrice != entry.Price || trade.ExitPrice != exit.Price
            || trade.EntryFee != entry.Commission || trade.ExitFee != exit.Commission
            || trade.EntrySlippage != entry.SlippageAmount || trade.ExitSlippage != exit.SlippageAmount
            || trade.ClosedGross != gross || trade.ClosedNet != gross - entry.Commission - exit.Commission
            || trade.MfePrice != Math.Max(0m, high - entry.Price) || trade.MaePrice != Math.Max(0m, entry.Price - low))
            throw new InvalidDataException("Individual trade monetary values contradict the bounded entry/exit fills.");
    }

    private static void RequireSavedAccount(JsonElement saved, RlSessionSnapshot s)
    {
        foreach (var pair in new Dictionary<string, decimal> { ["cash"] = s.EquityPoint.Cash, ["equity"] = s.EquityPoint.Equity,
            ["held_margin"] = s.EquityPoint.HeldMargin, ["quantity"] = s.Quantity, ["entry_price"] = s.EntryPrice, ["unrealized_pnl"] = s.UnrealizedPnL,
            ["peak_equity"] = s.PeakEquity, ["drawdown"] = s.Drawdown, ["maximum_drawdown"] = s.MaximumDrawdown, ["retained_stop_distance"] = s.RetainedStopDistance })
            if (Decimal(saved, pair.Key) != pair.Value) throw new InvalidDataException("Saved financial account field differs: " + pair.Key);
        if (saved.GetProperty("held_bars").GetInt32() != s.HeldBars || saved.GetProperty("pending_entry").GetBoolean() != s.PendingEntry
            || saved.GetProperty("pending_exit").GetBoolean() != s.PendingExit || saved.GetProperty("deferred_market_exit").GetBoolean() != s.DeferredMarketExit
            || (saved.GetProperty("active_stop_price").ValueKind == JsonValueKind.Null ? (decimal?)null : Decimal(saved, "active_stop_price")) != s.ActiveStopPrice
            || !saved.GetProperty("fields").EnumerateArray().Select(v => RlEvaluation.Parse(v.GetString()!)).SequenceEqual(s.AccountFields))
            throw new InvalidDataException("Saved financial account state/stop/observation fields differ.");
    }

    private static void RequireSavedFills(JsonElement saved, BacktestFill[] fills)
    {
        if (saved.GetArrayLength() != fills.Length) throw new InvalidDataException("Saved fill count differs.");
        for (int i = 0; i < fills.Length; i++)
        {
            var a = fills[i]; var b = saved[i];
            if (a.BarIndex != b.GetProperty("bar_index").GetInt32() || a.FillTime != b.GetProperty("fill_time").GetDateTime()
                || a.Side.ToString() != b.GetProperty("side").GetString() || a.Quantity != Decimal(b, "quantity") || a.Price != Decimal(b, "price")
                || a.Commission != Decimal(b, "commission") || a.SlippageAmount != Decimal(b, "slippage_amount"))
                throw new InvalidDataException("Saved fill financial value/time differs.");
        }
    }
}
