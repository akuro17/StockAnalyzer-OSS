using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// High-performance DuckDB batch market data provider.
/// Executes windowed aggregations across multiple Parquet time-series within a single global lock acquisition,
/// completely eliminating per-ticker serialization and UI freezes.
/// </summary>
public sealed class DuckDbScatterMetricProvider : IScatterMetricProvider
{
    private readonly DuckDBConnectionManager _dbManager;
    private readonly ILogger<DuckDbScatterMetricProvider> _logger;
    private readonly string _baseDataPath;
    private readonly string _metadataPath;

    public string ProviderId => "DuckDB";
    public bool IsLiveMarketData => true;

    public DuckDbScatterMetricProvider(
        DuckDBConnectionManager dbManager,
        IOptions<MarketDataSettings> settings,
        ILogger<DuckDbScatterMetricProvider>? logger = null)
    {
        _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
        _logger = logger ?? NullLogger<DuckDbScatterMetricProvider>.Instance;

        var opt = settings?.Value ?? new MarketDataSettings();
        _baseDataPath = PathDiscovery.ResolveDataPath(opt.DailyDataPath, MarketDataSettings.DefaultDailyDataPath, "*.parquet");
        _metadataPath = PathDiscovery.ResolveDataPath(opt.MetadataPath, MarketDataSettings.DefaultMetadataDataPath, "*.parquet");
    }

    public async Task<ScatterMetricResult> ComputeMetricsAsync(
        IReadOnlyList<string> tickers,
        string xMetric,
        string yMetric,
        CancellationToken cancellationToken = default)
    {
        if (tickers == null || tickers.Count == 0)
        {
            return ScatterMetricResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Locate existing daily Parquet files
        var existingDailyFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tickers.Count; i++)
        {
            string symbol = tickers[i];
            string path = Path.Combine(_baseDataPath, $"{symbol}.parquet").Replace("\\", "/");
            if (File.Exists(path))
            {
                existingDailyFiles[symbol] = path;
            }
        }

        // If no daily files exist, fallback gracefully to synthetic data
        if (existingDailyFiles.Count == 0)
        {
            _logger.LogWarning("No local daily parquet files found for requested {Count} tickers in {Path}. Falling back to synthetic provider.",
                tickers.Count, _baseDataPath);
            return await SyntheticScatterMetricProvider.Instance.ComputeMetricsAsync(tickers, xMetric, yMetric, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var priceMetrics = new Dictionary<string, (double Ret1d, double Volatility20d, double AvgVolume20d, double Rsi14)>(StringComparer.OrdinalIgnoreCase);
            var fundamentalMetrics = new Dictionary<string, (double Pe, double MarketCap)>(StringComparer.OrdinalIgnoreCase);

            // 2. Execute single batch query under one DuckDB global lock
            using (await _dbManager.AcquireLockAsync("DuckDbScatterMetricBatch", cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_dbManager.GetConnection() is not DbConnection connection)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection supporting async execution.");
                }

                // Batch Time-Series Window Query
                var filesListString = string.Join(", ", existingDailyFiles.Values.Select(f => $"'{EscapeDuckDbPath(f)}'"));
                var batchTsSql = $@"
                    WITH ranked AS (
                        SELECT 
                            replace(regexp_replace(filename, '.*[\\/]', ''), '.parquet', '') as symbol,
                            date,
                            close,
                            volume,
                            row_number() OVER (PARTITION BY filename ORDER BY date DESC) as rn
                        FROM read_parquet([{filesListString}], filename=true)
                        WHERE close IS NOT NULL AND date IS NOT NULL
                    ),
                    recent AS (
                        SELECT 
                            symbol,
                            date,
                            close,
                            volume,
                            rn,
                            lag(close, 1) OVER (PARTITION BY symbol ORDER BY date ASC) as prev_close
                        FROM ranked
                        WHERE rn <= 30
                    ),
                    diffs AS (
                        SELECT 
                            symbol,
                            close,
                            volume,
                            rn,
                            CASE WHEN prev_close > 0 THEN (close - prev_close) / prev_close ELSE NULL END as daily_ret,
                            CASE WHEN close > prev_close THEN (close - prev_close) ELSE 0.0 END as gain,
                            CASE WHEN close < prev_close THEN (prev_close - close) ELSE 0.0 END as loss
                        FROM recent
                    )
                    SELECT
                        symbol,
                        max(CASE WHEN rn = 1 THEN daily_ret * 100.0 ELSE NULL END) as ret_1d,
                        stddev_samp(CASE WHEN rn <= 20 THEN daily_ret ELSE NULL END) * sqrt(252.0) * 100.0 as volatility_20d,
                        avg(CASE WHEN rn <= 20 THEN volume ELSE NULL END) as avg_volume_20d,
                        CASE 
                            WHEN avg(CASE WHEN rn <= 14 THEN gain ELSE NULL END) + avg(CASE WHEN rn <= 14 THEN loss ELSE NULL END) > 0
                            THEN 100.0 * avg(CASE WHEN rn <= 14 THEN gain ELSE NULL END) / 
                                 (avg(CASE WHEN rn <= 14 THEN gain ELSE NULL END) + avg(CASE WHEN rn <= 14 THEN loss ELSE NULL END))
                            ELSE 50.0 
                        END as rsi_14
                    FROM diffs
                    GROUP BY symbol;";

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = batchTsSql;
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        string sym = reader.GetString(0);
                        double ret1d = reader.IsDBNull(1) ? double.NaN : Convert.ToDouble(reader.GetValue(1));
                        double vol20d = reader.IsDBNull(2) ? double.NaN : Convert.ToDouble(reader.GetValue(2));
                        double volAvg = reader.IsDBNull(3) ? double.NaN : Convert.ToDouble(reader.GetValue(3));
                        double rsi14 = reader.IsDBNull(4) ? double.NaN : Convert.ToDouble(reader.GetValue(4));

                        priceMetrics[sym] = (ret1d, vol20d, volAvg, rsi14);
                    }
                }

                // Batch Metadata Query (if fundamentals requested)
                bool needsFundamentals = xMetric is "P/E" or "MarketCap" || yMetric is "P/E" or "MarketCap";
                if (needsFundamentals)
                {
                    var existingMetaFiles = new List<string>();
                    for (int i = 0; i < tickers.Count; i++)
                    {
                        string metaPath = Path.Combine(_metadataPath, $"{tickers[i]}.meta.parquet").Replace("\\", "/");
                        if (File.Exists(metaPath))
                        {
                            existingMetaFiles.Add(metaPath);
                        }
                    }

                    if (existingMetaFiles.Count > 0)
                    {
                        var metaFilesString = string.Join(", ", existingMetaFiles.Select(f => $"'{EscapeDuckDbPath(f)}'"));
                        var metaSql = $@"
                            SELECT 
                                regexp_replace(regexp_replace(filename, '.*[\\/]', ''), '(\\.meta)?\\.parquet$', '') as symbol,
                                trailing_pe,
                                market_cap
                            FROM read_parquet([{metaFilesString}], filename=true);";

                        using var metaCmd = connection.CreateCommand();
                        metaCmd.CommandText = metaSql;
                        using var metaReader = await metaCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        while (await metaReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            string sym = metaReader.GetString(0);
                            double pe = metaReader.IsDBNull(1) ? double.NaN : Convert.ToDouble(metaReader.GetValue(1));
                            double mcap = metaReader.IsDBNull(2) ? double.NaN : Convert.ToDouble(metaReader.GetValue(2));
                            fundamentalMetrics[sym] = (pe, mcap);
                        }
                    }
                }
            }

