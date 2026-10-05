using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Reporting projection from completed Core operations, never a second accountant or a policy selector.</summary>
public static class RlConstraintEvaluation
{
    public static RlContinuousReadout Readout(RlSessionSnapshot account, BacktestConfiguration costs,
        RlContinuousAction proposal, RlAction policyAction, string policyId, string directionId)
    {
        RlEntryControl? control = account.Quantity > 0m
            ? account.RetainedEntrySizeRatio is { } ratio && account.RetainedStopDistance > 0m ? new(ratio, account.RetainedStopDistance, true) : null
            : policyId == RlContinuousContract.Target ? new(proposal.SizeRatio, proposal.StopDistance, false)
                : new(RlContinuousContract.MaximumSizeRatio, RlContinuousContract.BaselineStopDistance, false);
        bool eligible = account.Quantity == 0m && !account.PendingEntry && !account.DeferredMarketExit && policyAction == RlAction.Buy;
        decimal? quantity = eligible && control is not null
            ? BacktestEngine.ComputeEntryQuantity(costs, account.EquityPoint.Equity, account.MarketWindow[^1].Close, control.SizeRatio) : null;
        string state = account.DeferredMarketExit || (account.Quantity > 0m && account.PendingExit && account.ActiveStopPrice is null)
            ? "Delayed" : account.Quantity > 0m ? "Filled" : eligible ? "Planned" : "Ignored";
        return new(proposal, quantity, eligible && quantity >= 1m && quantity <= RlContract.MaxPositionQuantity
            ? account.MarketWindow[^1].Close * (1m - control!.StopDistance) : null, account.ActiveStopPrice, state, directionId)
            { SelectedPolicyControl = control, PolicyAction = policyAction };
    }
    public static JsonElement ToWire<T>(T value) => JsonSerializer.SerializeToElement(value, RlFinancialParityContract.TraceJson);
    public static RlEntryConstraintSummary Summarize(IEnumerable<RlEntryExecution> attempts)
    {
        var rows = attempts.ToArray();
        int zero = rows.Count(r => r.RequestedQuantity == 0m), cap = rows.Count(r => r.RejectionReason == RejectedReason.QuantityCapExceeded);
        return new(rows.Length, zero, cap, rows.Count(r => r.RejectionReason == RejectedReason.InsufficientFunds), rows.Count(r => r.FilledQuantity > 0m),
            rows.Length == 0 ? RlEntryConstraintStatus.NotApplicable : cap == rows.Length ? RlEntryConstraintStatus.NoAdmittedBuyBecauseOfCap
                : cap > 0 ? RlEntryConstraintStatus.CapConstrained : RlEntryConstraintStatus.Unconstrained);
    }

    public static RlExitCostSensitivity ExitSensitivity(decimal quantity, decimal close, decimal equity, BacktestConfiguration costs)
    {
        if (quantity < 0m) throw new InvalidDataException("R04 sensitivity requires a long/flat account.");
        decimal? price = quantity == 0m ? null : FillPricing.ApplySlippage(close, OrderSide.Sell, costs.SlippageRatio);
        decimal fee = quantity == 0m ? 0m : FillPricing.ComputeCommission(costs.CommissionFlat, costs.CommissionPerUnit, quantity);
        decimal adjusted = quantity == 0m ? equity : equity - quantity * (close - price!.Value) - fee;
        return new(close, price, fee, adjusted, (adjusted - costs.InitialCapital) / costs.InitialCapital);
    }

    public static RlConstraintEpisode Finish(RlSessionSnapshot state, BacktestResult result, IReadOnlyList<RlEntryExecution> attempts)
    {
        if (!state.Terminated) throw new InvalidOperationException("Constraint evaluation requires natural termination.");
        // A forced intrabar terminal account can retain the previous observation Close. Flat sensitivity reads no new bar.
        decimal close = state.MarketWindow[^1].Close;
        return new(RlConstraintContract.SpecificationId, Summarize(attempts), attempts.ToImmutableArray(), state.Quantity,
            state.MaximumDrawdown, state.EquityPoint.Equity, ExitSensitivity(state.Quantity, close, state.EquityPoint.Equity, result.Configuration));
    }

    public static RlConstraintEpisode ReadEpisode(RlEpisodeEvaluation row)
        => row.Constraints?.Deserialize<RlConstraintEpisode>(RlFinancialParityContract.TraceJson)
            ?? throw new InvalidDataException("Missing versioned constraint episode.");

