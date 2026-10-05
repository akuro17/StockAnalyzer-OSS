using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The pure transition rules of producer-attested evidence (no I/O, no storage). Given a forecast's latest outcome and ONE
/// higher-sequence snapshot it returns the outcome revision to append, or null when the forecast's state does not change
/// (a "no-op" that still advances the stream head). Existing label, window and vote rules are reused, never restated.
/// </summary>
internal static class AttestedMonitoringReducer
{
    /// <summary>
    /// Matured -> Pending(ProviderFinalityRevoked) on an explicit revocation or tombstone inside the used window;
    /// Pending/conflict -> Matured and Matured -> Matured (changed content) when every bar a..a+h is Final;
    /// otherwise null. Unknown, absent, short or empty evidence never withdraws and never matures.
    /// </summary>
    public static PredictionOutcomeRevision? Decide(MonitoredPrediction p, PredictionOutcomeRevision latest,
        AttestedMonitoringSnapshot snapshot, MonitoringAnchorIndex rowByAnchor)
    {
        if (latest.State == PredictionOutcomeState.Unavailable) return null; // terminal: later evidence never revives it
        bool matured = latest.State == PredictionOutcomeState.Matured;
        int anchor = rowByAnchor.Find(p.AnchorUtc);
        int end = anchor >= 0 && p.HorizonBars < snapshot.Candles.Length - anchor ? anchor + p.HorizonBars : -1;

        // The window the forecast actually uses: its recorded label end when matured, else the target row if present.
        DateTimeOffset? usedEnd = matured ? latest.LabelEndUtc : end >= 0 ? PredictionLogService.Utc(snapshot.Candles[end].Timestamp) : null;
        if (usedEnd is { } hi && Revoked(snapshot, p.AnchorUtc, hi))
            return matured
                ? new PredictionOutcomeRevision(checked(latest.Revision + 1), latest.SourceRevision, PredictionOutcomeState.Pending,
                    snapshot.ObservedUtc, DatasetRevision: snapshot.SourceRevision, PendingReason: PendingReasons.ProviderFinalityRevoked)
                : null;

        if (end < 0) return null;
        for (int i = anchor; i <= end; i++)
            if (snapshot.Evidence[i].Finality != BarFinality.Final) return null;
        if (PredictionLogService.Utc(snapshot.Candles[end].Timestamp) > snapshot.ObservedUtc) return null;

        var targetBars = snapshot.Candles.AsSpan(anchor, p.HorizonBars + 1).ToArray();
        var source = PredictionLogService.SourceRevision(targetBars);
        if (matured && latest.SourceRevision == source) return null; // same applicable content
        // The label is recomputed from the (possibly corrected) bars with the unchanged label function.
        var label = PredictionLogService.ActualLabel(targetBars[0].Close, targetBars[^1].Close, p.NeutralThreshold);
        return new PredictionOutcomeRevision(checked(latest.Revision + 1), source, PredictionOutcomeState.Matured, snapshot.ObservedUtc,
            PredictionLogService.Utc(targetBars[^1].Timestamp), label, DatasetRevision: snapshot.SourceRevision);
    }

    /// <summary>The Pending(EvidenceConflict) revision for a forecast that currently votes; null when it is not Matured.</summary>
    public static PredictionOutcomeRevision? Conflict(PredictionOutcomeRevision latest, AttestedMonitoringSnapshot snapshot) =>
        latest.State != PredictionOutcomeState.Matured ? null
            : new PredictionOutcomeRevision(checked(latest.Revision + 1), latest.SourceRevision, PredictionOutcomeState.Pending,
                snapshot.ObservedUtc, DatasetRevision: snapshot.SourceRevision, PendingReason: PendingReasons.EvidenceConflict);

    /// <summary>True when any bar in [from, to] is explicitly revoked or any tombstone anchor lies in [from, to].</summary>
    private static bool Revoked(AttestedMonitoringSnapshot snapshot, DateTimeOffset from, DateTimeOffset to)
    {
        if (snapshot.Tombstones.Any(t => t.AnchorUtc >= from && t.AnchorUtc <= to)) return true;
        // Evidence is ordered by anchor: binary-search the first bar at or after 'from', then walk the window.
        int low = 0, high = snapshot.Evidence.Length;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (snapshot.Evidence[mid].AnchorUtc < from) low = mid + 1; else high = mid;
        }
        for (int i = low; i < snapshot.Evidence.Length && snapshot.Evidence[i].AnchorUtc <= to; i++)
            if (snapshot.Evidence[i].Finality == BarFinality.ExplicitlyRevoked) return true;
        return false;
    }
}
