using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Real signal-generating strategy driven by a user-defined list of comparison conditions (see
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.3), reversing the P3 Gate G7
/// deferral that shipped as <see cref="NoOpBacktestStrategy"/> — that class is left completely
/// untouched; this is a new, separate strategy selected instead of it once at least one condition
/// entry exists (Task 5).
///
/// Each <see cref="BacktestConditionEntry"/> carries a <see cref="BacktestConditionEntry.Role"/>
/// (EntryOnly/ExitOnly/Both). The ordered entry list is filtered into independently left-to-right-chained
/// sub-sequences. While flat, a true entry chain returns LongEntry/ShortEntry; while holding a position,
/// a true exit chain returns LongExit/ShortExit matching the held side; otherwise null. At most one order
/// is ever returned per bar from <see cref="Evaluate"/>, matching
/// <see cref="IBacktestStrategy.Evaluate"/>'s single-nullable-return contract.
///
/// <see cref="BacktestConditionSide.OutputName"/> support (Task 6a, 2026-09-18, see
/// Y:\Temp\sa_implementation_plan_BacktestOutputNameSupport.md): a side naming a non-Main series of a
/// multi-series indicator (e.g. MACD's "Signal" line) is honored end to end — <see cref="RegisterSide"/>
/// includes <see cref="BacktestConditionSide.OutputName"/> in its dedupe key and passes it through to the
/// <see cref="StrategyIndicatorRequest"/> it builds, and <c>BacktestEngine.PrepareIndicators</c> extracts
/// that named series instead of always Main. This corrects an earlier gap: this class originally shipped
/// (Task 3) without <c>OutputName</c> in the dedupe key, because <see cref="StrategyIndicatorRequest"/>
/// had no <c>OutputName</c> field to plumb through at the time.
///
/// <see cref="BacktestConditionEntry.Position"/> / same-bar reversal support (Task 2, 2026-09-19, corrected
/// 2026-09-19 per user direction, see
/// Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md sections 2.2/3.3): v1's long-only
/// entry resolution (always LongEntry) is retired in favor of resolving LongEntry/ShortEntry from each
/// EntryOnly/Both entry's own <see cref="BacktestConditionEntry.Position"/> — this is the "small, additive
/// follow-up" the class's original comment already anticipated. The reversal capability itself (Entry in
/// Position's direction + Exit of the opposite side, same bar) is NOT implemented by overloading
/// <see cref="BacktestConditionRole.Both"/> with a second, runtime-conditional meaning — an initial version
/// did this, but the user asked for the more maintainable alternative once the tradeoff was surfaced: a
/// dedicated <see cref="BacktestConditionRole.Reversal"/> role carries the reversal semantics exclusively,
/// so <see cref="BacktestConditionRole.Both"/> keeps its pre-existing side-agnostic generic exit-chain
/// membership completely unconditionally (no new branching on <c>PositionSide</c> at all), preserving
/// <c>ConditionBasedBacktestStrategyTests.BothRole_SingleConditionDrivesEntryThenImmediateExit</c> and every
/// already-saved config using Both for its original simple "enter/exit off one boolean" purpose exactly as
/// before (plan section 3.1/3.4, user's function-reuse policy section 3.0, preserving existing behavior). A
/// <see cref="BacktestConditionRole.Reversal"/> entry never joins the generic exit chain; while flat or
/// while already holding its own Position's side it behaves like <see cref="BacktestConditionRole.EntryOnly"/>
/// (entry contribution only); while holding the OPPOSITE side, it requests opening its own Position's
/// direction via <see cref="Evaluate"/> and, independently, closing the held opposite side via the new
/// <see cref="EvaluateExit"/> override — the "Both button, Position selected -> Entry=Position,
/// Exit=opposite" behavior the user originally requested (the UI's Both button now emits this role).
///
/// Entry-price-relative Stop-Loss/Take-Profit (Task 8b, 2026-09-19, see
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md sections 3.4/4.5): an optional
/// <see cref="BacktestRiskManagementSettings"/> constructor parameter, `null`/fully-disabled by default
/// (existing-behavior-preservation for every caller built before this feature). While holding a position,
/// <see cref="Evaluate"/> checks this bar's High/Low against the entry-relative threshold BEFORE the
/// reversal check and the generic exit chain — a risk-management breach takes priority over opening a
/// same-bar reversal or firing an ordinary condition-based exit, since it is the strategy's own capital-
/// protection mechanism, not just another source of the same signal. Per section 3.4.3's RESOLVED
/// decision, a same-bar double-breach (both thresholds touched by one volatile bar) always resolves as
/// Stop-Loss, never Take-Profit — the conservative, worst-case assumption. The returned request uses
/// `OrderType.Stop`/`.Limit` with the exact breached threshold price so the engine's existing gap-aware
/// worse-of/better-of-vs-Open fill rule applies unchanged (Task 8a's finding: no engine change needed).
/// </summary>
public sealed class ConditionBasedBacktestStrategy : IBacktestStrategy
{
    private static readonly TimeInForce Gtc = new(true, -1);

