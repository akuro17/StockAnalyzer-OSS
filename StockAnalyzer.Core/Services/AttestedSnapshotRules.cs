using System;
using System.Globalization;
using System.IO;
using System.Text;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The structural contract of a producer snapshot, checked before any write, and its content digest. The same rules guard the
/// manifest loader and the monitoring store, so a snapshot built by any other source meets exactly the same contract.
/// </summary>
internal static class AttestedSnapshotRules
{
    public static void Validate(AttestedMonitoringSnapshot? snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.InstrumentId))
            throw new ArgumentException("An instrument id is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.ProviderId) || !Enum.IsDefined(snapshot.Timeframe) || snapshot.RevisionEpoch < 0
            || !PredictionMonitoringReducer.ValidHash(snapshot.SourceRevision)
            || snapshot.ObservedUtc == default || snapshot.ObservedUtc.Offset != TimeSpan.Zero || snapshot.ObservedUtc > now
            || snapshot.Candles.IsDefault || snapshot.Evidence.IsDefault || snapshot.Tombstones.IsDefault
            || snapshot.Candles.Length != snapshot.Evidence.Length)
            throw new InvalidDataException("The attested snapshot identity, time, or shape is invalid.");
        PredictionMonitoringReducer.ValidateChronology(snapshot.Candles);
        foreach (var candle in snapshot.Candles)
            if (!candle.IsValid())
                throw new InvalidDataException("The attested snapshot contains an invalid OHLCV bar.");
        if (snapshot.SourceRevision != PredictionLogService.SourceRevision(snapshot.Candles))
            throw new InvalidDataException("The attested snapshot is not bound to its bars.");

        DateTimeOffset? lastEnd = null;
        for (int i = 0; i < snapshot.Evidence.Length; i++)
        {
            var bar = snapshot.Evidence[i];
            if (bar is null || !Enum.IsDefined(bar.Finality) || bar.AnchorUtc != PredictionLogService.Utc(snapshot.Candles[i].Timestamp)
                || bar.PeriodStartUtc.Offset != TimeSpan.Zero || bar.PeriodEndUtc.Offset != TimeSpan.Zero
                || bar.AvailableAtUtc.Offset != TimeSpan.Zero || bar.AnchorUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Bar evidence is not aligned with its bar or is not UTC.");
            // start < end <= available <= observed; equality of end, available and observed is accepted.
            if (bar.PeriodStartUtc >= bar.PeriodEndUtc || bar.PeriodEndUtc > bar.AvailableAtUtc
                || bar.AvailableAtUtc > snapshot.ObservedUtc || lastEnd > bar.PeriodStartUtc)
                throw new InvalidDataException("Bar evidence has invalid chronology, overlap or future availability.");
            lastEnd = bar.PeriodEndUtc;
        }

        DateTimeOffset? lastTombstone = null;
        var evidenceAnchors = new System.Collections.Generic.HashSet<DateTimeOffset>();
        foreach (var bar in snapshot.Evidence) evidenceAnchors.Add(bar.AnchorUtc);
        foreach (var tombstone in snapshot.Tombstones)
        {
            if (tombstone is null || tombstone.AnchorUtc.Offset != TimeSpan.Zero || tombstone.AvailableAtUtc.Offset != TimeSpan.Zero
                || tombstone.AnchorUtc == default || lastTombstone >= tombstone.AnchorUtc || evidenceAnchors.Contains(tombstone.AnchorUtc)
                || tombstone.AvailableAtUtc > snapshot.ObservedUtc)
                throw new InvalidDataException("Tombstones must be strictly ordered, absent from the bar evidence and available by the observation.");
            lastTombstone = tombstone.AnchorUtc;
        }
    }

    /// <summary>
    /// Digest of the snapshot CONTENT: identity, epoch, sequence, source revision, every evidence field and every tombstone.
    /// The observation instant is not part of it, so a re-delivery of the same sequence is recognised as the same content.
    /// </summary>
    public static string Digest(AttestedMonitoringSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{snapshot.ProviderId.Length}:{snapshot.ProviderId}|{snapshot.InstrumentId.Length}:{snapshot.InstrumentId}|{(int)snapshot.Timeframe}|{snapshot.RevisionEpoch}|{snapshot.ProviderSequence}|{snapshot.SourceRevision}\n");
        foreach (var bar in snapshot.Evidence)
            text.Append(CultureInfo.InvariantCulture, $"B|{bar.AnchorUtc:O}|{bar.PeriodStartUtc:O}|{bar.PeriodEndUtc:O}|{bar.AvailableAtUtc:O}|{(int)bar.Finality}\n");
        foreach (var tombstone in snapshot.Tombstones)
            text.Append(CultureInfo.InvariantCulture, $"T|{tombstone.AnchorUtc:O}|{tombstone.AvailableAtUtc:O}|{tombstone.Reason?.Length ?? -1}:{tombstone.Reason}\n");
        return PredictionMonitoringReducer.Hash(text.ToString());
    }
}
