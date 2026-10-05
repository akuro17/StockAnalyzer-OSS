using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>Producer-attested evidence of the transactional monitoring backend (stream head, audit, bindings, per-forecast decisions).</summary>
internal sealed partial class SqlitePredictionLogService
{
    private const string DispositionApplied = "Applied", DispositionConflict = "Conflict",
        DispositionLowerEpoch = "IgnoredLowerEpoch", DispositionLowerSequence = "IgnoredLowerSequence";

    /// <summary>The audit reason of a delivery whose evidence is incomplete: bars the producer cannot yet vouch for are never votes.</summary>
    internal static string? EvidenceGapReason(AttestedMonitoringSnapshot snapshot)
    {
        int unknown = snapshot.Evidence.Count(bar => bar.Finality == BarFinality.Unknown);
        return unknown == 0 ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"EvidenceGap:UnknownBars={unknown}");
    }

    /// <summary>
    /// Applies one producer snapshot in sequence order, in ONE transaction: stream head, evidence audit row, forecast
    /// bindings, new outcome revisions, health and the transition outbox row commit or roll back together. A lower
    /// (epoch, sequence) is ignored; the same sequence with the same content is a no-op; the same sequence with different
    /// content withdraws every current vote of the stream and cannot be undone by that sequence; a higher one is decided
    /// forecast by forecast with <see cref="AttestedMonitoringReducer"/>.
    /// </summary>
    public async Task RefreshAttestedAsync(AttestedMonitoringSnapshot snapshot, CancellationToken ct = default)
    {
        AttestedSnapshotRules.Validate(snapshot, _clock.GetUtcNow());
        var digest = AttestedSnapshotRules.Digest(snapshot);
        var rowByAnchor = MonitoringAnchorIndex.Create(snapshot.Candles);
        var stream = (snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe);
        var symbols = _attestedSources.Where(s => s.ProviderId == snapshot.ProviderId && s.InstrumentId == snapshot.InstrumentId
            && s.Timeframe == snapshot.Timeframe).Select(s => s.Symbol).Distinct(StringComparer.Ordinal).ToList();
        HealthEvaluation? evaluation = null;
        bool appended = false, conflict = false;
        string? gap = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            using var transaction = _store.BeginWriter(connection, ct);
            await SqliteMonitoringStore.EnsureAttestationSchemaAsync(connection, transaction, ct).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var head = await SqliteMonitoringStore.ReadStreamAsync(connection, transaction, snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe, ct).ConfigureAwait(false);
            if (head is not null && snapshot.RevisionEpoch < head.Epoch)
            {
                var reason = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"HeadEpoch={head.Epoch}");
                await SqliteMonitoringStore.InsertEvidenceAsync(connection, transaction, snapshot, digest, DispositionLowerEpoch, reason, ct).ConfigureAwait(false);
                _store.Commit(transaction, ct);
                _logger.LogInformation("Ignored attested delivery of {Provider}/{Instrument}/{Timeframe}: epoch {Epoch} is below the stream head ({Reason}).",
                    snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe, snapshot.RevisionEpoch, reason);
                return;
            }
            bool sameEpoch = head is not null && snapshot.RevisionEpoch == head.Epoch;
            if (sameEpoch && snapshot.ProviderSequence < head!.Sequence)
            {
                var reason = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"HeadSequence={head!.Sequence}");
                await SqliteMonitoringStore.InsertEvidenceAsync(connection, transaction, snapshot, digest, DispositionLowerSequence, reason, ct).ConfigureAwait(false);
                _store.Commit(transaction, ct);
                _logger.LogInformation("Ignored attested delivery of {Provider}/{Instrument}/{Timeframe}: sequence {Sequence} is below the stream head ({Reason}).",
                    snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe, snapshot.ProviderSequence, reason);
                return;
            }
            bool sameSequence = sameEpoch && snapshot.ProviderSequence == head!.Sequence;
            if (sameSequence && head!.Digest == digest) { _store.Commit(transaction, ct); return; } // idempotent re-delivery
            conflict = sameSequence;

            var bindings = await SqliteMonitoringStore.ReadBindingsAsync(connection, transaction, ct).ConfigureAwait(false);
            foreach (var symbol in symbols)
                foreach (var (p, latest) in await SqliteMonitoringStore.ReadLatestForSymbolAsync(connection, transaction, symbol, snapshot.Timeframe, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (latest.State == PredictionOutcomeState.Unavailable) continue;
                    if (bindings.TryGetValue(p.PredictionId, out var bound))
                    {
                        if (bound != stream) continue; // another authority: incomparable, never merged
                    }
                    else
                    {
                        // The explicit enrollment event: earlier history stays as it was, evidence applies from here on.
                        await SqliteMonitoringStore.InsertBindingAsync(connection, transaction, p.PredictionId, snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe, now, ct).ConfigureAwait(false);
                        bindings[p.PredictionId] = stream;
                    }
                    var outcome = conflict ? AttestedMonitoringReducer.Conflict(latest, snapshot)
                        : AttestedMonitoringReducer.Decide(p, latest, snapshot, rowByAnchor);
                    if (outcome is null) continue;
                    await SqliteMonitoringStore.InsertOutcomeAsync(connection, transaction, p.PredictionId, outcome, ct).ConfigureAwait(false);
                    appended = true;
                }
            await SqliteMonitoringStore.UpsertStreamAsync(connection, transaction, snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe,
                conflict ? head! with { Conflicted = true } : new AttestedStreamHead(snapshot.RevisionEpoch, snapshot.ProviderSequence, digest, false), now, ct).ConfigureAwait(false);
            gap = conflict ? null : EvidenceGapReason(snapshot);
            await SqliteMonitoringStore.InsertEvidenceAsync(connection, transaction, snapshot, digest, conflict ? DispositionConflict : DispositionApplied,
                conflict ? "SameSequenceDifferentContent" : gap, ct).ConfigureAwait(false);
            if (appended) evaluation = await EvaluateAsync(connection, transaction, now, ct).ConfigureAwait(false);
            _store.Commit(transaction, ct);
        }
        finally { _gate.Release(); }
        if (conflict)
            _logger.LogWarning("Attested stream {Provider}/{Instrument}/{Timeframe} delivered sequence {Sequence} twice with different content; its votes are withdrawn.",
                snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe, snapshot.ProviderSequence);
        else if (gap is not null)
            _logger.LogInformation("Attested delivery of {Provider}/{Instrument}/{Timeframe} sequence {Sequence} has an evidence gap ({Reason}); existing outcomes are retained.",
                snapshot.ProviderId, snapshot.InstrumentId, snapshot.Timeframe, snapshot.ProviderSequence, gap);
        if (appended || conflict) Changed?.Invoke(this, EventArgs.Empty);
        if (evaluation is not null) foreach (var alert in evaluation.Alerts) Decayed?.Invoke(this, alert);
    }

    public async Task<ImmutableArray<AttestedStreamFault>> GetConflictedStreamsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
            var streams = await SqliteMonitoringStore.ReadConflictedStreamsAsync(connection, ct).ConfigureAwait(false);
            return streams.Select(s => new AttestedStreamFault(s.Provider, s.Instrument, s.Timeframe)).ToImmutableArray();
        }
        finally { _gate.Release(); }
    }
}
