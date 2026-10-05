using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Explicit, quiescent import of the V1 JSON monitoring files into monitoring.db. The caller guarantees that no old or new
/// monitoring writer runs. Every source file is read once, validated with the shared reducer rules, and imported in ONE
/// transaction together with the completion marker; any corrupt or inconsistent input aborts the whole import and leaves no
/// marker. Source files are never modified, renamed or deleted.
/// </summary>
internal static class PredictionMonitoringMigrator
{
    internal sealed record ImportSummary(int Predictions, int Outcomes, int Health, int Alerts, string ManifestDigest);

    private static T ParseSource<T>(byte[] bytes, string path)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, TrainingConfigJson.Options) ?? throw new InvalidDataException($"'{path}' is empty."); }
        catch (JsonException ex) { throw new InvalidDataException($"'{path}' is malformed.", ex); }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Digest of every outcome revision's identity (forecast, revision, state, source revision), in key order.</summary>
    private static string ContentDigest(IEnumerable<(string Id, long Revision, long State, string Source)> outcomes) =>
        PredictionMonitoringReducer.Hash(string.Join("\n", outcomes.OrderBy(o => o.Id, StringComparer.Ordinal).ThenBy(o => o.Revision)
            .Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Id}:{o.Revision}:{o.State}:{o.Source}"))));

    /// <summary>Returns the summary, or null when a complete marker already exists (nothing is re-imported or re-voted).</summary>
    public static async Task<ImportSummary?> ImportAsync(SqliteMonitoringStore store, DateTimeOffset importedUtc, CancellationToken ct)
    {
        var root = store.Root;
        var manifest = new StringBuilder();
        var audits = new List<PredictionAuditRecord>();
        var predictionsDirectory = Path.Combine(root, "predictions");
        if (Directory.Exists(predictionsDirectory))
            foreach (var path in Directory.EnumerateFiles(predictionsDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                manifest.Append("predictions/").Append(Path.GetFileName(path)).Append('\n').Append(Sha256(bytes)).Append('\n');
                var record = ParseSource<PredictionAuditRecord>(bytes, path);
                PredictionMonitoringReducer.ValidateAudit(record, Path.GetFileNameWithoutExtension(path));
                audits.Add(record);
            }
        var health = new List<ModelMonitoringSnapshot>();
        var healthPath = Path.Combine(root, "health.json");
        if (File.Exists(healthPath))
        {
            var bytes = await File.ReadAllBytesAsync(healthPath, ct).ConfigureAwait(false);
            manifest.Append("health.json\n").Append(Sha256(bytes)).Append('\n');
            health = ParseSource<List<ModelMonitoringSnapshot>>(bytes, healthPath);
            PredictionMonitoringReducer.ValidateHealth(health);
        }
        var alerts = new List<ModelDecayAlert>();
        var alertsDirectory = Path.Combine(root, "alerts");
        if (Directory.Exists(alertsDirectory))
            foreach (var path in Directory.EnumerateFiles(alertsDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                manifest.Append("alerts/").Append(Path.GetFileName(path)).Append('\n').Append(Sha256(bytes)).Append('\n');
                var alert = ParseSource<ModelDecayAlert>(bytes, path);
                if (string.IsNullOrWhiteSpace(alert.AlertId) || alert.AlertId != Path.GetFileNameWithoutExtension(path) || alert.Snapshot is null)
                    throw new InvalidDataException($"Alert '{path}' does not match its identity.");
                PredictionMonitoringReducer.ValidateHealth([alert.Snapshot]);
                alerts.Add(alert);
            }
        if (alerts.Select(a => a.AlertId).Distinct(StringComparer.Ordinal).Count() != alerts.Count)
            throw new InvalidDataException("Alert identities are duplicated.");
        var manifestDigest = PredictionMonitoringReducer.Hash(manifest.ToString());

        // The saved health must be reproducible from the audits at its own evaluation instant; otherwise the import is refused.
        var digestByGroup = new Dictionary<(string, string), string>();
        foreach (var instant in health.Select(h => h.EvaluatedUtc).Distinct())
        {
            var votes = new PredictionMonitoringReducer.VoteAggregator(instant, withVoteDigest: true);
            foreach (var audit in audits) votes.Add(audit.Prediction, audit.Outcomes[^1]);
            var groups = votes.Groups().ToDictionary(g => g.Key, g => g.Votes);
            foreach (var saved in health.Where(h => h.EvaluatedUtc == instant))
            {
                if (!groups.TryGetValue((saved.ModelId, saved.TargetContractHash), out var recomputed)
                    || recomputed.Count != saved.MaturedCount || recomputed.Correct != saved.CorrectCount
                    || PredictionMonitoringPolicy.ComputeAccuracy(recomputed.Count, recomputed.Correct) != saved.Accuracy
                    || PredictionMonitoringPolicy.ComputeHealthState(recomputed.Count, recomputed.Correct) != saved.State)
                    throw new InvalidDataException($"Saved health of {saved.ModelId}/{saved.TargetContractHash} cannot be reproduced from the audits at {instant:O}.");
                digestByGroup[(saved.ModelId, saved.TargetContractHash)] = recomputed.VoteDigest;
            }
        }

        await using var connection = await store.OpenAsync(requireInitialized: false, ct).ConfigureAwait(false);
        using var transaction = store.BeginWriter(connection, ct);
        await SqliteMonitoringStore.CreateSchemaAsync(connection, transaction, ct).ConfigureAwait(false);
        if ((await SqliteMonitoringStore.ReadMetadataAsync(connection, transaction, ct).ConfigureAwait(false)).ContainsKey(SqliteMonitoringStore.CompleteKey))
            return null;
        foreach (var audit in audits)
        {
            await SqliteMonitoringStore.InsertPredictionAsync(connection, transaction, audit.Prediction, ct).ConfigureAwait(false);
            foreach (var outcome in audit.Outcomes)
                await SqliteMonitoringStore.InsertOutcomeAsync(connection, transaction, audit.Prediction.PredictionId, outcome, ct).ConfigureAwait(false);
        }
        foreach (var snapshot in health)
            await SqliteMonitoringStore.UpsertHealthAsync(connection, transaction,
                new HealthRow(snapshot, 1, digestByGroup[(snapshot.ModelId, snapshot.TargetContractHash)]), ct).ConfigureAwait(false);
        // Historical alerts keep their GUID ids and payloads, a NULL revision, and are Presented: they are never replayed.
        foreach (var alert in alerts)
            await SqliteMonitoringStore.InsertAlertAsync(connection, transaction, alert, null, presented: true, ct).ConfigureAwait(false);

        int outcomeCount = audits.Sum(a => a.Outcomes.Length);
        if (await SqliteMonitoringStore.CountAsync(connection, transaction, "Predictions", ct).ConfigureAwait(false) != audits.Count
            || await SqliteMonitoringStore.CountAsync(connection, transaction, "Outcomes", ct).ConfigureAwait(false) != outcomeCount
            || await SqliteMonitoringStore.CountAsync(connection, transaction, "Health", ct).ConfigureAwait(false) != health.Count
            || await SqliteMonitoringStore.CountAsync(connection, transaction, "Alerts", ct).ConfigureAwait(false) != alerts.Count)
            throw new InvalidDataException("The imported row counts differ from the source files.");
        var stored = new List<(string, long, long, string)>();
        foreach (var audit in audits)
            foreach (var latest in (await SqliteMonitoringStore.ReadAuditAsync(connection, transaction, audit.Prediction.PredictionId, ct).ConfigureAwait(false))!.Outcomes)
                stored.Add((audit.Prediction.PredictionId, latest.Revision, (long)latest.State, latest.SourceRevision));
        if (ContentDigest(stored) != ContentDigest(audits.SelectMany(a => a.Outcomes.Select(o => (a.Prediction.PredictionId, o.Revision, (long)o.State, o.SourceRevision)))))
            throw new InvalidDataException("The imported outcome revisions differ from the source files.");

        await SqliteMonitoringStore.SetMetadataAsync(connection, transaction, SqliteMonitoringStore.VersionKey,
            SqliteMonitoringStore.FormatVersion.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await SqliteMonitoringStore.SetMetadataAsync(connection, transaction, SqliteMonitoringStore.ManifestKey, manifestDigest, ct).ConfigureAwait(false);
        await SqliteMonitoringStore.SetMetadataAsync(connection, transaction, SqliteMonitoringStore.CompleteKey,
            importedUtc.UtcTicks.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        store.Commit(transaction, ct);
        return new ImportSummary(audits.Count, outcomeCount, health.Count, alerts.Count, manifestDigest);
    }
}