    private readonly IReadOnlyList<BacktestConditionEntry> _longEntryChain;
    private readonly IReadOnlyList<BacktestConditionEntry> _shortEntryChain;
    private readonly IReadOnlyList<BacktestConditionEntry> _exitChain;
    private readonly IReadOnlyList<BacktestConditionEntry> _reversalLongChain;
    private readonly IReadOnlyList<BacktestConditionEntry> _reversalShortChain;
    private readonly IReadOnlyList<StrategyIndicatorRequest> _requiredIndicators;
    private readonly IReadOnlyDictionary<BacktestConditionSide, string> _sideToIndicatorKey;
    private readonly BacktestRiskManagementSettings? _riskManagement;
    private readonly BacktestConditionEntry[] _entries;

    public ConditionBasedBacktestStrategy(IReadOnlyList<BacktestConditionEntry> entries, BacktestRiskManagementSettings? riskManagement = null)
    {
        ImmutableArray<BacktestConditionEntry> snapshot = BacktestConditionValidator.Snapshot(entries);
        BacktestConditionValidator.ValidateRiskManagement(riskManagement);

        BacktestConditionExecutionPaths paths = BacktestConditionExecutionPaths.Create(snapshot);
        _longEntryChain = paths.LongEntry;
        _shortEntryChain = paths.ShortEntry;
        _exitChain = paths.Exit;
        _reversalLongChain = paths.ReverseLong;
        _reversalShortChain = paths.ReverseShort;
        (_requiredIndicators, _sideToIndicatorKey) = BuildIndicatorRequests(snapshot);
        _riskManagement = riskManagement is null ? null : new BacktestRiskManagementSettings
        {
            StopLossPercent = riskManagement.StopLossPercent,
            TakeProfitPercent = riskManagement.TakeProfitPercent,
        };
        _entries = snapshot.ToArray();
    }

    public string Name => "ConditionBased";

    /// <summary>The configured condition entries in their original order (read-only view; consumed by the run fingerprint's strategy manifest).</summary>
    public IReadOnlyList<BacktestConditionEntry> Entries => _entries;

    /// <summary>The configured entry-price-relative stop-loss / take-profit settings, or null when none were supplied.</summary>
    public BacktestRiskManagementSettings? RiskManagement => _riskManagement;

    public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => _requiredIndicators;

