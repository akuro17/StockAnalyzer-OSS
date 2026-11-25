using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>The two kinds of structure shift that share one state machine.</summary>
public enum StructureShiftKind
{
    /// <summary>Break of Structure: a close beyond the last unconsumed swing in the direction of the trend (the trend continues).</summary>
    Bos,

    /// <summary>Change of Character: a close beyond the protected swing against the trend (the trend flips).</summary>
    Choch,
}

/// <summary>
/// One shift found by <see cref="StructureShiftEngine.Analyze"/>: the bar of the break, its kind and direction (+1 bullish, -1 bearish), the swing that was broken and the protected swing that
/// the trend has after the shift (a swing is the bar index and the price of its pivot).
/// </summary>
public readonly record struct StructureShiftEvent(int Bar, StructureShiftKind Kind, int Direction, int SwingBar, decimal SwingPrice, int ProtectedSwingBar, decimal ProtectedSwingPrice);

/// <summary>The state machine of one run: the shifts in order, the trend state (+1, -1, 0 = not decided yet) and the bar of the protected swing (-1 = none) after every bar.</summary>
public sealed record StructureShiftAnalysis(IReadOnlyList<StructureShiftEvent> Events, int[] TrendState, int[] ProtectedSwingBar);

/// <summary>
/// Break of Structure and Change of Character (<c>02_Market_Structure\01_break_of_structure_bos.md</c>, <c>02_change_of_character_choch.md</c>; plan ChartPatterns56 v2, run V2-5; ledger
/// <c>sa_pattern_literal_ledger_V2-5_BosChoch.md</c>).
/// <para>
/// Model P: ONE causal pass over the whole series with a trend state (+1 up, -1 down, 0 undecided) and a protected swing, shared by both indicators. The swings are the confirmed ZigZag pivots
/// (<see cref="CausalZigZag"/>), used by a bar only when <c>ConfirmedAtIndex &lt; t</c> (plan R-32). At every bar:
/// </para>
/// <list type="bullet">
/// <item>Undecided: a close beyond the latest confirmed swing high (low) decides the trend up (down); it is not a signal. The decision is held until an opposite swing exists (R-22); the protected swing is the latest confirmed opposite swing.</item>
/// <item>In a trend, the protected swing is checked first: a close beyond it against the trend is a Change of Character (structure width, margin and optional volume must hold); it flips the trend.</item>
/// <item>Otherwise a close beyond the latest unconsumed swing in the trend direction, not older than <c>MaxPivotAgeBars</c>, is a Break of Structure; the broken swing is consumed and the protected swing moves to the lowest (highest) opposite swing after it (R-23).
/// The age limit applies to the Break of Structure only (R-24).</item>
/// </list>
/// A break consumes the broken swing and every open swing of the same kind that the close has passed (interpretation S-2), so a swing never fires twice. An inconsistent bar is no break bar and is not read by a margin,
/// width or volume test that spans it (R-16); it never changes the state.
/// </summary>
public static class StructureShiftEngine
{
    /// <summary>Runs the state machine and returns the result of the shifts of <paramref name="kind"/>, one per bar (the indicator makes the bars before the minimum history null).</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, CoreStructureShiftParameter p, StructureShiftKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        StructureShiftAnalysis analysis = Analyze(ctx, p, cancellationToken);

        // The key is the swing that was used: it fires once (the machine consumes it as well; the timeline is the second guard and the carrier of the persistence).
        var timeline = new PatternEventTimeline<(int Direction, int SwingBar)>(ctx.Count);
        foreach (StructureShiftEvent e in analysis.Events)
        {
            if (e.Kind == kind)
            {
                timeline.TryFire((e.Direction, e.SwingBar), e.Direction, e.Bar, p.SignalPersistenceBars, null, e.Bar, out _);
            }
        }