            // 3. Assemble ScatterMetricItems in original ticker order
            var items = new List<ScatterMetricItem>(tickers.Count);
            int validCount = 0;

            for (int i = 0; i < tickers.Count; i++)
            {
                string symbol = tickers[i];
                if (priceMetrics.TryGetValue(symbol, out var pm))
                {
                    if (!fundamentalMetrics.TryGetValue(symbol, out var fm))
                    {
                        fm = (double.NaN, double.NaN);
                    }

                    double xVal = ResolveMetricValue(xMetric, pm, fm);
                    double yVal = ResolveMetricValue(yMetric, pm, fm);
                    double retRatio = double.IsNaN(pm.Ret1d) ? double.NaN : pm.Ret1d / 100.0;

                    items.Add(new ScatterMetricItem(symbol, xVal, yVal, retRatio));
                    if (double.IsFinite(xVal) && double.IsFinite(yVal))
                    {
                        validCount++;
                    }
                }
                else
                {
                    // Symbol missing on disk: Safe NaN fallback
                    items.Add(new ScatterMetricItem(symbol, double.NaN, double.NaN, double.NaN));
                }
            }

            return new ScatterMetricResult(
                items: items,
                isLiveMarketData: true,
                providerId: ProviderId,
                statusMessage: $"Live Market Data ({validCount}/{tickers.Count} valid points)"
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "DuckDB batch metric calculation failed. Falling back to synthetic provider.");
            return await SyntheticScatterMetricProvider.Instance.ComputeMetricsAsync(tickers, xMetric, yMetric, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static double ResolveMetricValue(
        string metricName,
        (double Ret1d, double Volatility20d, double AvgVolume20d, double Rsi14) pm,
        (double Pe, double MarketCap) fm)
    {
        return metricName switch
        {
            "Return" => pm.Ret1d,
            "Volatility" => pm.Volatility20d,
            "RSI" => pm.Rsi14,
            "Volume" => pm.AvgVolume20d,
            "P/E" => fm.Pe,
            "MarketCap" => fm.MarketCap,
            _ => pm.Ret1d
        };
    }

    private static string EscapeDuckDbPath(string path) => path.Replace("'", "''");
}