    public StrategyOrderRequest? Evaluate(StrategyContext context)
    {
        if (context.PositionSide is null)
        {
            if (_longEntryChain.Count > 0 &&
                BacktestConditionEvaluator.Evaluate(_longEntryChain, _sideToIndicatorKey, context.Indicators, context.BarIndex))
            {
                return new StrategyOrderRequest(SignalType.LongEntry, OrderType.Market, null, null, Gtc, "Condition entry chain matched");
            }
            if (_shortEntryChain.Count > 0 &&
                BacktestConditionEvaluator.Evaluate(_shortEntryChain, _sideToIndicatorKey, context.Indicators, context.BarIndex))
            {
                return new StrategyOrderRequest(SignalType.ShortEntry, OrderType.Market, null, null, Gtc, "Condition entry chain matched");
            }
            return null;
        }

        // Risk management (Task 8b, section 4.5): a Stop-Loss/Take-Profit breach takes priority over both
        // the reversal check and the generic exit chain below - checked first, every bar, while holding.
        if (_riskManagement is { IsEnabled: true } && EvaluateRiskManagementBreach(context) is { } riskExit)
        {
            return riskExit;
        }

        // Reversal (plan section 2.2 third row): a Reversal entry whose own Position is the OPPOSITE of
        // the side currently held opens a new position this same bar; the matching close of the held side
        // is reported independently by EvaluateExit. A Reversal entry never contributes to the unchanged
        // generic exit chain below, regardless of which side is held.
        if (context.PositionSide == TradeSide.Long && _reversalShortChain.Count > 0 &&
            BacktestConditionEvaluator.Evaluate(_reversalShortChain, _sideToIndicatorKey, context.Indicators, context.BarIndex))
        {
            return new StrategyOrderRequest(SignalType.ShortEntry, OrderType.Market, null, null, Gtc, "Reversal: opening Short");
        }
        if (context.PositionSide == TradeSide.Short && _reversalLongChain.Count > 0 &&
            BacktestConditionEvaluator.Evaluate(_reversalLongChain, _sideToIndicatorKey, context.Indicators, context.BarIndex))
        {
            return new StrategyOrderRequest(SignalType.LongEntry, OrderType.Market, null, null, Gtc, "Reversal: opening Long");
        }

        if (_exitChain.Count > 0 &&
            BacktestConditionEvaluator.Evaluate(_exitChain, _sideToIndicatorKey, context.Indicators, context.BarIndex))
        {
            SignalType exitSignal = context.PositionSide == TradeSide.Long ? SignalType.LongExit : SignalType.ShortExit;
            return new StrategyOrderRequest(exitSignal, OrderType.Market, null, null, Gtc, "Condition exit chain matched");
        }

        return null;
    }

    /// <summary>
    /// New override (plan section 3.3): reports the Exit-type request that accompanies a same-bar
    /// Reversal (see <see cref="Evaluate"/>'s two reversal branches above) — closing the currently-held
    /// side so the freed margin can back the new entry <see cref="Evaluate"/> just requested. Returns null
    /// whenever no reversal is in effect this bar (the ordinary, unchanged exit path — including Both's
    /// own exit contribution — stays entirely in <see cref="Evaluate"/>). Called every bar from
    /// <c>BacktestEngine</c>'s bar loop immediately after <see cref="Evaluate"/>, independently filling the
    /// engine's separate Exit-type pending-order slot (dual pending-order slots, see
    /// Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md section 2.3).
    /// </summary>
    public StrategyOrderRequest? EvaluateExit(StrategyContext context)
    {
        if (context.PositionSide == TradeSide.Long && _reversalShortChain.Count > 0 &&
            BacktestConditionEvaluator.Evaluate(_reversalShortChain, _sideToIndicatorKey, context.Indicators, context.BarIndex))
        {
            return new StrategyOrderRequest(SignalType.LongExit, OrderType.Market, null, null, Gtc, "Reversal: closing Long before opening Short");
        }
        if (context.PositionSide == TradeSide.Short && _reversalLongChain.Count > 0 &&
            BacktestConditionEvaluator.Evaluate(_reversalLongChain, _sideToIndicatorKey, context.Indicators, context.BarIndex))
        {
            return new StrategyOrderRequest(SignalType.ShortExit, OrderType.Market, null, null, Gtc, "Reversal: closing Short before opening Long");
        }
        return null;
    }