    public static RlEvaluationCoverage Coverage(RlSourceSnapshot source, IReadOnlyList<RlEpisodeEvaluation> validation, IReadOnlyList<RlEpisodeEvaluation> oos)
    {
        var coverage = ImmutableArray.CreateBuilder<RlPartitionCoverage>();
        void Partition(string name, int start, int end, ImmutableArray<RlEpisodeBlock> selected, IReadOnlyList<RlEpisodeEvaluation> evaluated)
        {
            int possible = Math.Max(0, end - start) / RlContract.SupportLength;
            int excluded = source.Catalog.Excluded.Count(s => s.StartsWith(name + ":", StringComparison.Ordinal));
            coverage.Add(new(name, possible, selected.Length, excluded, possible - selected.Length - excluded,
                Math.Max(0, end - start) % RlContract.SupportLength, evaluated.Count, evaluated.Sum(e => e.CompletedDecisions)));
        }
        Partition("train", 0, source.Catalog.TrainCut, source.Catalog.Train, Array.Empty<RlEpisodeEvaluation>());
        Partition("validation", source.Catalog.TrainCut + RlContract.WindowLength, source.Catalog.ValidationCut, source.Catalog.Validation, validation);
        Partition("oos", source.Catalog.ValidationCut + RlContract.WindowLength, source.Bars.Length, source.Catalog.Oos, oos);
        return new(source.Bars[0].Timestamp, source.Bars[^1].Timestamp, source.Bars.Length,
            source.Bars[RlContract.WindowLength - 1].Timestamp, source.Bars[^2].Timestamp,
            source.Bars.Length - RlContract.WindowLength, coverage.ToImmutable());
    }

