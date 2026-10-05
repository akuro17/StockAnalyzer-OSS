using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Threading;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Data service that loads market data from Parquet files using DuckDB.
/// </summary>
public class ParquetDataService : IDataService, IAttestedMonitoringDataSource
{
    /// <summary>Optional source-authored BOOLEAN attestation; absent/null/false is unknown.</summary>
    public const string FinalityColumn = "is_final";
    private readonly DuckDBConnectionManager _dbManager;
    private readonly MarketDataSettings _settings;
    private readonly ILogger<ParquetDataService> _logger;
    private readonly TimeProvider _clock;
    private readonly PredictionMonitoringSettings _monitoring;
    private readonly string? _attestedCopyRoot;
    private int _copiesSwept;
    private const string ParquetExtension = ".parquet";
    private const string ParquetSearchPattern = "*" + ParquetExtension;

    public async Task<MonitoringObservation?> LoadMonitoringObservationAsync(string symbol, TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
    {
        if (!MonitoringTimeframes.TryFrom(timeFrame, out _)) return null;
        var basePath = ResolveBasePath(timeFrame);
        var path = ResolveFilePath(basePath, symbol);
        if (path is null) return null;

        using (await _dbManager.AcquireLockAsync("MonitoringObservation", cancellationToken).ConfigureAwait(false))
        {
            // Capture before reading so a delayed older refresh cannot supersede a newer observation.
            var observedUtc = _clock.GetUtcNow();
            if (_dbManager.GetConnection() is not DbConnection connection)
                throw new InvalidOperationException("Connection must support async operations.");
            using var command = connection.CreateCommand();
            // Read all rows, including pending forecasts outside the currently visible chart window.
            command.CommandText = $"SELECT * FROM read_parquet('{EscapeDuckDbPath(path)}') ORDER BY date ASC";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var (snapshot, flags) = await ReadMonitoringRowsAsync(reader, cancellationToken).ConfigureAwait(false);
            return new MonitoringObservation(PredictionLogService.SourceRevision(snapshot), observedUtc, snapshot, flags);
        }
    }

    /// <summary>
    /// Reads every row of a monitoring Parquet without repairing or dropping any of them (a missing value, an invalid bar or a
    /// non-chronological date is a fault), plus the optional source-authored <see cref="FinalityColumn"/> flags.
    /// </summary>
    private static async Task<(ImmutableArray<CandleData> Candles, ImmutableArray<bool> Flags)> ReadMonitoringRowsAsync(
        DbDataReader reader, CancellationToken cancellationToken)
    {
        int date = reader.GetOrdinal("date"), open = reader.GetOrdinal("open"), high = reader.GetOrdinal("high"),
            low = reader.GetOrdinal("low"), close = reader.GetOrdinal("close"), volume = reader.GetOrdinal("volume");
        int final = -1;
        for (int i = 0; i < reader.FieldCount; i++)
            if (reader.GetName(i).Equals(FinalityColumn, StringComparison.OrdinalIgnoreCase)) final = i;
        if (final >= 0 && reader.GetFieldType(final) != typeof(bool))
            throw new InvalidDataException("Provider finality must be a BOOLEAN column.");
        var candles = ImmutableArray.CreateBuilder<CandleData>();
        var flags = ImmutableArray.CreateBuilder<bool>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Monitoring must not silently repair or discard a source bar, changing the horizon.
            if (reader.IsDBNull(date) || reader.IsDBNull(open) || reader.IsDBNull(high)
                || reader.IsDBNull(low) || reader.IsDBNull(close) || reader.IsDBNull(volume))
                throw new InvalidDataException("Monitoring source has a missing OHLCV value.");
            var candle = new CandleData(DateTime.SpecifyKind(reader.GetDateTime(date), DateTimeKind.Utc),
                Convert.ToDecimal(reader.GetValue(open)), Convert.ToDecimal(reader.GetValue(high)),
                Convert.ToDecimal(reader.GetValue(low)), Convert.ToDecimal(reader.GetValue(close)),
                Convert.ToInt64(reader.GetValue(volume)));
            if (!candle.IsValid() || candle.Close <= 0
                || candles.Count > 0 && candle.Timestamp <= candles[candles.Count - 1].Timestamp)
                throw new InvalidDataException("Monitoring source has invalid or duplicate chronological bars.");
            candles.Add(candle);
            flags.Add(final >= 0 && !reader.IsDBNull(final) && reader.GetBoolean(final));
        }
        return (candles.ToImmutable(), flags.ToImmutable());
    }

