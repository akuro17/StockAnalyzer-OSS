using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// High-performance DuckDB heatmap return provider.
/// Resolves the reference date by taking the (offset + 1)-th distinct trading day in the union
/// calendar of requested tickers' daily bars, evaluating 1-day returns for all tickers on that exact date.
/// </summary>
public sealed class DuckDbHeatmapReturnProvider : IHeatmapReturnProvider, IRsRatingClosesProvider
{
    private readonly DuckDBConnectionManager _dbManager;
    private readonly ILogger<DuckDbHeatmapReturnProvider> _logger;
    private readonly string _baseDataPath;

    public string ProviderId => "DuckDB";

    public DuckDbHeatmapReturnProvider(
        DuckDBConnectionManager dbManager,
        IOptions<MarketDataSettings> settings,
        ILogger<DuckDbHeatmapReturnProvider>? logger = null)
    {
        _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
        _logger = logger ?? NullLogger<DuckDbHeatmapReturnProvider>.Instance;

        var opt = settings?.Value ?? new MarketDataSettings();
        _baseDataPath = PathDiscovery.ResolveDataPath(opt.DailyDataPath, MarketDataSettings.DefaultDailyDataPath, "*.parquet");
    }

    public async Task<HeatmapReturnResult> ComputeAsync(
        IReadOnlyList<string> tickers,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (tickers == null || tickers.Count == 0)
        {
            return HeatmapReturnResult.Empty;
        }

        if (offset < HeatmapOffsetLimits.Min || offset > HeatmapOffsetLimits.Max)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                $"Offset must be between {HeatmapOffsetLimits.Min} and {HeatmapOffsetLimits.Max}.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Locate existing daily Parquet files
        var existingDailyFiles = ResolveExistingDailyFiles(tickers);

        if (existingDailyFiles.Count == 0)
        {
            _logger.LogWarning(
                "No local daily parquet files found for requested {Count} tickers in {Path}. Falling back to synthetic provider.",
                tickers.Count,
                _baseDataPath);
            return await SyntheticHeatmapReturnProvider.Instance
                .ComputeAsync(tickers, offset, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            // 2. Execute single batch query under one DuckDB global lock
            using (await _dbManager.AcquireLockAsync("DuckDbHeatmapReturnBatch", cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_dbManager.GetConnection() is not DbConnection connection)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection supporting async execution.");
                }

                var filesListString = BuildDuckDbFilesList(existingDailyFiles.Values);

                var sql = $@"
                WITH src AS (
                    SELECT {DuckDbSymbolSql.Expression} AS symbol, date, close,
                           lag(close, 1) OVER (PARTITION BY filename ORDER BY date ASC) AS prev_close
                    FROM read_parquet([{filesListString}], filename=true)
                    WHERE close IS NOT NULL AND date IS NOT NULL
                ),
                target AS (
                    SELECT date FROM (SELECT DISTINCT date FROM src) ORDER BY date DESC LIMIT 1 OFFSET {offset}
                )
                SELECT s.symbol, s.close, s.prev_close, s.date
                FROM src s JOIN target t ON s.date = t.date;";

                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                DateOnly? resolvedTargetDate = null;
                var rowMap = new Dictionary<string, (decimal Close, decimal? PrevClose)>(StringComparer.OrdinalIgnoreCase);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string sym = reader.GetString(0);
                    decimal close = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                    decimal? prevClose = reader.IsDBNull(2) ? null : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);

                    if (resolvedTargetDate == null && !reader.IsDBNull(3))
                    {
                        var rawDate = reader.GetValue(3);
                        resolvedTargetDate = rawDate switch
                        {
                            DateOnly d => d,
                            DateTime dt => DateOnly.FromDateTime(dt),
                            string s when DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
                            _ => throw new InvalidOperationException($"Unexpected date column type: {rawDate?.GetType().FullName}")
                        };
                    }

                    rowMap[sym] = (close, prevClose);
                }

                var items = new List<UniformHeatmapItem>(tickers.Count);
                int validCount = 0;
                for (int i = 0; i < tickers.Count; i++)
                {
                    string ticker = tickers[i];
                    double ret = double.NaN;
                    if (rowMap.TryGetValue(ticker, out var row))
                    {
                        if (row.PrevClose is > 0m)
                        {
                            decimal retDecimal = (row.Close - row.PrevClose.Value) / row.PrevClose.Value;
                            ret = (double)retDecimal;
                            validCount++;
                        }
                    }
                    items.Add(new UniformHeatmapItem(ticker, ret));
                }

                return new HeatmapReturnResult(
                    items,
                    resolvedTargetDate,
                    isLiveMarketData: true,
                    providerId: ProviderId,
                    statusMessage: $"Live Market Data ({validCount}/{tickers.Count} valid)");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to compute heatmap returns via DuckDB; falling back to synthetic provider.");
            return await SyntheticHeatmapReturnProvider.Instance
                .ComputeAsync(tickers, offset, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the five closes (<c>C0, C63, C126, C189, C252</c>) required for
    /// <see cref="RsRatingCalculator"/> across all requested tickers in a single multi-file DuckDB query.
    /// Null closes are skipped before counting reverse-chronological positions; symbols with fewer than
    /// <see cref="RsRatingCalculator.RequiredCloseCount"/> valid non-null closes are omitted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, RsRatingCloses>> GetRsRatingClosesAsync(
        IReadOnlyList<string> tickers,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, RsRatingCloses>(StringComparer.OrdinalIgnoreCase);
        if (tickers == null || tickers.Count == 0)
        {
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var existingDailyFiles = ResolveExistingDailyFiles(tickers);
        if (existingDailyFiles.Count == 0)
        {
            return result;
        }

        using (await _dbManager.AcquireLockAsync("DuckDbRsRatingClosesBatch", cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_dbManager.GetConnection() is not DbConnection connection)
            {
                throw new InvalidOperationException("Connection must be a DbConnection supporting async execution.");
            }

            var filesListString = BuildDuckDbFilesList(existingDailyFiles.Values);

            var sql = $@"
                WITH ranked AS (
                    SELECT filename,
                           {DuckDbSymbolSql.Expression} AS symbol,
                           close,
                           ROW_NUMBER() OVER (PARTITION BY filename ORDER BY date DESC) AS rn
                    FROM read_parquet([{filesListString}], filename=true)
                    WHERE close IS NOT NULL AND date IS NOT NULL
                )
                SELECT symbol,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankLatestClose} THEN close END) AS c0,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter1Close} THEN close END) AS c63,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter2Close} THEN close END) AS c126,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter3Close} THEN close END) AS c189,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter4Close} THEN close END) AS c252
                FROM ranked
                WHERE rn IN ({RsRatingCalculator.RankLatestClose}, {RsRatingCalculator.RankQuarter1Close}, {RsRatingCalculator.RankQuarter2Close}, {RsRatingCalculator.RankQuarter3Close}, {RsRatingCalculator.RankQuarter4Close})
                GROUP BY filename, symbol
                HAVING COUNT(*) = {RsRatingCalculator.CheckpointCount};";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue;
                string symbol = reader.GetString(0);

                if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5))
                {
                    continue;
                }

                if (!TryConvertToDecimal(reader.GetValue(1), out decimal c0) ||
                    !TryConvertToDecimal(reader.GetValue(2), out decimal c63) ||
                    !TryConvertToDecimal(reader.GetValue(3), out decimal c126) ||
                    !TryConvertToDecimal(reader.GetValue(4), out decimal c189) ||
                    !TryConvertToDecimal(reader.GetValue(5), out decimal c252))
                {
                    continue;
                }

                result[symbol] = new RsRatingCloses(c0, c63, c126, c189, c252);
            }
        }

        return result;
    }

    private Dictionary<string, string> ResolveExistingDailyFiles(IReadOnlyList<string> tickers)
    {
        var existingDailyFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tickers.Count; i++)
        {
            string symbol = tickers[i];
            if (string.IsNullOrWhiteSpace(symbol) || !TickerSymbol.IsWellFormed(symbol)) continue;
            if (existingDailyFiles.ContainsKey(symbol)) continue;

            string path = Path.Combine(_baseDataPath, $"{symbol}.parquet").Replace("\\", "/");
            if (File.Exists(path))
            {
                existingDailyFiles[symbol] = path;
            }
        }

        return existingDailyFiles;
    }

    private static string BuildDuckDbFilesList(IEnumerable<string> filePaths)
        => string.Join(", ", filePaths.Select(f => $"'{ParquetMarketDataProvider.EscapeDuckDbPath(f)}'"));

    private static bool TryConvertToDecimal(object rawValue, out decimal value)
    {
        value = 0m;
        if (rawValue is null || rawValue == DBNull.Value)
        {
            return false;
        }

        if (rawValue is float f && !float.IsFinite(f))
        {
            return false;
        }

        if (rawValue is double d && !double.IsFinite(d))
        {
            return false;
        }

        try
        {
            value = Convert.ToDecimal(rawValue, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }
    }
}
