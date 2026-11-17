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
    private readonly int _rsRatingMaxStaleCalendarDays;

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
        // Outside the hard band (for example a negative value, which would leave out every symbol) is fatal here too; the warnings of the standard band are
        // logged once by StockAnalyzerSettings, which validates the same values at startup.
        StockAnalyzer.Core.Models.Settings.RsRatingSettingsValidator.ValidateMaxStaleCalendarDays(opt.RsRatingMaxStaleCalendarDays);
        _rsRatingMaxStaleCalendarDays = opt.RsRatingMaxStaleCalendarDays;
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
                        resolvedTargetDate = ConvertToDateOnly(reader.GetValue(3));
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
    /// A symbol whose latest bar is more than <see cref="MarketDataSettings.RsRatingMaxStaleCalendarDays"/> calendar days older than the latest bar
    /// of the symbols that have all five closes (the universe's reference date) is omitted too, so that a delisted or unsynchronised symbol is not ranked
    /// against current prices (decision D-1: the reference date is the latest date among the eligible symbols; a lag equal to the tolerance is kept).
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
                           date,
                           ROW_NUMBER() OVER (PARTITION BY filename ORDER BY date DESC) AS rn
                    FROM read_parquet([{filesListString}], filename=true)
                    WHERE close IS NOT NULL AND date IS NOT NULL
                )
                SELECT symbol,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankLatestClose} THEN close END) AS c0,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter1Close} THEN close END) AS c63,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter2Close} THEN close END) AS c126,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter3Close} THEN close END) AS c189,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankQuarter4Close} THEN close END) AS c252,
                       MAX(CASE WHEN rn = {RsRatingCalculator.RankLatestClose} THEN date END) AS last_date
                FROM ranked
                WHERE rn IN ({RsRatingCalculator.RankLatestClose}, {RsRatingCalculator.RankQuarter1Close}, {RsRatingCalculator.RankQuarter2Close}, {RsRatingCalculator.RankQuarter3Close}, {RsRatingCalculator.RankQuarter4Close})
                GROUP BY filename, symbol
                HAVING COUNT(*) = {RsRatingCalculator.CheckpointCount};";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var eligible = new List<(string Symbol, DateOnly LatestDate, RsRatingCloses Closes)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue;
                string symbol = reader.GetString(0);

                if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5) || reader.IsDBNull(6))
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

                eligible.Add((symbol, ConvertToDateOnly(reader.GetValue(6)), new RsRatingCloses(c0, c63, c126, c189, c252)));
            }

            AddSymbolsWithinReferenceDate(eligible, result);
        }

        return result;
    }

    /// <summary>
    /// Copies into <paramref name="result"/> the symbols whose latest bar is at most <see cref="_rsRatingMaxStaleCalendarDays"/> calendar days older than
    /// the latest bar of all <paramref name="eligible"/> symbols (the reference date). The reference date is the one definition shared by every RS Rating
    /// consumer (watchlist, screener, MCP), because they all read through this provider.
    /// </summary>
    private void AddSymbolsWithinReferenceDate(
        IReadOnlyList<(string Symbol, DateOnly LatestDate, RsRatingCloses Closes)> eligible,
        Dictionary<string, RsRatingCloses> result)
    {
        if (eligible.Count == 0)
        {
            return;
        }

        // One pass: the reference date is the newest latest-bar date; the runner-up is the newest date below it. The newest date is isolated when only one
        // symbol has it (the count) and the runner-up is further away than the tolerance.
        DateOnly referenceDate = DateOnly.MinValue;
        DateOnly runnerUpDate = DateOnly.MinValue;
        int symbolsAtReferenceDate = 0;
        for (int i = 0; i < eligible.Count; i++)
        {
            DateOnly latest = eligible[i].LatestDate;
            if (latest > referenceDate)
            {
                runnerUpDate = referenceDate;
                referenceDate = latest;
                symbolsAtReferenceDate = 1;
            }
            else if (latest == referenceDate)
            {
                symbolsAtReferenceDate++;
            }
            else if (latest > runnerUpDate)
            {
                runnerUpDate = latest;
            }
        }

        int excluded = 0;
        for (int i = 0; i < eligible.Count; i++)
        {
            var (symbol, latestDate, closes) = eligible[i];
            if (referenceDate.DayNumber - latestDate.DayNumber > _rsRatingMaxStaleCalendarDays)
            {
                excluded++;
                continue;
            }

            result[symbol] = closes;
        }

        if (excluded == 0)
        {
            return;
        }

        // Debug: a symbol that stays stale (a delisted one) is left out on every computation, which is a diagnostic, not a lifecycle event.
        _logger.LogDebug(
            "RS Rating left out {ExcludedCount} of {EligibleCount} symbols whose latest bar is more than {MaxStaleCalendarDays} calendar days older than the reference date {ReferenceDate}.",
            excluded,
            eligible.Count,
            _rsRatingMaxStaleCalendarDays,
            referenceDate);

        // The newest date belongs to one symbol alone and is further than the tolerance above the runner-up: every other symbol was left out because of it. One abnormal
        // (future-dated) bar looks exactly like this, and so does a single freshly synchronised symbol among stale ones; the operator should look.
        if (symbolsAtReferenceDate == 1 && runnerUpDate != DateOnly.MinValue && referenceDate.DayNumber - runnerUpDate.DayNumber > _rsRatingMaxStaleCalendarDays)
        {
            _logger.LogWarning(
                "RS Rating reference date {ReferenceDate} is more than {MaxStaleCalendarDays} calendar days newer than the next newest latest bar {RunnerUpDate}; every other symbol was left out. The reference date may come from an abnormal (future-dated) bar.",
                referenceDate,
                _rsRatingMaxStaleCalendarDays,
                runnerUpDate);
        }
    }

    private static DateOnly ConvertToDateOnly(object rawDate)
        => rawDate switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s when DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidOperationException($"Unexpected date column type: {rawDate?.GetType().FullName}")
        };

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