    public static RlConstraintReport Create(RlSourceSnapshot source, IReadOnlyList<RlEpisodeEvaluation> validation,
        IReadOnlyList<RlEpisodeEvaluation> oos, IReadOnlyList<RlEpisodeEvaluation> validationBaseline,
        IReadOnlyList<RlEpisodeEvaluation> oosBaseline, RlTemporalBoundStatus temporal, JsonElement baselines)
    {
        var deltas = ImmutableArray.CreateBuilder<RlPairedPolicyDelta>();
        void Pair(IReadOnlyList<RlEpisodeEvaluation> actor, IReadOnlyList<RlEpisodeEvaluation> baseline)
        {
            if (actor.Count != baseline.Count) throw new InvalidDataException("Paired policy block counts differ.");
            for (int i = 0; i < actor.Count; i++)
            {
                if (actor[i].BlockId != baseline[i].BlockId || actor[i].Partition != baseline[i].Partition)
                    throw new InvalidDataException("Paired policy comparisons require the same chronological blocks.");
                decimal a = RlEvaluation.Parse(actor[i].TerminalReturn), b = RlEvaluation.Parse(baseline[i].TerminalReturn);
                deltas.Add(new(actor[i].BlockId, actor[i].Partition, a, b, a - b));
            }
        }
        Pair(validation, validationBaseline); Pair(oos, oosBaseline);
        var summaries = ImmutableArray.CreateBuilder<RlPolicyConstraintSummary>();
        foreach (var partition in baselines.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            foreach (var policy in partition.Value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var rows = policy.Value.TryGetProperty("seeds", out var seeds)
                    ? seeds.EnumerateArray().SelectMany(s => s.GetProperty("episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!).ToArray()
                    : policy.Value.GetProperty("episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!;
                summaries.Add(new(partition.Name, policy.Name, rows.Length, Summarize(rows.SelectMany(r => ReadEpisode(r).Attempts))));
            }
        return new(RlConstraintContract.SpecificationId, source.Revision, RlContinuousContract.ContractHash, Coverage(source, validation, oos),
            Summarize(validation.SelectMany(e => ReadEpisode(e).Attempts)), Summarize(oos.SelectMany(e => ReadEpisode(e).Attempts)),
            deltas.ToImmutable(), summaries.ToImmutable(), validation.Concat(oos).Select(r => new RlTerminalMarkSummary(r.BlockId, r.Partition,
                RlEvaluation.Parse(r.ResidualQuantity), RlEvaluation.Parse(r.TerminalReturn), RlEvaluation.Parse(r.MaximumDrawdown),
                ReadEpisode(r).ExitCostSensitivity.AdjustedReturn)).ToImmutableArray(),
            RlConstraintContract.SelectionMetric, RlContract.Gamma, RlContract.EpisodeHorizon,
            RlSourceContract.Unverified, "FinancialParityVerified", temporal, RlConstraintContract.InvestmentEfficacy);
    }

    public static void ValidateEpisode(RlEpisodeEvaluation row, RlEpisodeBlock block, RlSourceSnapshot source, BacktestConfiguration costs)
    {
        var episode = ReadEpisode(row);
        if (episode.SpecificationId != RlConstraintContract.SpecificationId || episode.Attempts.IsDefault
            || episode.Attempts.Length > row.CompletedDecisions || episode.Entries != Summarize(episode.Attempts)
            || episode.ResidualQuantity != RlEvaluation.Parse(row.ResidualQuantity)
            || episode.CloseMarkMaximumDrawdown != RlEvaluation.Parse(row.MaximumDrawdown)
            || (episode.MarkEquity - costs.InitialCapital) / costs.InitialCapital != RlEvaluation.Parse(row.TerminalReturn))
            throw new InvalidDataException("Constraint episode identity/count/mark evidence differs.");
        int previous = -1;
        foreach (var attempt in episode.Attempts)
        {
            int index = block.StartIndex + RlContract.WindowLength - 1 + attempt.Decision;
            if (attempt.Decision <= previous || attempt.Decision < 0 || attempt.Decision >= row.CompletedDecisions
                || attempt.DecisionTime != source.Bars[index].Timestamp || attempt.DecisionClose != source.Bars[index].Close
                || attempt.SizeRatio < 0m || attempt.SizeRatio > RlContinuousContract.MaximumSizeRatio
                || attempt.StopDistance < RlContinuousContract.MinimumStopDistance
                || attempt.StopDistance > RlContinuousContract.MinimumStopDistance
                    + (decimal)(RlContinuousContract.ActionMaximum - RlContinuousContract.ActionMinimum) * RlContinuousContract.StopCoefficient
                || attempt.DecisionEquity <= 0m || attempt.CashBeforeEntry != attempt.DecisionEquity
                || attempt.DecisionBudget != attempt.DecisionEquity * attempt.SizeRatio
                || attempt.RequestedQuantity != BacktestEngine.ComputeEntryQuantity(costs, attempt.DecisionEquity, attempt.DecisionClose, attempt.SizeRatio)
                || attempt.FilledQuantity < 0m || (attempt.FilledQuantity != 0m && attempt.FilledQuantity != attempt.RequestedQuantity)
                || attempt.FilledQuantity > RlContract.MaxPositionQuantity || attempt.ExecutableQuantity != attempt.FilledQuantity)
                throw new InvalidDataException("Eligible Buy decision/quantity/budget evidence differs.");
            previous = attempt.Decision;
            if (attempt.RequestedQuantity == 0m)
            {
                if (attempt.Outcome != RlActionOutcome.NoEntry || attempt.RejectionReason is not null || attempt.OrderId is not null)
                    throw new InvalidDataException("Zero proposal was reported as an admitted order.");
            }
            else if (attempt.RequestedQuantity > RlContract.MaxPositionQuantity)
            {
                if (attempt.RejectionReason != RejectedReason.QuantityCapExceeded || attempt.Outcome != RlActionOutcome.Rejected)
                    throw new InvalidDataException("Quantity cap rejection evidence differs.");
            }
            else if (attempt.FilledQuantity == 0m)
            {
                decimal price = FillPricing.ApplySlippage(source.Bars[index + 1].Open, OrderSide.Buy, costs.SlippageRatio);
                decimal required = attempt.RequestedQuantity * price * costs.InitialMarginRatio
                    + FillPricing.ComputeCommission(costs.CommissionFlat, costs.CommissionPerUnit, attempt.RequestedQuantity);
                if (attempt.Outcome != RlActionOutcome.Rejected || attempt.RejectionReason != RejectedReason.InsufficientFunds
                    || required <= attempt.CashBeforeEntry)
                    throw new InvalidDataException("Insufficient funds rejection evidence differs.");
            }
            if (attempt.FilledQuantity == 0m)
            {
                if (attempt.FillPrice is not null || attempt.FilledValue != 0m || attempt.HeldMargin != 0m || attempt.Commission != 0m)
                    throw new InvalidDataException("Rejected/no-entry evidence invented a fill.");
            }
            else if (attempt.FillPrice != FillPricing.ApplySlippage(source.Bars[index + 1].Open, OrderSide.Buy, costs.SlippageRatio)
                || attempt.FilledValue != attempt.FilledQuantity * attempt.FillPrice || attempt.HeldMargin != attempt.FilledValue * costs.InitialMarginRatio
                || attempt.Commission != FillPricing.ComputeCommission(costs.CommissionFlat, costs.CommissionPerUnit, attempt.FilledQuantity)
                || attempt.HeldMargin + attempt.Commission > attempt.CashBeforeEntry || attempt.RejectionReason is not null || attempt.Outcome != RlActionOutcome.Submitted)
                throw new InvalidDataException("Actual entry monetary/admission evidence differs.");
        }
        decimal close = episode.ExitCostSensitivity.TerminalClose;
        if (episode.ResidualQuantity > 0m && close != source.Bars[block.StartIndex + RlContract.WindowLength - 1 + row.CompletedDecisions].Close
            || episode.ExitCostSensitivity != ExitSensitivity(episode.ResidualQuantity, close, episode.MarkEquity, costs))
            throw new InvalidDataException("Evaluation-only final Close sensitivity differs.");
    }

    public static void ValidateReport(string directory, JsonElement job, JsonElement report, IReadOnlyDictionary<string, string> metadata,
        RlSourceSnapshot source, BacktestConfiguration costs)
    {
        bool versioned = job.TryGetProperty(RlConstraintContract.MetadataKey, out var version);
        if (versioned != metadata.ContainsKey(RlConstraintContract.MetadataKey)
            || versioned != report.TryGetProperty("constraint_diagnostics", out var saved))
            throw new InvalidDataException("Constraint diagnostic evidence was downgraded or version bindings differ.");
        if (!versioned) return;
        if (version.GetString() != RlConstraintContract.SpecificationId || metadata[RlConstraintContract.MetadataKey] != RlConstraintContract.SpecificationId)
            throw new InvalidDataException("Unsupported constraint diagnostic evidence version.");
        RlEpisodeEvaluation[] Rows(JsonElement rows, IReadOnlyList<RlEpisodeBlock> blocks)
        {
            var parsed = rows.Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json) ?? throw new InvalidDataException("Missing constraint episodes.");
            if (parsed.Length != blocks.Count) throw new InvalidDataException("Constraint evaluation block counts differ.");
            for (int i = 0; i < parsed.Length; i++)
            {
                if (parsed[i].BlockId != blocks[i].Id || parsed[i].Partition != blocks[i].Partition)
                    throw new InvalidDataException("Constraint block order/identity differs.");
                ValidateEpisode(parsed[i], blocks[i], source, costs);
            }
            return parsed;
        }
        var validation = Rows(report.GetProperty("validation_episodes"), source.Catalog.Validation);
        var oos = Rows(report.GetProperty("oos_episodes"), source.Catalog.Oos);
        foreach (var candidate in report.GetProperty("candidate_evaluations").EnumerateArray())
            Rows(candidate.GetProperty("summary").GetProperty("episodes"), source.Catalog.Validation);
        RlEpisodeEvaluation[] Baseline(string partition, IReadOnlyList<RlEpisodeBlock> blocks)
        {
            if (blocks.Count == 0) return Array.Empty<RlEpisodeEvaluation>();
            var policies = report.GetProperty("baselines").GetProperty(partition);
            foreach (var policy in policies.EnumerateObject())
            {
                Rows(policy.Value.GetProperty("episodes"), blocks);
                if (policy.Value.TryGetProperty("seeds", out var seeds))
                    foreach (var seed in seeds.EnumerateArray()) Rows(seed.GetProperty("episodes"), blocks);
            }
            return Rows(policies.GetProperty(RlContinuousContract.PairedBaselineId).GetProperty("episodes"), blocks);
        }
        var bound = JsonSerializer.Deserialize<RlTemporalEvidence>(File.ReadAllText(Path.Combine(directory, RlTemporalContract.DirectionEvidenceFile)), RlFiles.Json)!;
        var expected = Create(source, validation, oos, Baseline("validation", source.Catalog.Validation), Baseline("oos", source.Catalog.Oos), bound.Status, report.GetProperty("baselines"));
        RlContinuousParity.RequireExactJson(ToWire(expected), saved, "Constraint report coverage/paired deltas/status");
        var selected = report.GetProperty("candidate_evaluations").EnumerateArray().Single(c =>
            c.GetProperty("seed").GetInt32() == report.GetProperty("candidate_seed").GetInt32()
            && c.GetProperty("checkpoint_step").GetInt32() == report.GetProperty("checkpoint_step").GetInt32())
            .GetProperty("summary").GetProperty("episodes");
        for (int i = 0; i < validation.Length; i++)
            RlContinuousParity.RequireExactJson(report.GetProperty("validation_episodes")[i].GetProperty("constraints"),
                selected[i].GetProperty("constraints"), "Selected constraint evidence");
    }
}