    /// <summary>
    /// Read-only access that never blocks the producer: on Windows an open handle without <see cref="FileShare.Delete"/> would make
    /// the producer's atomic replace of the file fail while it is being read.
    /// </summary>
    private static FileStreamOptions ProducerSharedRead => new()
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous
    };

    /// <summary>
    /// Loads the producer attestation sidecar (<c>&lt;instrument&gt;.attest.json</c>) next to the Parquet of the timeframe. No
    /// sidecar means no capability for this instrument (null). The Parquet bytes are copied once, hashed while copying and compared
    /// with the manifest before they are read; any inconsistency is a fault, never a repaired or partial snapshot.
    /// </summary>
    public async Task<AttestedMonitoringSnapshot?> LoadAttestedObservationAsync(string instrumentId, TimeFrame timeframe,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instrumentId)) throw new ArgumentException("An instrument id is required.", nameof(instrumentId));
        if (!MonitoringTimeframes.TryFrom(timeframe, out var expected)) return null;
        // The id becomes a file name: it can never contain a separator, a drive, or a traversal segment.
        if (!AttestedFileNames.IsPlain(instrumentId))
            throw new ArgumentException("The instrument id must be a plain file name stem.", nameof(instrumentId));
        var folder = ResolveBasePath(timeframe);
        var manifestPath = Path.Combine(folder, instrumentId + AttestationManifestReader.FileSuffix);
        if (!File.Exists(manifestPath)) return null;

        byte[] manifestBytes;
        await using (var manifestStream = new FileStream(manifestPath, ProducerSharedRead))
        {
            // Length and bytes come from the same handle, so a producer replacing the file cannot change what is checked and read.
            if (manifestStream.Length > _monitoring.AttestedManifestMaxBytes)
                throw new InvalidDataException("The attestation manifest exceeds the configured size limit.");
            manifestBytes = new byte[manifestStream.Length];
            await manifestStream.ReadExactlyAsync(manifestBytes, ct).ConfigureAwait(false);
        }
        var manifest = AttestationManifestReader.Parse(manifestBytes);
        if (manifest.InstrumentId != instrumentId || manifest.Timeframe != expected)
            throw new InvalidDataException("The attestation manifest does not describe the requested instrument and timeframe.");
        if (!AttestedFileNames.IsPlain(manifest.SourcePath))
            throw new InvalidDataException("The attested Parquet must be a file in the manifest folder.");
        var sourcePath = Path.Combine(folder, manifest.SourcePath);
        if (!File.Exists(sourcePath) || (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The attested Parquet is missing or is a link.");

        var copyDirectory = _attestedCopyRoot ?? PredictionMonitoringPaths.AttestedCopyRoot(PredictionMonitoringPaths.DefaultRoot());
        Directory.CreateDirectory(copyDirectory);
        SweepStaleCopies(copyDirectory);
        var copyPath = Path.Combine(copyDirectory, Guid.NewGuid().ToString("N") + ParquetExtension);
        try
        {
            // One private copy is made, and the hash is taken from that copy: the bytes that are verified are the bytes DuckDB reads.
            await using (var input = new FileStream(sourcePath, ProducerSharedRead))
            await using (var output = new FileStream(copyPath, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous }))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            string actual;
            await using (var copy = new FileStream(copyPath, new FileStreamOptions
            { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous }))
                actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(copy, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != manifest.SourceSha256)
                throw new InvalidDataException("The attested Parquet does not match the manifest hash (source integrity fault).");

            // ObservedUtc of the snapshot is the producer manifest as_of_utc, not an instant read here.
            ImmutableArray<CandleData> candles;
            using (await _dbManager.AcquireLockAsync("AttestedObservation", ct).ConfigureAwait(false))
            {
                if (_dbManager.GetConnection() is not DbConnection connection)
                    throw new InvalidOperationException("Connection must support async operations.");
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM read_parquet('{EscapeDuckDbPath(copyPath.Replace("\\", "/"))}') ORDER BY date ASC";
                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                candles = (await ReadMonitoringRowsAsync(reader, ct).ConfigureAwait(false)).Candles;
            }
            if (candles.Length != manifest.RowCount || candles.Length != manifest.Bars.Count)
                throw new InvalidDataException("The attested Parquet row count differs from the manifest evidence.");
            var evidence = ImmutableArray.CreateBuilder<BarEvidence>(candles.Length);
            for (int i = 0; i < candles.Length; i++)
            {
                var bar = manifest.Bars[i];
                if (DateOnly.FromDateTime(candles[i].Timestamp) != bar.Date)
                    throw new InvalidDataException("A Parquet date does not match its bar evidence in row order.");
                evidence.Add(new BarEvidence(PredictionLogService.Utc(candles[i].Timestamp), bar.PeriodStartUtc, bar.PeriodEndUtc, bar.AvailableAtUtc, bar.Finality));
            }
            var snapshot = new AttestedMonitoringSnapshot(manifest.ProviderId, manifest.InstrumentId, manifest.Timeframe, manifest.RevisionEpoch,
                manifest.ProviderSequence, PredictionLogService.SourceRevision(candles), manifest.AsOfUtc, candles, evidence.ToImmutable(),
                [.. manifest.Tombstones]);
            AttestedSnapshotRules.Validate(snapshot, _clock.GetUtcNow());
            return snapshot;
        }
        finally
        {
            try { File.Delete(copyPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } // a working copy; a leftover is harmless
        }
    }

    /// <summary>
    /// Once per service instance: removes working copies a crash left behind (older than the configured retention by last write).
    /// Best effort: a copy another instance is writing or reading is recent or locked and stays; no failure reaches the caller.
    /// </summary>
    private void SweepStaleCopies(string directory)
    {
        if (Interlocked.Exchange(ref _copiesSwept, 1) != 0) return;
        var oldest = _clock.GetUtcNow() - _monitoring.AttestedWorkingCopyRetention;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, ParquetSearchPattern))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= oldest.UtcDateTime) continue;
                    File.Delete(path);
                    _logger.LogInformation("Removed a stale attestation working copy {Path}.", path);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public ParquetDataService(
        DuckDBConnectionManager dbManager, 
        IOptions<MarketDataSettings> settings,
        ILogger<ParquetDataService>? logger = null,
        TimeProvider? timeProvider = null,
        IOptions<PredictionMonitoringSettings>? monitoringOptions = null,
        string? attestedCopyRoot = null)
    {
        _attestedCopyRoot = attestedCopyRoot;
        _clock = timeProvider ?? TimeProvider.System;
        _monitoring = monitoringOptions?.Value ?? new PredictionMonitoringSettings();
        _dbManager = dbManager;
        _settings = settings.Value;
        _logger = logger ?? NullLogger<ParquetDataService>.Instance;
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
    {
        var basePath = ResolveBasePath(timeFrame);

        var filePath = ResolveFilePath(basePath, symbol);
        if (filePath == null)
        {
            _logger.LogDebug("Parquet file not found for symbol {Symbol} in {BasePath}", symbol, basePath);
            return Array.Empty<CandleData>();
        }


        try
        {
            using (await _dbManager.AcquireLockAsync("LoadCandles"))
            {
                var connection = _dbManager.GetConnection();
                if (connection is not DbConnection dbConnection)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
                }

                var candles = new List<CandleData>();
                using var command = dbConnection.CreateCommand();
                
                string limitClause = count > 0 ? $"LIMIT {count}" : "";
                command.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{EscapeDuckDbPath(filePath)}') WHERE close IS NOT NULL AND date IS NOT NULL ORDER BY date DESC {limitClause}";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(4)) continue;

                    // DuckDB's ADO provider returns DateTimeKind.Unspecified regardless of the
                    // underlying Parquet data's own UTC timestamps; re-stamp Kind=Utc here so
                    // downstream consumers that validate it (e.g. BacktestInput) accept these bars.
                    var dt = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
                    var close = Convert.ToDecimal(reader.GetValue(4));
                    var open = reader.IsDBNull(1) ? close : Convert.ToDecimal(reader.GetValue(1));
                    var high = reader.IsDBNull(2) ? Math.Max(open, close) : Convert.ToDecimal(reader.GetValue(2));
                    var low = reader.IsDBNull(3) ? Math.Min(open, close) : Convert.ToDecimal(reader.GetValue(3));
                    var volume = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5));

                    candles.Add(new CandleData(dt, open, high, low, close, volume));
                }

                // Reverse to get ASC order for the chart
                candles.Reverse();
                _logger.LogInformation("Loaded {Count} candles for {Symbol} from {FilePath}", candles.Count, symbol, filePath);
                return candles;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load candles from Parquet for {Symbol}", symbol);
            return Array.Empty<CandleData>();
        }
    }

    private string ResolveBasePath(TimeFrame timeFrame)
    {
        var (configuredPath, relativeFallback) = timeFrame switch
        {
            TimeFrame.D1 => (_settings.DailyDataPath, MarketDataSettings.DefaultDailyDataPath),
            TimeFrame.W1 => (_settings.WeeklyDataPath, "Data/Weekly"),
            TimeFrame.MN1 => (_settings.MonthlyDataPath, "Data/Monthly"),
            _ => throw new NotSupportedException($"TimeFrame {timeFrame} is not currently supported for direct Parquet loading.")
        };
        return Common.PathDiscovery.ResolveDataPath(configuredPath, relativeFallback, ParquetSearchPattern);
    }

    private static string? ResolveFilePath(string basePath, string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        var candidates = new List<string> { symbol };
        if (symbol.StartsWith('^'))
        {
            candidates.Add(symbol.TrimStart('^'));
        }
        else
        {
            candidates.Add("^" + symbol);
        }

        int count = candidates.Count;
        for (int i = 0; i < count; i++)
        {
            var s = candidates[i];
            if (s.Contains('.')) candidates.Add(s.Replace('.', '-'));
            if (s.Contains('-')) candidates.Add(s.Replace('-', '.'));
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            var path = Path.Combine(basePath, $"{candidates[i]}{ParquetExtension}").Replace("\\", "/");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string EscapeDuckDbPath(string path) => path.Replace("'", "''");
}
