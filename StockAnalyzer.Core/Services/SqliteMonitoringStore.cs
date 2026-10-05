using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>A persisted per-group health row: the public snapshot plus its monotonic revision and the digest of the votes it represents.</summary>
internal sealed record HealthRow(ModelMonitoringSnapshot Snapshot, long Revision, string VoteDigest);

/// <summary>The head of one producer stream: the newest accepted (epoch, sequence), its content digest and whether that sequence is in conflict.</summary>
internal sealed record AttestedStreamHead(long Epoch, ulong Sequence, string Digest, bool Conflicted);

/// <summary>A pending notification as stored: stable id, payload and the committed group revision that ordered it.</summary>
internal sealed record PendingNotification(ModelDecayAlert Alert, long HealthRevision, long AttemptCount);

/// <summary>
/// The monitoring-only SQLite store (monitoring.db): one non-pooled connection per operation, WAL + synchronous=FULL +
/// foreign_keys=ON verified on every connection, writer transactions started with BEGIN IMMEDIATE, decimals as invariant TEXT,
/// instants as UTC ticks. It performs no business calculation; the shared <see cref="PredictionMonitoringReducer"/> owns that.
/// </summary>
internal sealed class SqliteMonitoringStore
{
    public const int FormatVersion = 1;
    public const string VersionKey = "Version", ManifestKey = "ImportManifestDigest", CompleteKey = "ImportComplete";
    // SQLite primary result codes for a database lock that was not obtained in time.
    private const int SqliteBusy = 5, SqliteLocked = 6;

    private static readonly JsonSerializerOptions PayloadOptions = new(TrainingConfigJson.Options) { WriteIndented = false };
    private readonly string _connectionString;
    private readonly int _busyMilliseconds;
    private readonly ILogger _logger;

    public string DatabasePath { get; }
    public string Root { get; }