        PatternBarResult[] results = timeline.Resolve(PatternEngineSupport.AllowsBullish(p.DirectionMode), PatternEngineSupport.AllowsBearish(p.DirectionMode));
        PatternEngineSupport.ClearInconsistentBars(ctx, results);
        return results;
    }

    /// <summary>The single pass over the series: every shift of both kinds, the trend state and the protected swing after each bar. The direction mode is not applied here (the state always follows both sides).</summary>
    public static StructureShiftAnalysis Analyze(PatternSeriesContext ctx, CoreStructureShiftParameter p, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        IReadOnlyList<ConfirmedPivot> pivots = CausalZigZag.Extract(ctx, new CausalZigZagOptions(p.ZigZagThresholdPercent, p.MinSwingAtrMultiplier, PatternSpecConstants.StructureShiftFractalBars));
        var machine = new Machine(ctx, p, pivots);
        var trend = new int[ctx.Count];
        var protectedBar = new int[ctx.Count];

        for (int t = 0; t < ctx.Count; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            machine.Step(t);
            trend[t] = machine.Trend;
            protectedBar[t] = machine.ProtectedSwingBar;
        }

        return new StructureShiftAnalysis(machine.Events, trend, protectedBar);
    }

    private sealed class Machine
    {
        private readonly PatternSeriesContext _ctx;
        private readonly CoreStructureShiftParameter _p;
        private readonly IReadOnlyList<ConfirmedPivot> _pivots;

        // Indices into _pivots of the confirmed swings (ConfirmedAtIndex < t) that are not consumed, oldest first.
        private readonly List<int> _openHighs = new();
        private readonly List<int> _openLows = new();
        private int _known;
        private int _protected = -1;

        public Machine(PatternSeriesContext ctx, CoreStructureShiftParameter p, IReadOnlyList<ConfirmedPivot> pivots)
        {
            _ctx = ctx;
            _p = p;
            _pivots = pivots;
        }

        public List<StructureShiftEvent> Events { get; } = new();

        public int Trend { get; private set; }

        public int ProtectedSwingBar => _protected < 0 ? -1 : _pivots[_protected].Index;

        public void Step(int t)
        {
            // Spec 2 / plan R-32: a swing is used from the bar AFTER its confirmation.
            while (_known < _pivots.Count && _pivots[_known].ConfirmedAtIndex < t)
            {
                (_pivots[_known].IsHigh ? _openHighs : _openLows).Add(_known);
                _known++;
            }

            if (!_ctx.IsValid(t))
            {
                return;
            }

            if (Trend == 0)
            {
                Decide(t);
            }
            else if (!TryChoch(t))
            {
                TryBos(t);
            }
        }

        /// <summary>The list of open swings that a close in direction <paramref name="side"/> breaks: highs for an up close, lows for a down close.</summary>
        private List<int> OpenSwings(int side) => side > 0 ? _openHighs : _openLows;

        /// <summary>Break margin of the level (plan R-20); null when it cannot be computed.</summary>
        private decimal? Margin(decimal level, int t)
            => PatternMath.BreakMargin(_ctx, _p.BreakoutMarginPercent, PatternSpecConstants.StructureShiftBreakoutAtrMultiplier, level, t);

        /// <summary>True when the close of bar <paramref name="t"/> is beyond <paramref name="level"/> in direction <paramref name="side"/> by more than the margin (strict).</summary>
        private bool Beyond(int side, decimal level, int t)
        {
            decimal? margin = Margin(level, t);
            return margin is not null && side * (_ctx[t].Close - level) > margin.Value;
        }

        private bool VolumeMet(int t)
            => PatternMath.VolumeConfirmed(_ctx, _ctx[t].Volume, t - 1, _p.RequireVolumeConfirmation, _p.BreakoutVolumeMultiplier);

        /// <summary>A break in direction <paramref name="side"/> consumes the swing that was broken and every open swing of the same kind that the close passed (interpretation S-2).</summary>
        private void Consume(int side, decimal close)
            => OpenSwings(side).RemoveAll(i => side * (close - _pivots[i].Price) > 0m);

        /// <summary>Undecided state (spec 2, R-22): the first close beyond the latest confirmed swing decides the direction, once an opposite swing exists; no signal.</summary>
        private void Decide(int t)
        {
            for (int side = 1; side >= -1; side -= 2)
            {
                List<int> swings = OpenSwings(side);
                if (swings.Count == 0 || !Beyond(side, _pivots[swings[^1]].Price, t))
                {
                    continue;
                }

                int protection = LatestKnown(wantHigh: side < 0);
                if (protection < 0)
                {
                    // R-22: held. The reference swing stays unconsumed and is looked at again on the next bar.
                    continue;
                }

                Trend = side;
                _protected = protection;
                Consume(side, _ctx[t].Close);
                return;
            }
        }

        /// <summary>Change of Character (spec 02 section 5): a close beyond the protected swing against the trend flips the trend. Returns true when it fired.</summary>
        private bool TryChoch(int t)
        {
            ConfirmedPivot prot = _pivots[_protected];
            int side = -Trend;
            if (!Beyond(side, prot.Price, t) || !StructureWidthMet(t) || !VolumeMet(t))
            {
                return false;
            }

            int broken = _protected;
            Consume(side, _ctx[t].Close);
            Trend = side;

            // S-3: the new protected swing is the extreme opposite swing after the broken one; none confirmed yet: the latest confirmed one.
            int next = ProtectionAfter(broken, side);
            _protected = next >= 0 ? next : LatestKnown(wantHigh: side < 0);
            Events.Add(new StructureShiftEvent(t, StructureShiftKind.Choch, side, prot.Index, prot.Price, _pivots[_protected].Index, _pivots[_protected].Price));
            return true;
        }

        /// <summary>Break of Structure (spec 01 section 5): a close beyond the latest unconsumed swing in the trend direction, within the age limit, keeps the trend and moves the protected swing.</summary>
        private void TryBos(int t)
        {
            int side = Trend;
            List<int> swings = OpenSwings(side);
            if (swings.Count == 0)
            {
                return;
            }

            int reference = swings[^1];
            ConfirmedPivot swing = _pivots[reference];

            // R-24: the age limit is a limit of the Break of Structure only.
            if (t - swing.ConfirmedAtIndex > _p.MaxPivotAgeBars || !Beyond(side, swing.Price, t) || !VolumeMet(t))
            {
                return;
            }

            Consume(side, _ctx[t].Close);

            // R-23: the lowest (highest) opposite swing after the broken one; none: the protected swing stays.
            int next = ProtectionAfter(reference, side);
            if (next >= 0)
            {
                _protected = next;
            }

            Events.Add(new StructureShiftEvent(t, StructureShiftKind.Bos, side, swing.Index, swing.Price, _pivots[_protected].Index, _pivots[_protected].Price));
        }

        /// <summary>
        /// Spec 02 section 4 (U2-10 d): the structure width of the protected swing, the price distance to the pivot confirmed just before it, must be at least 2.0 ATR_ctx(t-1, 14).
        /// No earlier pivot or no ATR: not met.
        /// </summary>
        private bool StructureWidthMet(int t)
        {
            if (_protected <= 0 || !PatternMath.TryAtrCtx(_ctx, t - 1, PatternSpecConstants.AtrPeriod, out decimal atr, out _))
            {
                return false;
            }

            decimal width = Math.Abs(_pivots[_protected - 1].Price - _pivots[_protected].Price);
            return width >= PatternSpecConstants.StructureShiftChochWidthAtrMultiplier * atr;
        }

        /// <summary>
        /// R-23: after a break in direction <paramref name="side"/> of the swing <paramref name="broken"/> (a list index), the protected swing is the most extreme opposite swing (lowest low for +1, highest high for -1)
        /// that came after it and is confirmed. Equal prices: the later pivot. -1 when there is none.
        /// </summary>
        private int ProtectionAfter(int broken, int side)
        {
            int best = -1;
            for (int k = _known - 1; k >= 0 && _pivots[k].Index > _pivots[broken].Index; k--)
            {
                ConfirmedPivot candidate = _pivots[k];
                if (candidate.IsHigh == (side < 0) && (best < 0 || side * (_pivots[best].Price - candidate.Price) > 0m))
                {
                    best = k;
                }
            }

            return best;
        }

        /// <summary>The latest confirmed swing high (<paramref name="wantHigh"/>) or low, or -1.</summary>
        private int LatestKnown(bool wantHigh)
        {
            for (int k = _known - 1; k >= 0; k--)
            {
                if (_pivots[k].IsHigh == wantHigh)
                {
                    return k;
                }
            }

            return -1;
        }
    }
}
