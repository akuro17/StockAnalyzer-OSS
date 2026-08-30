using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Data service that loads market data from Parquet files using DuckDB.
/// </summary>
public class ParquetDataService : IDataService
{
    private readonly DuckDBConnectionManager _dbManager;
    private readonly MarketDataSettings _settings;
    private readonly ILogger<ParquetDataService> _logger;

    public ParquetDataService(
        DuckDBConnectionManager dbManager, 
        IOptions<MarketDataSettings> settings,
        ILogger<ParquetDataService>? logger = null)
    {
        _dbManager = dbManager;
        _settings = settings.Value;
        _logger = logger ?? NullLogger<ParquetDataService>.Instance;
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
    {
        var (configuredPath, relativeFallback) = timeFrame switch
        {
            TimeFrame.D1 => (_settings.DailyDataPath, "Data/Daily"),
            TimeFrame.W1 => (_settings.WeeklyDataPath, "Data/Weekly"),
            TimeFrame.MN1 => (_settings.MonthlyDataPath, "Data/Monthly"),
            _ => throw new NotSupportedException($"TimeFrame {timeFrame} is not currently supported for direct Parquet loading.")
        };

        var basePath = Common.PathDiscovery.ResolveDataPath(configuredPath, relativeFallback, "*.parquet");

        var filePath = Path.Combine(basePath, $"{symbol}.parquet").Replace("\\", "/");
        if (!File.Exists(filePath))
        {
            var altSymbol = symbol.Contains('.') ? symbol.Replace('.', '-') : (symbol.Contains('-') ? symbol.Replace('-', '.') : null);
            if (altSymbol != null)
            {
                var altFilePath = Path.Combine(basePath, $"{altSymbol}.parquet").Replace("\\", "/");
                if (File.Exists(altFilePath))
                {
                    filePath = altFilePath;
                }
                else
                {
                    _logger.LogDebug("Parquet file not found: {FilePath}", filePath);
                    return Array.Empty<CandleData>();
                }
            }
            else
            {
                _logger.LogDebug("Parquet file not found: {FilePath}", filePath);
                return Array.Empty<CandleData>();
            }
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

                    var dt = reader.GetDateTime(0);
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

    private static string EscapeDuckDbPath(string path) => path.Replace("'", "''");
}