    public SqliteMonitoringStore(string root, PredictionMonitoringSettings settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PredictionMonitoringSettingsValidator.Validate(settings); // warnings are reported once by the backend factory
        _logger = logger ?? NullLogger.Instance;
        Root = root;
        DatabasePath = Path.Combine(root, "monitoring.db");
        _busyMilliseconds = (int)settings.BusyTimeout.TotalMilliseconds;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(settings.BusyTimeout.TotalSeconds)),
        }.ToString();
    }

    public bool DatabaseExists => File.Exists(DatabasePath);

    // ---- connections, transactions, schema -----------------------------------------------------------------------

    /// <summary>Opens and verifies a connection. <paramref name="requireInitialized"/> rejects an empty or foreign-version file.</summary>
    public async Task<SqliteConnection> OpenAsync(bool requireInitialized, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureAsync(connection, ct).ConfigureAwait(false);
            var version = Convert.ToInt64(await ScalarAsync(connection, null, "PRAGMA user_version;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version > FormatVersion) throw new InvalidDataException($"The monitoring database format {version} is newer than this application supports.");
            if (requireInitialized && version != FormatVersion)
                throw new InvalidDataException("The monitoring database is not initialized; run the import first.");
            return connection;
        }
        catch (SqliteException ex) when (IsBusy(ex)) { await connection.DisposeAsync().ConfigureAwait(false); throw Busy("open the database", ex); }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task ConfigureAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecAsync(connection, null, $"PRAGMA busy_timeout = {_busyMilliseconds.ToString(CultureInfo.InvariantCulture)};", ct).ConfigureAwait(false);
        var mode = Convert.ToString(await ScalarAsync(connection, null, "PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new PredictionMonitoringStorageException(
                $"The monitoring database requires WAL journaling on a local filesystem, but '{mode}' is in effect at '{DatabasePath}'.");
        await ExecAsync(connection, null, "PRAGMA synchronous = FULL;", ct).ConfigureAwait(false);
        await ExecAsync(connection, null, "PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
        if (Convert.ToInt64(await ScalarAsync(connection, null, "PRAGMA synchronous;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 2
            || Convert.ToInt64(await ScalarAsync(connection, null, "PRAGMA foreign_keys;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
            throw new PredictionMonitoringStorageException("The monitoring database could not enforce synchronous=FULL and foreign keys.");
    }

    public static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is SqliteBusy or SqliteLocked;

    /// <summary>BEGIN IMMEDIATE: the writer lock is taken up front, so contention surfaces here as a typed busy fault.</summary>
    public SqliteTransaction BeginWriter(SqliteConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return connection.BeginTransaction(deferred: false); }
        catch (SqliteException ex) when (IsBusy(ex)) { throw Busy("start a writer transaction", ex); }
    }

    /// <summary>Commits after a last cancellation check. A failed commit leaves nothing committed and no event published.</summary>
    public void Commit(SqliteTransaction transaction, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { transaction.Commit(); }
        catch (SqliteException ex) when (IsBusy(ex)) { throw Busy("commit", ex); }
    }

    /// <summary>The typed busy fault, recorded first: contention is otherwise invisible to the operator.</summary>
    private PredictionMonitoringBusyException Busy(string operation, SqliteException cause)
    {
        _logger.LogWarning(cause, "The monitoring database was locked for longer than {Timeout} while trying to {Operation}.",
            TimeSpan.FromMilliseconds(_busyMilliseconds), operation);
        return new PredictionMonitoringBusyException(cause);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS StoreMetadata(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Predictions(
            PredictionId TEXT NOT NULL PRIMARY KEY, ModelId TEXT NOT NULL, TargetHash TEXT NOT NULL,
            Symbol TEXT NOT NULL, Timeframe INTEGER NOT NULL, PredictionJson TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_Predictions_SymbolTimeframe ON Predictions(Symbol, Timeframe);
        CREATE INDEX IF NOT EXISTS IX_Predictions_Group ON Predictions(ModelId, TargetHash);
        CREATE TABLE IF NOT EXISTS Outcomes(
            PredictionId TEXT NOT NULL REFERENCES Predictions(PredictionId), Revision INTEGER NOT NULL CHECK(Revision >= 0),
            OutcomeJson TEXT NOT NULL, State INTEGER NOT NULL CHECK(State IN (0, 1, 2)), LabelEndTicks INTEGER NULL,
            PRIMARY KEY(PredictionId, Revision));
        CREATE INDEX IF NOT EXISTS IX_Outcomes_PredictionRevisionDesc ON Outcomes(PredictionId, Revision DESC);
        CREATE INDEX IF NOT EXISTS IX_Outcomes_StateLabelEnd ON Outcomes(State, LabelEndTicks);
        CREATE TABLE IF NOT EXISTS Health(
            ModelId TEXT NOT NULL, TargetHash TEXT NOT NULL, HealthRevision INTEGER NOT NULL CHECK(HealthRevision >= 0),
            N INTEGER NOT NULL CHECK(N >= 0), K INTEGER NOT NULL CHECK(K >= 0 AND K <= N), AccuracyText TEXT NULL,
            State INTEGER NOT NULL CHECK(State IN (0, 1, 2)), EvaluatedTicks INTEGER NOT NULL, LastAlertId TEXT NULL,
            VoteDigest TEXT NOT NULL, PRIMARY KEY(ModelId, TargetHash));
        CREATE TABLE IF NOT EXISTS Alerts(
            AlertId TEXT NOT NULL PRIMARY KEY, ModelId TEXT NOT NULL, TargetHash TEXT NOT NULL, HealthRevision INTEGER NULL,
            PayloadJson TEXT NOT NULL, UNIQUE(ModelId, TargetHash, HealthRevision));
        CREATE TABLE IF NOT EXISTS Outbox(
            AlertId TEXT NOT NULL PRIMARY KEY REFERENCES Alerts(AlertId), State INTEGER NOT NULL CHECK(State IN (0, 1)),
            AttemptCount INTEGER NOT NULL CHECK(AttemptCount >= 0), LastError TEXT NULL);
        CREATE INDEX IF NOT EXISTS IX_Outbox_StateAlert ON Outbox(State, AlertId);
        """;

    /// <summary>Creates the V1 schema and stamps user_version inside the caller's transaction (rolled back with it).</summary>
    public static async Task CreateSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await ExecAsync(connection, transaction, Schema, ct).ConfigureAwait(false);
        await ExecAsync(connection, transaction, $"PRAGMA user_version = {FormatVersion};", ct).ConfigureAwait(false);
    }

    // ---- backend marker --------------------------------------------------------------------------------------------

    /// <summary>
    /// True when a completed import marker of the supported format exists. An absent/empty/unstamped file is "not migrated";
    /// a newer user_version or an unknown marker version fails instead of falling back to stale JSON.
    /// </summary>
    public async Task<bool> IsImportCompleteAsync(CancellationToken ct)
    {
        if (!DatabaseExists) return false;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var version = Convert.ToInt64(await ScalarAsync(connection, null, "PRAGMA user_version;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version > FormatVersion) throw new InvalidDataException($"The monitoring database format {version} is newer than this application supports.");
        if (await ScalarAsync(connection, null, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'StoreMetadata';", ct).ConfigureAwait(false) is null)
            return false;
        var metadata = await ReadMetadataAsync(connection, null, ct).ConfigureAwait(false);
        if (metadata.TryGetValue(VersionKey, out var stored) && stored != FormatVersion.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException($"The monitoring database format '{stored}' is not supported.");
        return metadata.ContainsKey(CompleteKey) && version == FormatVersion;
    }

    public static async Task<Dictionary<string, string>> ReadMetadataAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = Command(connection, transaction, "SELECT key, value FROM StoreMetadata;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public static Task SetMetadataAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string value, CancellationToken ct) =>
        ExecAsync(connection, transaction, "INSERT INTO StoreMetadata(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            ct, ("$k", key), ("$v", value));

    // ---- forecasts and outcome revisions -----------------------------------------------------------------------------

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, PayloadOptions);

    public static T Parse<T>(string json, string what)
    {
        try { return JsonSerializer.Deserialize<T>(json, PayloadOptions) ?? throw new InvalidDataException($"The stored {what} is empty."); }
        catch (JsonException ex) { throw new InvalidDataException($"The stored {what} is malformed.", ex); }
    }

    public static Task InsertPredictionAsync(SqliteConnection connection, SqliteTransaction transaction, MonitoredPrediction p, CancellationToken ct) =>
        ExecAsync(connection, transaction, "INSERT INTO Predictions(PredictionId, ModelId, TargetHash, Symbol, Timeframe, PredictionJson) VALUES($id, $m, $t, $s, $f, $j);",
            ct, ("$id", p.PredictionId), ("$m", p.ModelId), ("$t", p.TargetContractHash), ("$s", p.Symbol), ("$f", (long)p.Timeframe), ("$j", Serialize(p)));

    public static Task InsertOutcomeAsync(SqliteConnection connection, SqliteTransaction transaction, string predictionId, PredictionOutcomeRevision o, CancellationToken ct) =>
        ExecAsync(connection, transaction, "INSERT INTO Outcomes(PredictionId, Revision, OutcomeJson, State, LabelEndTicks) VALUES($id, $r, $j, $st, $l);",
            ct, ("$id", predictionId), ("$r", o.Revision), ("$j", Serialize(o)), ("$st", (long)o.State), ("$l", o.LabelEndUtc?.UtcTicks));

    private static MonitoredPrediction ReadPrediction(SqliteDataReader reader, int column, string id)
    {
        var p = Parse<MonitoredPrediction>(reader.GetString(column), "forecast");
        if (p.PredictionId != id) throw new InvalidDataException("A stored forecast does not match its key.");
        return p;
    }

    /// <summary>One forecast with every outcome revision, validated like a JSON audit file; null when absent.</summary>
    public static async Task<PredictionAuditRecord?> ReadAuditAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        MonitoredPrediction? prediction = null;
        await using (var command = Command(connection, transaction, "SELECT PredictionId, ModelId, TargetHash, Symbol, Timeframe, PredictionJson FROM Predictions WHERE PredictionId = $id;", ("$id", id)))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                prediction = ReadPrediction(reader, 5, id);
                if (prediction.ModelId != reader.GetString(1) || prediction.TargetContractHash != reader.GetString(2)
                    || prediction.Symbol != reader.GetString(3) || (long)prediction.Timeframe != reader.GetInt64(4))
                    throw new InvalidDataException("A stored forecast does not match its indexed columns.");
            }
        if (prediction is null) return null;
        var outcomes = ImmutableArray.CreateBuilder<PredictionOutcomeRevision>();
        await using (var command = Command(connection, transaction, "SELECT Revision, OutcomeJson, State, LabelEndTicks FROM Outcomes WHERE PredictionId = $id ORDER BY Revision ASC;", ("$id", id)))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) outcomes.Add(ReadOutcome(reader, 0, 1, 2, 3));
        var record = new PredictionAuditRecord(PredictionMonitoringPolicy.SchemaVersion, prediction, outcomes.ToImmutable());
        PredictionMonitoringReducer.ValidateAudit(record, id);
        return record;
    }

    private static PredictionOutcomeRevision ReadOutcome(SqliteDataReader reader, int revision, int json, int state, int labelEnd)
    {
        var o = Parse<PredictionOutcomeRevision>(reader.GetString(json), "outcome");
        long? ticks = reader.IsDBNull(labelEnd) ? null : reader.GetInt64(labelEnd);
        if (o.Revision != reader.GetInt64(revision) || (long)o.State != reader.GetInt64(state) || o.LabelEndUtc?.UtcTicks != ticks)
            throw new InvalidDataException("A stored outcome does not match its indexed columns.");
        return o;
    }

    private const string LatestOutcomeJoin = """
        JOIN Outcomes o ON o.PredictionId = p.PredictionId
         AND o.Revision = (SELECT MAX(Revision) FROM Outcomes WHERE PredictionId = p.PredictionId)
        """;

    /// <summary>
    /// Forecasts of one symbol/timeframe with their latest outcome (the refresh candidates). The legacy refresh passes
    /// <paramref name="excludeAttested"/> so forecasts governed by a producer stream are never touched by it.
    /// </summary>
    public static async Task<List<(MonitoredPrediction Prediction, PredictionOutcomeRevision Latest)>> ReadLatestForSymbolAsync(
        SqliteConnection connection, SqliteTransaction transaction, string symbol, TimeframeType timeframe, CancellationToken ct,
        bool excludeAttested = false)
    {
        var rows = new List<(MonitoredPrediction, PredictionOutcomeRevision)>();
        await using var command = Command(connection, transaction,
            "SELECT p.PredictionId, p.PredictionJson, o.Revision, o.OutcomeJson, o.State, o.LabelEndTicks FROM Predictions p " + LatestOutcomeJoin
            + " WHERE p.Symbol = $s AND p.Timeframe = $f"
            + (excludeAttested ? " AND NOT EXISTS (SELECT 1 FROM AttestedBindings b WHERE b.PredictionId = p.PredictionId)" : string.Empty)
            + " ORDER BY p.PredictionId;", ("$s", symbol), ("$f", (long)timeframe));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            rows.Add((ReadPrediction(reader, 1, reader.GetString(0)), ReadOutcome(reader, 2, 3, 4, 5)));
        }
        return rows;
    }

    /// <summary>The forecasts whose latest outcome is Matured with now-30d &lt; LabelEnd &lt;= now: only the window is read and parsed.</summary>
    public static async Task<List<(MonitoredPrediction Prediction, PredictionOutcomeRevision Latest)>> ReadVotesAsync(
        SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, CancellationToken ct)
    {
        var rows = new List<(MonitoredPrediction, PredictionOutcomeRevision)>();
        await using var command = Command(connection, transaction,
            "SELECT p.PredictionId, p.PredictionJson, o.Revision, o.OutcomeJson, o.State, o.LabelEndTicks FROM Outcomes o JOIN Predictions p ON p.PredictionId = o.PredictionId "
            + "WHERE o.State = $matured AND o.LabelEndTicks > $lo AND o.LabelEndTicks <= $hi "
            + "AND o.Revision = (SELECT MAX(Revision) FROM Outcomes WHERE PredictionId = o.PredictionId) ORDER BY p.PredictionId;",
            ("$matured", (long)PredictionOutcomeState.Matured), ("$lo", now.AddDays(-PredictionMonitoringPolicy.LookbackCalendarDays).UtcTicks), ("$hi", now.UtcTicks));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            rows.Add((ReadPrediction(reader, 1, reader.GetString(0)), ReadOutcome(reader, 2, 3, 4, 5)));
        }
        return rows;
    }

    public static async Task<List<(string Model, string Target)>> ReadGroupsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var groups = new List<(string, string)>();
        await using var command = Command(connection, transaction, "SELECT DISTINCT ModelId, TargetHash FROM Predictions ORDER BY ModelId, TargetHash;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) groups.Add((reader.GetString(0), reader.GetString(1)));
        return groups;
    }

    // ---- producer-attested evidence (companion tables, created only when the capability is first used) --------------------------

    private const string AttestationSchema = """
        CREATE TABLE IF NOT EXISTS AttestedStreams(
            ProviderId TEXT NOT NULL, InstrumentId TEXT NOT NULL, Timeframe INTEGER NOT NULL,
            HeadEpoch INTEGER NOT NULL CHECK(HeadEpoch >= 0), HeadSequence TEXT NOT NULL, HeadDigest TEXT NOT NULL,
            Conflicted INTEGER NOT NULL CHECK(Conflicted IN (0, 1)), UpdatedTicks INTEGER NOT NULL,
            PRIMARY KEY(ProviderId, InstrumentId, Timeframe));
        CREATE TABLE IF NOT EXISTS AttestedEvidence(
            ProviderId TEXT NOT NULL, InstrumentId TEXT NOT NULL, Timeframe INTEGER NOT NULL, Epoch INTEGER NOT NULL,
            Sequence TEXT NOT NULL, Digest TEXT NOT NULL, ObservedTicks INTEGER NOT NULL, Disposition TEXT NOT NULL, Reason TEXT NULL,
            PRIMARY KEY(ProviderId, InstrumentId, Timeframe, Epoch, Sequence, Digest));
        CREATE TABLE IF NOT EXISTS AttestedBindings(
            PredictionId TEXT NOT NULL PRIMARY KEY REFERENCES Predictions(PredictionId),
            ProviderId TEXT NOT NULL, InstrumentId TEXT NOT NULL, Timeframe INTEGER NOT NULL, BoundTicks INTEGER NOT NULL);
        """;

    /// <summary>Additive and idempotent; runs inside the caller's writer transaction (rolled back with it).</summary>
    public static Task EnsureAttestationSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct) =>
        ExecAsync(connection, transaction, AttestationSchema, ct);

    public static async Task<bool> AttestationSchemaExistsAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct) =>
        await ScalarAsync(connection, transaction, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'AttestedBindings';", ct).ConfigureAwait(false) is not null;

    public static async Task<AttestedStreamHead?> ReadStreamAsync(SqliteConnection connection, SqliteTransaction transaction,
        string provider, string instrument, TimeframeType timeframe, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT HeadEpoch, HeadSequence, HeadDigest, Conflicted FROM AttestedStreams WHERE ProviderId = $p AND InstrumentId = $i AND Timeframe = $f;",
            ("$p", provider), ("$i", instrument), ("$f", (long)timeframe));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        if (!ulong.TryParse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || !PredictionMonitoringReducer.ValidHash(reader.GetString(2)))
            throw new InvalidDataException("A stored attested stream head is invalid.");
        return new AttestedStreamHead(reader.GetInt64(0), sequence, reader.GetString(2), reader.GetInt64(3) == 1);
    }

    public static Task UpsertStreamAsync(SqliteConnection connection, SqliteTransaction transaction, string provider, string instrument,
        TimeframeType timeframe, AttestedStreamHead head, DateTimeOffset now, CancellationToken ct) =>
        ExecAsync(connection, transaction, """
            INSERT INTO AttestedStreams(ProviderId, InstrumentId, Timeframe, HeadEpoch, HeadSequence, HeadDigest, Conflicted, UpdatedTicks)
            VALUES($p, $i, $f, $e, $s, $d, $c, $u)
            ON CONFLICT(ProviderId, InstrumentId, Timeframe) DO UPDATE SET HeadEpoch = excluded.HeadEpoch, HeadSequence = excluded.HeadSequence,
                HeadDigest = excluded.HeadDigest, Conflicted = excluded.Conflicted, UpdatedTicks = excluded.UpdatedTicks;
            """, ct, ("$p", provider), ("$i", instrument), ("$f", (long)timeframe), ("$e", head.Epoch),
            ("$s", head.Sequence.ToString(CultureInfo.InvariantCulture)), ("$d", head.Digest), ("$c", head.Conflicted ? 1L : 0L), ("$u", now.UtcTicks));

    /// <summary>One audit row per distinct delivery (authority, epoch, sequence, digest); a repeated identical delivery adds nothing.</summary>
    public static Task InsertEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction, AttestedMonitoringSnapshot snapshot,
        string digest, string disposition, string? reason, CancellationToken ct) =>
        ExecAsync(connection, transaction, """
            INSERT OR IGNORE INTO AttestedEvidence(ProviderId, InstrumentId, Timeframe, Epoch, Sequence, Digest, ObservedTicks, Disposition, Reason)
            VALUES($p, $i, $f, $e, $s, $d, $o, $x, $r);
            """, ct, ("$p", snapshot.ProviderId), ("$i", snapshot.InstrumentId), ("$f", (long)snapshot.Timeframe), ("$e", snapshot.RevisionEpoch),
            ("$s", snapshot.ProviderSequence.ToString(CultureInfo.InvariantCulture)), ("$d", digest), ("$o", snapshot.ObservedUtc.UtcTicks),
            ("$x", disposition), ("$r", reason));

    /// <summary>A stored timeframe number is trusted only when it names a timeframe monitoring supports; anything else is a corrupt row.</summary>
    private static TimeframeType ReadTimeframe(long stored, string row) =>
        stored is >= 0 and <= int.MaxValue && MonitoringTimeframes.IsSupported((TimeframeType)stored) ? (TimeframeType)stored
            : throw new InvalidDataException($"A stored attestation {row} is invalid.");

    /// <summary>The stream every bound forecast belongs to.</summary>
    public static async Task<Dictionary<string, (string Provider, string Instrument, TimeframeType Timeframe)>> ReadBindingsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var bindings = new Dictionary<string, (string, string, TimeframeType)>(StringComparer.Ordinal);
        await using var command = Command(connection, transaction, "SELECT PredictionId, ProviderId, InstrumentId, Timeframe FROM AttestedBindings;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            bindings[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), ReadTimeframe(reader.GetInt64(3), "binding"));
        }
        return bindings;
    }

    public static Task InsertBindingAsync(SqliteConnection connection, SqliteTransaction transaction, string predictionId, string provider,
        string instrument, TimeframeType timeframe, DateTimeOffset now, CancellationToken ct) =>
        ExecAsync(connection, transaction, "INSERT INTO AttestedBindings(PredictionId, ProviderId, InstrumentId, Timeframe, BoundTicks) VALUES($id, $p, $i, $f, $b);",
            ct, ("$id", predictionId), ("$p", provider), ("$i", instrument), ("$f", (long)timeframe), ("$b", now.UtcTicks));

    /// <summary>Streams whose current sequence is in conflict (an unresolved data-integrity fault until a higher sequence arrives).</summary>
    public static async Task<List<(string Provider, string Instrument, TimeframeType Timeframe)>> ReadConflictedStreamsAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        var streams = new List<(string, string, TimeframeType)>();
        if (!await AttestationSchemaExistsAsync(connection, null, ct).ConfigureAwait(false)) return streams;
        await using var command = Command(connection, null, "SELECT ProviderId, InstrumentId, Timeframe FROM AttestedStreams WHERE Conflicted = 1 ORDER BY ProviderId, InstrumentId, Timeframe;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) streams.Add((reader.GetString(0), reader.GetString(1), ReadTimeframe(reader.GetInt64(2), "stream")));
        return streams;
    }

    // ---- health, alerts, outbox ----------------------------------------------------------------------------------------

    /// <summary>The persisted health rows, validated exactly like the JSON predecessor (including State = f(N,K)).</summary>
    public static async Task<List<HealthRow>> ReadHealthAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var rows = new List<HealthRow>();
        await using (var command = Command(connection, transaction,
            "SELECT ModelId, TargetHash, HealthRevision, N, K, AccuracyText, State, EvaluatedTicks, LastAlertId, VoteDigest FROM Health ORDER BY ModelId, TargetHash;"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var state = reader.GetInt64(6);
                if (state is < 0 or > (long)ModelHealthState.Decayed || reader.GetInt64(3) > int.MaxValue || reader.GetInt64(4) > int.MaxValue)
                    throw new InvalidDataException("A stored health row is invalid.");
                decimal? accuracy = reader.IsDBNull(5) ? null : decimal.Parse(reader.GetString(5), NumberStyles.Number, CultureInfo.InvariantCulture);
                var snapshot = new ModelMonitoringSnapshot(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(3), (int)reader.GetInt64(4),
                    accuracy, (ModelHealthState)state, new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero), reader.IsDBNull(8) ? null : reader.GetString(8));
                rows.Add(new HealthRow(snapshot, reader.GetInt64(2), reader.GetString(9)));
            }
        PredictionMonitoringReducer.ValidateHealth(rows.Select(r => r.Snapshot).ToList());
        return rows;
    }

    public static Task UpsertHealthAsync(SqliteConnection connection, SqliteTransaction transaction, HealthRow row, CancellationToken ct)
    {
        var s = row.Snapshot;
        return ExecAsync(connection, transaction, """
            INSERT INTO Health(ModelId, TargetHash, HealthRevision, N, K, AccuracyText, State, EvaluatedTicks, LastAlertId, VoteDigest)
            VALUES($m, $t, $rev, $n, $k, $a, $st, $e, $alert, $d)
            ON CONFLICT(ModelId, TargetHash) DO UPDATE SET HealthRevision = excluded.HealthRevision, N = excluded.N, K = excluded.K,
                AccuracyText = excluded.AccuracyText, State = excluded.State, EvaluatedTicks = excluded.EvaluatedTicks,
                LastAlertId = excluded.LastAlertId, VoteDigest = excluded.VoteDigest;
            """, ct, ("$m", s.ModelId), ("$t", s.TargetContractHash), ("$rev", row.Revision), ("$n", (long)s.MaturedCount), ("$k", (long)s.CorrectCount),
            ("$a", s.Accuracy?.ToString(CultureInfo.InvariantCulture)), ("$st", (long)s.State), ("$e", s.EvaluatedUtc.UtcTicks),
            ("$alert", s.LastAlertId), ("$d", row.VoteDigest));
    }

    /// <summary>Inserts the alert and its outbox row. A new event is Pending/0/null; an imported historical alert is Presented/0/null with a NULL revision.</summary>
    public static async Task InsertAlertAsync(SqliteConnection connection, SqliteTransaction transaction, ModelDecayAlert alert, long? healthRevision,
        bool presented, CancellationToken ct)
    {
        await ExecAsync(connection, transaction, "INSERT INTO Alerts(AlertId, ModelId, TargetHash, HealthRevision, PayloadJson) VALUES($id, $m, $t, $rev, $j);", ct,
            ("$id", alert.AlertId), ("$m", alert.Snapshot.ModelId), ("$t", alert.Snapshot.TargetContractHash), ("$rev", healthRevision), ("$j", Serialize(alert))).ConfigureAwait(false);
        await ExecAsync(connection, transaction, "INSERT INTO Outbox(AlertId, State, AttemptCount, LastError) VALUES($id, $st, 0, NULL);", ct,
            ("$id", alert.AlertId), ("$st", presented ? 1L : 0L)).ConfigureAwait(false);
    }

    /// <summary>Pending events ordered by committed group revision, then alert id.</summary>
    public static async Task<List<PendingNotification>> ReadPendingAsync(SqliteConnection connection, CancellationToken ct)
    {
        var pending = new List<PendingNotification>();
        await using var command = Command(connection, null,
            "SELECT a.PayloadJson, a.HealthRevision, o.AttemptCount, a.AlertId FROM Outbox o JOIN Alerts a ON a.AlertId = o.AlertId "
            + "WHERE o.State = 0 ORDER BY a.HealthRevision, a.AlertId;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1)) throw new InvalidDataException("A pending notification has no committed health revision.");
            var alert = Parse<ModelDecayAlert>(reader.GetString(0), "alert");
            if (alert.AlertId != reader.GetString(3)) throw new InvalidDataException("A stored alert does not match its key.");
            pending.Add(new PendingNotification(alert, reader.GetInt64(1), reader.GetInt64(2)));
        }
        return pending;
    }

    public static async Task<bool> AcknowledgeAsync(SqliteConnection connection, SqliteTransaction transaction, string alertId, CancellationToken ct) =>
        await ExecAsync(connection, transaction, "UPDATE Outbox SET State = 1 WHERE AlertId = $id AND State = 0;", ct, ("$id", alertId)).ConfigureAwait(false) == 1;

    /// <summary>Persists attempt count + 1 (checked Int64) and the error; the event stays Pending.</summary>
    public static async Task RecordFailureAsync(SqliteConnection connection, SqliteTransaction transaction, string alertId, long attempts, string error, CancellationToken ct)
    {
        var next = checked(attempts + 1);
        if (await ExecAsync(connection, transaction, "UPDATE Outbox SET AttemptCount = $n, LastError = $e WHERE AlertId = $id AND State = 0 AND AttemptCount = $old;", ct,
            ("$n", next), ("$e", error), ("$id", alertId), ("$old", attempts)).ConfigureAwait(false) > 1)
            throw new InvalidDataException("An outbox row was updated more than once.");
    }

    public static async Task<long> CountAsync(SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction, $"SELECT COUNT(*) FROM {table};", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

    // ---- command helpers ----------------------------------------------------------------------------------------------

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static async Task<int> ExecAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