    /// <summary>
    /// Task 8b breach check (section 3.4.1's RESOLVED intrabar High/Low detection basis, section 3.4.3's
    /// RESOLVED Stop-Loss-first tie-break): only called while holding a position with a non-null
    /// <see cref="StrategyContext.EntryPrice"/>. Long Stop-Loss breaches on <c>Bar.Low</c>, Take-Profit on
    /// <c>Bar.High</c>; Short is the mirror image. Checking Stop-Loss first and returning immediately on a
    /// match is what gives Stop-Loss priority on a same-bar double-breach - Take-Profit is never even
    /// evaluated once Stop-Loss has already matched.
    /// </summary>
    private StrategyOrderRequest? EvaluateRiskManagementBreach(StrategyContext context)
    {
        if (context.EntryPrice is not { } entryPrice) return null;

        CandleData bar = context.Bar;
        bool isLong = context.PositionSide == TradeSide.Long;
        SignalType exitSignal = isLong ? SignalType.LongExit : SignalType.ShortExit;

        if (_riskManagement!.StopLossPercent is { } stopLossPercent)
        {
            decimal stopPrice = EnsurePositiveThreshold(
                isLong ? checked(entryPrice * (1m - stopLossPercent)) : checked(entryPrice * (1m + stopLossPercent)),
                "Stop-Loss");
            bool breached = isLong ? bar.Low <= stopPrice : bar.High >= stopPrice;
            if (breached)
            {
                return new StrategyOrderRequest(exitSignal, OrderType.Stop, null, stopPrice, Gtc, "Stop-Loss breach");
            }
        }

        if (_riskManagement.TakeProfitPercent is { } takeProfitPercent)
        {
            decimal limitPrice = EnsurePositiveThreshold(
                isLong ? checked(entryPrice * (1m + takeProfitPercent)) : checked(entryPrice * (1m - takeProfitPercent)),
                "Take-Profit");
            bool breached = isLong ? bar.High >= limitPrice : bar.Low <= limitPrice;
            if (breached)
            {
                return new StrategyOrderRequest(exitSignal, OrderType.Limit, limitPrice, null, Gtc, "Take-Profit breach");
            }
        }

        return null;
    }

    private static decimal EnsurePositiveThreshold(decimal value, string name)
        => value > 0m ? value : throw new OverflowException($"{name} threshold rounded to a non-positive decimal value.");

    /// <summary>Dedupe by content — not by entry — per plan section 4.3: two conditions referencing the
    /// same (IndicatorType, Parameters content, OutputName, Frame, PriceSource) share one
    /// <see cref="StrategyIndicatorRequest"/>, even when they come from distinct
    /// <see cref="BacktestConditionSide"/> object instances. <c>OutputName</c> is part of the key (Task
    /// 6a) so two sides differing only by which named series they read (e.g. MACD's "Main" vs "Signal")
    /// are never collapsed onto the same request. <c>PriceSource</c> is part of the key too (SAで改善,
    /// Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md) so two "Price" catalog rows
    /// (e.g. "High" and "Low", both sharing <see cref="IndicatorType.Price"/>) never collapse onto the
    /// same request - without this they would silently compare a value against itself.</summary>
    private static (IReadOnlyList<StrategyIndicatorRequest>, IReadOnlyDictionary<BacktestConditionSide, string>) BuildIndicatorRequests(
        IReadOnlyList<BacktestConditionEntry> entries)
    {
        var dedup = new Dictionary<(IndicatorType Type, string ParametersKey, string OutputName, TimeFrame? Frame, PriceType? PriceSource), string>();
        var mapping = new Dictionary<BacktestConditionSide, string>();
        var requests = new List<StrategyIndicatorRequest>();

        foreach (BacktestConditionSide side in BacktestConditionValidator.ActiveSides(entries))
        {
            RegisterSide(side, dedup, mapping, requests);
        }

        return (requests, mapping);
    }

    private static void RegisterSide(
        BacktestConditionSide side,
        Dictionary<(IndicatorType Type, string ParametersKey, string OutputName, TimeFrame? Frame, PriceType? PriceSource), string> dedup,
        Dictionary<BacktestConditionSide, string> mapping,
        List<StrategyIndicatorRequest> requests)
    {
        (IndicatorType, string, string, TimeFrame?, PriceType?) dedupKey = (side.IndicatorType, ParametersContentKey(side.Parameters), side.OutputName, side.Frame, side.PriceSource);
        if (!dedup.TryGetValue(dedupKey, out string? key))
        {
            key = $"cond{dedup.Count}";
            dedup[dedupKey] = key;
            requests.Add(new StrategyIndicatorRequest(
                key,
                side.IndicatorType,
                side.Parameters?.Clone(),
                side.Frame,
                BacktestConditionValidator.NormalizeOutputName(side),
                side.PriceSource,
                StrictOutputName: true));
        }
        mapping[side] = key;
    }

    private static string ParametersContentKey(CoreIndicatorParameterBase? parameters)
        => parameters is null ? string.Empty : JsonSerializer.Serialize(parameters, parameters.GetType());
}
