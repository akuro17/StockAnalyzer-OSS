using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
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
public class ParquetDataService : IDataService
{
    /// <summary>Optional source-authored BOOLEAN attestation; absent/null/false is unknown.</summary>
    public const string FinalityColumn = "is_final";
    private readonly DuckDBConnectionManager _dbManager;
    private readonly MarketDataSettings _settings;
    private readonly ILogger<ParquetDataService> _logger;
    private readonly TimeProvider _clock;

    public async Task<MonitoringObservation?> LoadMonitoringObservationAsync(string symbol, TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
    {
        if (timeFrame is not (TimeFrame.D1 or TimeFrame.W1 or TimeFrame.MN1)) return null;
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
            var snapshot = candles.ToImmutable();
            return new MonitoringObservation(PredictionLogService.SourceRevision(snapshot), observedUtc,
                snapshot, flags.ToImmutable());
        }
    }

    public ParquetDataService(
        DuckDBConnectionManager dbManager, 
        IOptions<MarketDataSettings> settings,
        ILogger<ParquetDataService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
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
        return Common.PathDiscovery.ResolveDataPath(configuredPath, relativeFallback, "*.parquet");
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
            var path = Path.Combine(basePath, $"{candidates[i]}.parquet").Replace("\\", "/");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string EscapeDuckDbPath(string path) => path.Replace("'", "''");
}
