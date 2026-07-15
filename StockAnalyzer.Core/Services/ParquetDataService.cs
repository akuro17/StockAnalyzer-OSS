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
            _logger.LogDebug("Parquet file not found: {FilePath}", filePath);
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
                
                // Note: Column names 'date', 'open', 'high', 'low', 'close', 'volume' are lowercase in the Python update script.
                command.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{filePath}') ORDER BY date DESC LIMIT {count}";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    candles.Add(new CandleData(
                        reader.GetDateTime(0),
                        Convert.ToDecimal(reader.GetValue(1)),
                        Convert.ToDecimal(reader.GetValue(2)),
                        Convert.ToDecimal(reader.GetValue(3)),
                        Convert.ToDecimal(reader.GetValue(4)),
                        Convert.ToInt64(reader.GetValue(5))
                    ));
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
}
