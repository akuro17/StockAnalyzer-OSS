using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using StockAnalyzer.Core.Models.Portfolio;
using Python.Runtime;
using System.Text.Json;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// High-performance market data provider using DuckDB and Parquet files.
/// </summary>
public class ParquetMarketDataProvider : IMarketDataProvider
{
    private readonly DuckDBConnectionManager _dbManager;
    private readonly IPythonService _pythonService;
    private readonly ILogger<ParquetMarketDataProvider> _logger;
    private readonly string _baseDataPath;
    private readonly string _metadataPath;
    private readonly string? _tickerListPath;
    private readonly ResiliencePipeline _resiliencePipeline;
    
    // Low-allocation cache for ticker metadata (Sector/Industry) with TTL
    private readonly ConcurrentDictionary<string, (TickerMetadata Meta, DateTime Timestamp)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _metadataSemaphore = new(1, 1);
    
    // Column Constants for Metadata Parquet
    private const string ColShortName = "short_name";
    private const string ColLongName = "long_name";
    private const string ColRegion = "region";
    private const string ColSector = "sector";
    private const string ColIndustry = "industry";
    private const string ColCurrency = "currency";
    private const string ColCurrentPrice = "current_price";
    private const string ColLastClose = "last_close";
    private const string ColReturnOnEquity = "return_on_equity";
    private const string ColReturnOnAssets = "return_on_assets";
    private const string ColGrossMargins = "gross_margins";
    private const string ColOperatingMargins = "operating_margins";
    private const string ColProfitMargins = "profit_margins";
    private const string ColCurrentRatio = "current_ratio";
    private const string ColDebtToEquity = "debt_to_equity";
    private const string ColEbitda = "ebitda";
    private const string ColFreeCashflow = "free_cashflow";
    private const string ColOperatingCashflow = "operating_cashflow";
    private const string ColTrailingPE = "trailing_pe";
    private const string ColForwardPE = "forward_pe";
    private const string ColPriceToBook = "price_to_book";
    private const string ColTrailingEps = "trailing_eps";
    private const string ColForwardEps = "forward_eps";
    private const string ColBookValue = "book_value";
    private const string ColSharesOutstanding = "shares_outstanding";
    private const string ColFloatShares = "float_shares";
    private const string ColShortRatio = "short_ratio";
    private const string ColShortPercentOfFloat = "short_percent_of_float";
    private const string ColHeldPercentInsiders = "held_percent_insiders";
    private const string ColHeldPercentInstitutions = "held_percent_institutions";
    private const string ColLongBusinessSummary = "long_business_summary";
    private const string ColFullTimeEmployees = "full_time_employees";
    private const string ColFiftyTwoWeekHigh = "fifty_two_week_high";
    private const string ColFiftyTwoWeekLow = "fifty_two_week_low";
    private const string ColRevenueGrowth = "revenue_growth";
    private const string ColEarningsGrowth = "earnings_growth";
    private const string ColEnterpriseValue = "enterprise_value";
    private const string ColEnterpriseToEbitda = "enterprise_to_ebitda";
    private const string ColBeta = "beta";
    private const string ColPayoutRatio = "payout_ratio";
    private const string ColDividendRate = "dividend_rate";
    private const string ColDividendYield = "dividend_yield";
    private const string ColTotalDebt = "total_debt";
    private const string ColTotalCash = "total_cash";
    private const string ColTotalRevenue = "total_revenue";
    private const string ColMarketCap = "market_cap";
    private const string ColPbrCalculated = "pbr_calculated";
    private const string ColDividendYieldCalculated = "dividend_yield_calculated";
    private const string ColEarningsYield = "earnings_yield";
    private const string ColFcfYield = "fcf_yield";
    private const string ColFcfMargin = "fcf_margin";
    private const string ColNetDebt = "net_debt";
    private const string ColNetDebtToEbitda = "net_debt_to_ebitda";
    private const string ColDividendCoverage = "dividend_coverage";
    private const string ColPctFromFiftyTwoWeekHigh = "pct_from_fifty_two_week_high";
    private const string ColFloatRatio = "float_ratio";
    private const string ColMarketCapPerEmployee = "market_cap_per_employee";
    private const string ColPegRatio = "peg_ratio";
    private const string ColOperatingCashFlowYield = "operating_cash_flow_yield";
    private const string ColNetCashRatio = "net_cash_ratio";
    private const string ColPriceToSalesTrailing12Months = "price_to_sales_trailing_12_months";
    private const string ColEnterpriseToRevenue = "enterprise_to_revenue";
    private const string ColEbitdaMargins = "ebitda_margins";
    private const string ColQuickRatio = "quick_ratio";
    private const string ColAverageVolume = "average_volume";
    private const string ColPriceToCashFlowRatio = "price_to_cash_flow_ratio";
    private const string ColNetDebtEquityRatio = "net_debt_equity_ratio";
    private const string ColFiftyTwoWeekRangePosition = "fifty_two_week_range_position";
    private const string ColDailyTurnoverRate = "daily_turnover_rate";
    private const string ColAverageTurnoverRate = "average_turnover_rate";
    private const string ColDailyFloatShareTurnoverRatio = "daily_float_turnover_ratio";
    private const string ColAverageFloatTurnover = "average_float_turnover";
    private const string ColQuoteType = "quote_type";
    private const string ColExchangeTimezoneName = "exchange_timezone_name";
    private const string ColGmtOffSetMilliseconds = "gmt_offset_milliseconds";
    private const string ColExDividendDate = "ex_dividend_date";
    private const string ColLastFiscalYearEnd = "last_fiscal_year_end";
    private const string ColMostRecentQuarter = "most_recent_quarter";
    private const string ColTargetHighPrice = "target_high_price";
    private const string ColTargetLowPrice = "target_low_price";
    private const string ColTargetMeanPrice = "target_mean_price";
    private const string ColTargetMedianPrice = "target_median_price";
    private const string ColRecommendationKey = "recommendation_key";
    private const string ColRecommendationMean = "recommendation_mean";
    private const string ColNumberOfAnalystOpinions = "number_of_analyst_opinions";
    private const string ColMetadataLastUpdated = "metadata_last_updated";
    private const string ColTag = "tag";

    public ParquetMarketDataProvider(
        DuckDBConnectionManager dbManager, 
        IPythonService pythonService,
        IOptions<MarketDataSettings> settings,
        ILogger<ParquetMarketDataProvider>? logger = null)
    {
        _dbManager = dbManager;
        _pythonService = pythonService;
        _logger = logger ?? NullLogger<ParquetMarketDataProvider>.Instance;
        
        // Fail-Fast: Validate settings immediately
        settings.Value.Validate();
        
        _baseDataPath = Common.PathDiscovery.ResolveDataPath(settings.Value.DailyDataPath, "Data/Daily", "*.parquet");
        _metadataPath = Common.PathDiscovery.ResolveDataPath(settings.Value.MetadataPath, "Data/Metadata");
        
        if (!string.IsNullOrEmpty(settings.Value.TickerListPath))
        {
            _tickerListPath = Common.PathDiscovery.ResolveFilePath(settings.Value.TickerListPath, "StockAnalyzer.Python/tickers.json");
        }
        
        // Ensure default tickers.json exists
        if (!string.IsNullOrEmpty(_tickerListPath) && !File.Exists(_tickerListPath))
        {
            try
            {
                var parentDir = Path.GetDirectoryName(_tickerListPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }
                var defaultTickers = new List<string> { "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA" };
                var defaultJson = JsonSerializer.Serialize(defaultTickers, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_tickerListPath, defaultJson, System.Text.Encoding.UTF8);
                _logger.LogInformation("Created default master ticker list at {Path}", _tickerListPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create default master ticker list at {Path}", _tickerListPath);
            }
        }

        var weeklyPath = Common.PathDiscovery.ResolveDataPath(settings.Value.WeeklyDataPath, "Data/Weekly");
        var monthlyPath = Common.PathDiscovery.ResolveDataPath(settings.Value.MonthlyDataPath, "Data/Monthly");

        // Ensure directories exist or can be created during initialization
        foreach (var path in new[] { _baseDataPath, weeklyPath, monthlyPath, _metadataPath })
        {
            if (!Directory.Exists(path))
            {
                try 
                {
                    Directory.CreateDirectory(path);
                    _logger.LogInformation("Created missing data directory: {Path}", path);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Failed to initialize ParquetMarketDataProvider: Cannot create directory {Path}", path);
                    throw;
                }
            }
        }

        _resiliencePipeline = BuildResiliencePipeline();
        _logger.LogDebug("ParquetMarketDataProvider initialized with base path: {BasePath}", _baseDataPath);
    }

    private ResiliencePipeline BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                OnRetry = args =>
                {
                    _logger.LogWarning(args.Outcome.Exception, "DuckDB operation failed. Retrying ({AttemptNumber}/3)...", args.AttemptNumber);
                    return default;
                },
                ShouldHandle = new PredicateBuilder()
                    .Handle<Exception>(ex => 
                        ex.Message.Contains("Database is locked", StringComparison.OrdinalIgnoreCase) ||
                        ex is IOException ||
                        ex.GetType().Name.Contains("DuckDBException"))
            })
            .Build();
    }

    public async Task<IReadOnlyList<string>> GetAvailableTickersAsync()
    {
        using (await _dbManager.AcquireLockAsync("GetAvailableTickers"))
        {
            if (!string.IsNullOrEmpty(_tickerListPath) && File.Exists(_tickerListPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_tickerListPath);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        _logger.LogTrace("Loaded {Count} available tickers from master list: {Path}", list.Count, _tickerListPath);
                        return list;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read master ticker list from {Path}. Falling back to directory crawl.", _tickerListPath);
                }
            }
        }

        if (!Directory.Exists(_baseDataPath))
        {
            return Array.Empty<string>();
        }

        // Fallback: simple disk crawl for existing data files
        var tickers = await Task.Run(() => 
            Directory.GetFiles(_baseDataPath, "*.parquet")
                     .Select(Path.GetFileNameWithoutExtension)
                     .Where(f => f != null)
                     .Cast<string>()
                     .OrderBy(x => x)
                     .ToList());

        _logger.LogTrace("Discovered {Count} available tickers via disk crawl in {BasePath}", tickers.Count, _baseDataPath);
        return tickers;
    }

    public async Task AddTickerAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        symbol = symbol.Trim().ToUpperInvariant();

        await UpdateTickerListAsync(list => 
        {
            if (!list.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(symbol);
                return true;
            }
            return false;
        });
    }

    public async Task AddTickersAsync(IEnumerable<string> symbols)
    {
        if (symbols == null) return;
        
        var normalizedSymbols = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .ToList();

        if (normalizedSymbols.Count == 0) return;

        await UpdateTickerListAsync(list => 
        {
            bool added = false;
            foreach (var symbol in normalizedSymbols)
            {
                if (!list.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(symbol);
                    added = true;
                }
            }
            return added;
        });
    }

    public async Task RemoveTickerAsync(string symbol) => await RemoveTickersAsync(new[] { symbol });

    public async Task RemoveTickersAsync(IEnumerable<string> symbols)
    {
        if (symbols == null) return;
        
        var normalizedSymbols = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .ToList();

        if (normalizedSymbols.Count == 0) return;

        await UpdateTickerListAsync(list => 
        {
            bool removed = false;
            foreach (var symbol in normalizedSymbols)
            {
                var item = list.FirstOrDefault(x => x.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    list.Remove(item);
                    removed = true;
                }
            }
            return removed;
        });
    }

    private async Task UpdateTickerListAsync(Func<List<string>, bool> updateAction)
    {
        if (string.IsNullOrEmpty(_tickerListPath))
        {
             _logger.LogWarning("TickerListPath is not configured. Cannot update master ticker list.");
             return;
        }

        using (await _dbManager.AcquireLockAsync("UpdateTickerList"))
        {
            await _resiliencePipeline.ExecuteAsync(async ct => 
            {
                List<string> list;
                if (File.Exists(_tickerListPath))
                {
                    var json = await File.ReadAllTextAsync(_tickerListPath, ct);
                    list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
                else
                {
                    // Fallback to defaults if file missing
                    list = new List<string> { "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA" };
                    _logger.LogInformation("Master ticker list not found. Initialized with defaults.");
                }

                if (updateAction(list))
                {
                    list = list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
                    
                    var tempPath = _tickerListPath + ".tmp";
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var newJson = JsonSerializer.Serialize(list, options);
                    
                    await File.WriteAllTextAsync(tempPath, newJson, ct);
                    
                    // Atomic Swap with Verification
                    var tempInfo = new FileInfo(tempPath);
                    if (tempInfo.Exists && tempInfo.Length > 0)
                    {
                        if (File.Exists(_tickerListPath))
                        {
                            File.Replace(tempPath, _tickerListPath, null);
                        }
                        else
                        {
                            File.Move(tempPath, _tickerListPath);
                        }
                        _logger.LogDebug("Master ticker list updated successfully at {Path}", _tickerListPath);
                    }
                    else
                    {
                        _logger.LogError("Ticker list update failed: Temporary file is missing or empty at {Path}", tempPath);
                    }
                }
            });
        }
    }

    public async Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string ticker, TimeFrame timeFrame)
    {
        // For now, we only handle D1 from the Daily folder.
        if (timeFrame != TimeFrame.D1)
        {
            throw new NotSupportedException($"TimeFrame {timeFrame} is not yet supported in ParquetMarketDataProvider.");
        }

        var filePath = Path.Combine(_baseDataPath, $"{ticker}.parquet").Replace("\\", "/");
        if (!File.Exists(filePath))
        {
            _logger.LogDebug("Parquet file not found for ticker: {Ticker}", ticker);
            return Array.Empty<CandleData>();
        }

        var candles = new List<CandleData>();

        using (await _dbManager.AcquireLockAsync("GetTickersData"))
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var sw = Stopwatch.StartNew();
                var connection = _dbManager.GetConnection();
                
                if (connection is not DbConnection dbConnection)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
                }

                using var command = dbConnection.CreateCommand();
                command.CommandText = $"SELECT * FROM read_parquet('{filePath}')";

                // Use true async API and pass CancellationToken
                using var reader = await command.ExecuteReaderAsync(ct);
                
                int dateIdx = reader.GetOrdinal("date");
                int openIdx = reader.GetOrdinal("open");
                int highIdx = reader.GetOrdinal("high");
                int lowIdx = reader.GetOrdinal("low");
                int closeIdx = reader.GetOrdinal("close");
                int volumeIdx = reader.GetOrdinal("volume");

                while (await reader.ReadAsync(ct))
                {
                    candles.Add(new CandleData(
                        reader.GetDateTime(dateIdx),
                        Convert.ToDecimal(reader.GetValue(openIdx)),
                        Convert.ToDecimal(reader.GetValue(highIdx)),
                        Convert.ToDecimal(reader.GetValue(lowIdx)),
                        Convert.ToDecimal(reader.GetValue(closeIdx)),
                        Convert.ToInt64(reader.GetValue(volumeIdx))
                    ));
                }
                sw.Stop();
                _logger.LogTrace("Loaded {Count} candles for {Ticker} in {ElapsedMs}ms", candles.Count, ticker, sw.ElapsedMilliseconds);
            });
        }

        return candles;
    }

    public async Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria)
    {
        if (criteria == null || criteria.Conditions == null || criteria.Conditions.Count == 0)
        {
            return await GetAvailableTickersAsync();
        }

        using (await _dbManager.AcquireLockAsync("Screen"))
        {
            var sw = Stopwatch.StartNew();
            return await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var matchedTickers = new List<string>();
                var connection = _dbManager.GetConnection();

                if (connection is not DbConnection dbConnection)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
                }

                // High-performance batch screening using glob patterns
                var parquetPattern = Path.Combine(_baseDataPath, "*.parquet").Replace("\\", "/");
                var sql = SqlConditionTranslator.BuildBatchQuery(parquetPattern, criteria);

                using var command = dbConnection.CreateCommand();
                command.CommandText = sql;

                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    matchedTickers.Add(reader.GetString(0));
                }

                sw.Stop();
                _logger.LogInformation("Batch screening completed. Matched {Count} tickers in {ElapsedMs}ms", matchedTickers.Count, sw.ElapsedMilliseconds);
                return (IReadOnlyList<string>)matchedTickers;
            }, CancellationToken.None);
        }
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols)
    {
        var symbolList = symbols?.ToList() ?? new List<string>();
        if (symbolList.Count == 0) return new Dictionary<string, decimal>();

        var results = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        
        using (await _dbManager.AcquireLockAsync("GetLatestPrices"))
        {
            var sw = Stopwatch.StartNew();
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var connection = _dbManager.GetConnection();
                if (connection is not DbConnection dbConnection)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
                }

                var targetFiles = symbolList
                    .Select(s => Path.Combine(_baseDataPath, $"{s}.parquet").Replace("\\", "/"))
                    .Where(f => File.Exists(f))
                    .ToList();

                if (targetFiles.Count == 0)
                {
                    _logger.LogDebug("No parquet files found for the requested symbols.");
                    return;
                }

                var filesListString = string.Join(", ", targetFiles.Select(f => $"'{f}'"));
                var sql = $@"
                    SELECT 
                        replace(regexp_replace(filename, '.*[\\\\/]', ''), '.parquet', '') as ticker,
                        arg_max(close, date) as close
                    FROM read_parquet([{filesListString}], filename=true)
                    GROUP BY filename";

                using var command = dbConnection.CreateCommand();
                command.CommandText = sql;

                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    results[reader.GetString(0)] = Convert.ToDecimal(reader.GetValue(1));
                }
            });

            sw.Stop();
            _logger.LogTrace("Fetched latest prices for {Count} symbols in {ElapsedMs}ms", results.Count, sw.ElapsedMilliseconds);
        }

        return results;
    }

    public async Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker)
    {
        await _metadataSemaphore.WaitAsync();
        try
        {
            var meta = await _pythonService.RunAsync(py =>
            {
                try 
                {
                    using var sys = py.Import("sys");
                    var scriptDir = Common.PathDiscovery.ResolveDataPath(null, "StockAnalyzer.Python");
                    
                    // Convert backslashes for Python sys.path
                    scriptDir = scriptDir.Replace("\\", "/");
                    
                    using var pathList = sys.GetAttr("path");
                    bool pathExists = false;
                    foreach (var path in (dynamic)pathList)
                    {
                        if (path.ToString() == scriptDir)
                        {
                            pathExists = true;
                            break;
                        }
                    }
                    if (!pathExists)
                    {
                        using var pyScriptDir = PyObject.FromManagedObject(scriptDir);
                        pathList.InvokeMethod("append", pyScriptDir);
                    }

                    using var dp = py.Import("data_provider");
                    using var providerClass = dp.GetAttr("StockDataProvider");
                    using var provider = providerClass.Invoke();
                    using var pyTicker = PyObject.FromManagedObject(ticker);
                    using var result = provider.InvokeMethod("get_ticker_metadata", pyTicker);

                    using (var status = result["status"])
                    {
                        if (status.ToString() != "ok")
                        {
                            return new TickerMetadata(ticker, ticker, "", "", "", "");
                        }
                    }

                    string Normalize(object? val, string fallback = "-")
                    {
                        var s = val?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(s)) return fallback;
                        if (s.Equals("N/A", StringComparison.OrdinalIgnoreCase) || 
                            s.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            return fallback;
                        }
                        return s;
                    }

                    decimal? GetDecimal(string key)
                    {
                        try
                        {
                            using var val = result[key];
                            if (val == null) return null;
                            var s = val.ToString()?.Trim();
                            if (string.IsNullOrEmpty(s) || s.Equals("N/A", StringComparison.OrdinalIgnoreCase) || s.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
                            return Convert.ToDecimal(s);
                        }
                        catch
                        {
                            return null;
                        }
                    }

                    long? GetLong(string key)
                    {
                        try
                        {
                            using var val = result[key];
                            if (val == null) return null;
                            var s = val.ToString()?.Trim();
                            if (string.IsNullOrEmpty(s) || s.Equals("N/A", StringComparison.OrdinalIgnoreCase) || s.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
                            return Convert.ToInt64(s);
                        }
                        catch
                        {
                            return null;
                        }
                    }

                    string? GetString(string key)
                    {
                        using var val = result[key];
                        if (val == null) return null;
                        var s = val.ToString()?.Trim();
                        if (string.IsNullOrEmpty(s) || s.Equals("N/A", StringComparison.OrdinalIgnoreCase) || s.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
                        return s;
                    }

                    using var shortName = result["shortName"];
                    using var longName = result["longName"];
                    using var country = result["country"];
                    using var sector = result["sector"];
                    using var industry = result["industry"];
                    using var currency = result["currency"];
                    using var currentPrice = result["currentPrice"];
                    using var lastClose = result["lastClose"];

                    var rawMeta = new TickerMetadata(
                        Normalize(shortName, ticker),
                        Normalize(longName, ticker),
                        Normalize(country),
                        Normalize(sector),
                        Normalize(industry),
                        Normalize(currency, "USD"),
                        currentPrice != null && currentPrice.ToString() != "None" ? (decimal?)Convert.ToDecimal(currentPrice.ToString()) : null,
                        lastClose != null && lastClose.ToString() != "None" ? (decimal?)Convert.ToDecimal(lastClose.ToString()) : null
                    )
                    {
                        ReturnOnEquity = GetDecimal("returnOnEquity"),
                        ReturnOnAssets = GetDecimal("returnOnAssets"),
                        GrossMargins = GetDecimal("grossMargins"),
                        OperatingMargins = GetDecimal("operatingMargins"),
                        ProfitMargins = GetDecimal("profitMargins"),
                        CurrentRatio = GetDecimal("currentRatio"),
                        DebtToEquity = GetDecimal("debtToEquity"),
                        Ebitda = GetDecimal("ebitda"),
                        FreeCashflow = GetDecimal("freeCashflow"),
                        OperatingCashflow = GetDecimal("operatingCashflow"),
                        TrailingPE = GetDecimal("trailingPE"),
                        ForwardPE = GetDecimal("forwardPE"),
                        PriceToBook = GetDecimal("priceToBook"),
                        TrailingEps = GetDecimal("trailingEps"),
                        ForwardEps = GetDecimal("forwardEps"),
                        BookValue = GetDecimal("bookValue"),
                        SharesOutstanding = GetDecimal("sharesOutstanding"),
                        FloatShares = GetDecimal("floatShares"),
                        ShortRatio = GetDecimal("shortRatio"),
                        ShortPercentOfFloat = GetDecimal("shortPercentOfFloat"),
                        HeldPercentInsiders = GetDecimal("heldPercentInsiders"),
                        HeldPercentInstitutions = GetDecimal("heldPercentInstitutions"),
                        LongBusinessSummary = GetString("longBusinessSummary"),
                        FullTimeEmployees = GetLong("fullTimeEmployees"),
                        FiftyTwoWeekHigh = GetDecimal("fiftyTwoWeekHigh"),
                        FiftyTwoWeekLow = GetDecimal("fiftyTwoWeekLow"),
                        RevenueGrowth = GetDecimal("revenueGrowth"),
                        EarningsGrowth = GetDecimal("earningsGrowth"),
                        EnterpriseValue = GetDecimal("enterpriseValue"),
                        EnterpriseToEbitda = GetDecimal("enterpriseToEbitda"),
                        Beta = GetDecimal("beta"),
                        PayoutRatio = GetDecimal("payoutRatio"),
                        DividendRate = GetDecimal("dividendRate"),
                        DividendYield = GetDecimal("dividendYield"),
                        TotalDebt = GetDecimal("totalDebt"),
                        TotalCash = GetDecimal("totalCash"),
                        TotalRevenue = GetDecimal("totalRevenue"),
                        MarketCap = GetDecimal("marketCap"),
                        QuoteType = GetString("quoteType"),
                        ExchangeTimezoneName = GetString("exchangeTimezoneName"),
                        GmtOffSetMilliseconds = GetLong("gmtOffSetMilliseconds"),
                        ExDividendDate = GetLong("exDividendDate"),
                        LastFiscalYearEnd = GetLong("lastFiscalYearEnd"),
                        MostRecentQuarter = GetLong("mostRecentQuarter"),
                        TargetHighPrice = GetDecimal("targetHighPrice"),
                        TargetLowPrice = GetDecimal("targetLowPrice"),
                        TargetMeanPrice = GetDecimal("targetMeanPrice"),
                        TargetMedianPrice = GetDecimal("targetMedianPrice"),
                        RecommendationKey = GetString("recommendationKey"),
                        RecommendationMean = GetDecimal("recommendationMean"),
                        NumberOfAnalystOpinions = GetLong("numberOfAnalystOpinions"),
                        MetadataLastUpdated = DateTime.UtcNow
                    };

                    return FundamentalsCalculator.CalculateDerived(rawMeta);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch metadata for {Ticker} via Python", ticker);
                    return TickerMetadata.Unknown;
                }
            });

            // Persist successful fetch results
            if (!string.IsNullOrEmpty(meta.ShortName))
            {
                await SaveMetadataToDiskAsync(ticker, meta);
            }

            _metadataCache[ticker] = (meta, DateTime.UtcNow);
            return meta;
        }
        finally
        {
            _metadataSemaphore.Release();
        }
    }

    public Task SaveMetadataAsync(string ticker, TickerMetadata meta)
    {
        _metadataCache[ticker] = (meta, DateTime.UtcNow);
        return SaveMetadataToDiskAsync(ticker, meta);
    }

    private async Task SaveMetadataToDiskAsync(string ticker, TickerMetadata meta)
    {
        if (!Directory.Exists(_metadataPath))
        {
            try { Directory.CreateDirectory(_metadataPath); } catch { /* Ignore creation errors, will fail on save */ }
        }
        var finalPath = Path.Combine(_metadataPath, $"{ticker}.meta.parquet");
        var tempPath = finalPath + ".tmp";
        var duckDbPath = tempPath.Replace("\\", "/");

        try
        {
            using (await _dbManager.AcquireLockAsync("SaveMetadataToDisk"))
            {
                var connection = _dbManager.GetConnection();
                using var command = connection.CreateCommand();

                command.CommandText = $@"
                    COPY (
                        SELECT 
                            ? as {ColShortName}, 
                            ? as {ColLongName}, 
                            ? as {ColRegion}, 
                            ? as {ColSector}, 
                            ? as {ColIndustry}, 
                            ? as {ColCurrency}, 
                            ? as {ColCurrentPrice}, 
                            ? as {ColLastClose},
                            ? as {ColReturnOnEquity},
                            ? as {ColReturnOnAssets},
                            ? as {ColGrossMargins},
                            ? as {ColOperatingMargins},
                            ? as {ColProfitMargins},
                            ? as {ColCurrentRatio},
                            ? as {ColDebtToEquity},
                            ? as {ColEbitda},
                            ? as {ColFreeCashflow},
                            ? as {ColOperatingCashflow},
                            ? as {ColTrailingPE},
                            ? as {ColForwardPE},
                            ? as {ColPriceToBook},
                            ? as {ColTrailingEps},
                            ? as {ColForwardEps},
                            ? as {ColBookValue},
                            ? as {ColSharesOutstanding},
                            ? as {ColFloatShares},
                            ? as {ColShortRatio},
                            ? as {ColShortPercentOfFloat},
                            ? as {ColHeldPercentInsiders},
                            ? as {ColHeldPercentInstitutions},
                            ? as {ColLongBusinessSummary},
                            ? as {ColFullTimeEmployees},
                            ? as {ColFiftyTwoWeekHigh},
                            ? as {ColFiftyTwoWeekLow},
                            ? as {ColRevenueGrowth},
                            ? as {ColEarningsGrowth},
                            ? as {ColEnterpriseValue},
                            ? as {ColEnterpriseToEbitda},
                            ? as {ColBeta},
                            ? as {ColPayoutRatio},
                            ? as {ColDividendRate},
                            ? as {ColDividendYield},
                            ? as {ColTotalDebt},
                            ? as {ColTotalCash},
                            ? as {ColTotalRevenue},
                            ? as {ColMarketCap},
                            ? as {ColPbrCalculated},
                            ? as {ColDividendYieldCalculated},
                            ? as {ColEarningsYield},
                            ? as {ColFcfYield},
                            ? as {ColFcfMargin},
                            ? as {ColNetDebt},
                            ? as {ColNetDebtToEbitda},
                            ? as {ColDividendCoverage},
                            ? as {ColPctFromFiftyTwoWeekHigh},
                            ? as {ColFloatRatio},
                            ? as {ColMarketCapPerEmployee},
                            ? as {ColPegRatio},
                            ? as {ColOperatingCashFlowYield},
                            ? as {ColNetCashRatio},
                            ? as {ColPriceToSalesTrailing12Months},
                            ? as {ColEnterpriseToRevenue},
                            ? as {ColEbitdaMargins},
                            ? as {ColQuickRatio},
                            ? as {ColAverageVolume},
                            ? as {ColPriceToCashFlowRatio},
                            ? as {ColNetDebtEquityRatio},
                            ? as {ColFiftyTwoWeekRangePosition},
                            ? as {ColDailyTurnoverRate},
                            ? as {ColAverageTurnoverRate},
                            ? as {ColDailyFloatShareTurnoverRatio},
                            ? as {ColAverageFloatTurnover},
                            ? as {ColQuoteType},
                            ? as {ColExchangeTimezoneName},
                            ? as {ColGmtOffSetMilliseconds},
                            ? as {ColExDividendDate},
                            ? as {ColLastFiscalYearEnd},
                            ? as {ColMostRecentQuarter},
                            ? as {ColTargetHighPrice},
                            ? as {ColTargetLowPrice},
                            ? as {ColTargetMeanPrice},
                            ? as {ColTargetMedianPrice},
                            ? as {ColRecommendationKey},
                            ? as {ColRecommendationMean},
                            ? as {ColNumberOfAnalystOpinions},
                            ? as {ColMetadataLastUpdated},
                            ? as {ColTag}
                    ) TO '{duckDbPath}' (FORMAT PARQUET)";

                void AddParam(object? value)
                {
                    var p = command.CreateParameter();
                    p.Value = value ?? DBNull.Value;
                    command.Parameters.Add(p);
                }

                AddParam(meta.ShortName);
                AddParam(meta.LongName);
                AddParam(meta.Region);
                AddParam(meta.Sector);
                AddParam(meta.Industry);
                AddParam(meta.Currency);
                AddParam(meta.CurrentPrice);
                AddParam(meta.LastClose);
                AddParam(meta.ReturnOnEquity);
                AddParam(meta.ReturnOnAssets);
                AddParam(meta.GrossMargins);
                AddParam(meta.OperatingMargins);
                AddParam(meta.ProfitMargins);
                AddParam(meta.CurrentRatio);
                AddParam(meta.DebtToEquity);
                AddParam(meta.Ebitda);
                AddParam(meta.FreeCashflow);
                AddParam(meta.OperatingCashflow);
                AddParam(meta.TrailingPE);
                AddParam(meta.ForwardPE);
                AddParam(meta.PriceToBook);
                AddParam(meta.TrailingEps);
                AddParam(meta.ForwardEps);
                AddParam(meta.BookValue);
                AddParam(meta.SharesOutstanding);
                AddParam(meta.FloatShares);
                AddParam(meta.ShortRatio);
                AddParam(meta.ShortPercentOfFloat);
                AddParam(meta.HeldPercentInsiders);
                AddParam(meta.HeldPercentInstitutions);
                AddParam(meta.LongBusinessSummary);
                AddParam(meta.FullTimeEmployees);
                AddParam(meta.FiftyTwoWeekHigh);
                AddParam(meta.FiftyTwoWeekLow);
                AddParam(meta.RevenueGrowth);
                AddParam(meta.EarningsGrowth);
                AddParam(meta.EnterpriseValue);
                AddParam(meta.EnterpriseToEbitda);
                AddParam(meta.Beta);
                AddParam(meta.PayoutRatio);
                AddParam(meta.DividendRate);
                AddParam(meta.DividendYield);
                AddParam(meta.TotalDebt);
                AddParam(meta.TotalCash);
                AddParam(meta.TotalRevenue);
                AddParam(meta.MarketCap);
                AddParam(meta.PbrCalculated);
                AddParam(meta.DividendYieldCalculated);
                AddParam(meta.EarningsYield);
                AddParam(meta.FcfYield);
                AddParam(meta.FcfMargin);
                AddParam(meta.NetDebt);
                AddParam(meta.NetDebtToEbitda);
                AddParam(meta.DividendCoverage);
                AddParam(meta.PctFromFiftyTwoWeekHigh);
                AddParam(meta.FloatRatio);
                AddParam(meta.MarketCapPerEmployee);
                AddParam(meta.PegRatio);
                AddParam(meta.OperatingCashFlowYield);
                AddParam(meta.NetCashRatio);
                AddParam(meta.PriceToSalesTrailing12Months);
                AddParam(meta.EnterpriseToRevenue);
                AddParam(meta.EbitdaMargins);
                AddParam(meta.QuickRatio);
                AddParam(meta.AverageVolume);
                AddParam(meta.PriceToCashFlowRatio);
                AddParam(meta.NetDebtEquityRatio);
                AddParam(meta.FiftyTwoWeekRangePosition);
                AddParam(meta.DailyTurnoverRate);
                AddParam(meta.AverageTurnoverRate);
                AddParam(meta.DailyFloatShareTurnoverRatio);
                AddParam(meta.AverageFloatTurnover);
                AddParam(meta.QuoteType);
                AddParam(meta.ExchangeTimezoneName);
                AddParam(meta.GmtOffSetMilliseconds);
                AddParam(meta.ExDividendDate);
                AddParam(meta.LastFiscalYearEnd);
                AddParam(meta.MostRecentQuarter);
                AddParam(meta.TargetHighPrice);
                AddParam(meta.TargetLowPrice);
                AddParam(meta.TargetMeanPrice);
                AddParam(meta.TargetMedianPrice);
                AddParam(meta.RecommendationKey);
                AddParam(meta.RecommendationMean);
                AddParam(meta.NumberOfAnalystOpinions);
                AddParam(meta.MetadataLastUpdated);
                AddParam(meta.Tag);

                if (command is DbCommand dbCommand)
                {
                    await dbCommand.ExecuteNonQueryAsync();
                }
                else
                {
                    command.ExecuteNonQuery();
                }

                // Atomic Swap with Verification
                var tempInfo = new FileInfo(tempPath);
                if (tempInfo.Exists && tempInfo.Length > 0)
                {
                    if (File.Exists(finalPath))
                    {
                        File.Replace(tempPath, finalPath, null);
                    }
                    else
                    {
                        File.Move(tempPath, finalPath);
                    }
                    _logger.LogTrace("Saved metadata for {Ticker} to disk.", ticker);
                }
                else
                {
                    _logger.LogWarning("Metadata save failed: Temporary file is missing or empty for {Ticker} at {Path}", ticker, tempPath);
                }
            }
        }
        catch (Exception ex)
        {
            // Cleanup incomplete temp file
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            _logger.LogError(ex, "Failed to save metadata to disk for {Ticker}", ticker);
        }
    }

    public async ValueTask<TickerMetadata> GetMetadataAsync(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return TickerMetadata.Unknown;

        // 1. Memory Check
        if (_metadataCache.TryGetValue(ticker, out var cached))
        {
            // Simple TTL: 24h for valid data, 1m for empty/error/fallback results
            var isFallback = string.IsNullOrEmpty(cached.Meta.ShortName) || cached.Meta.ShortName.Equals(ticker, StringComparison.OrdinalIgnoreCase);
            var ttl = isFallback ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(24);
            if (DateTime.UtcNow - cached.Timestamp < ttl)
            {
                return cached.Meta;
            }
        }

        // 2. Disk Check (NEW: Primary local source)
        var diskMeta = await LoadMetadataFromDiskAsync(ticker);
        if (!string.IsNullOrEmpty(diskMeta.ShortName))
        {
            _metadataCache[ticker] = (diskMeta, DateTime.UtcNow);
            return diskMeta;
        }

        // 3. User-Triggered Throttled Python Fetch
        // NOTE: We no longer auto-fetch here as per user constraint.
        // Python fetch is ONLY triggered via manual Sync calls that write back to disk.
        return new TickerMetadata(ticker, ticker, "", "Unknown", "Unknown", "");
    }

    private async Task<TickerMetadata> LoadMetadataFromDiskAsync(string ticker)
    {
        var filePath = Path.Combine(_metadataPath, $"{ticker}.meta.parquet").Replace("\\", "/");
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Metadata file does not exist for {Ticker} at path {Path}", ticker, filePath);
            return TickerMetadata.Unknown;
        }

        try
        {
            using (await _dbManager.AcquireLockAsync("LoadMetadataFromDisk"))
            {
                return await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    var connection = _dbManager.GetConnection();
                    if (connection is not DbConnection dbConnection)
                    {
                        throw new InvalidOperationException("Connection must be a DbConnection.");
                    }

                    using var command = dbConnection.CreateCommand();
                    command.CommandText = $"SELECT * FROM read_parquet('{filePath}') LIMIT 1";

                    using var reader = await command.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        var columnMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            columnMap[reader.GetName(i)] = i;
                        }

                        int GetIdx(string colName)
                        {
                            return columnMap.TryGetValue(colName, out int idx) ? idx : -1;
                        }

                        decimal? GetDecimal(string colName)
                        {
                            int idx = GetIdx(colName);
                            return idx < 0 || reader.IsDBNull(idx) ? null : Convert.ToDecimal(reader.GetValue(idx));
                        }

                        long? GetLong(string colName)
                        {
                            int idx = GetIdx(colName);
                            return idx < 0 || reader.IsDBNull(idx) ? null : Convert.ToInt64(reader.GetValue(idx));
                        }

                        string? GetString(string colName)
                        {
                            int idx = GetIdx(colName);
                            return idx < 0 || reader.IsDBNull(idx) ? null : reader.GetString(idx);
                        }

                        DateTime? GetDateTime(string colName)
                        {
                            int idx = GetIdx(colName);
                            return idx < 0 || reader.IsDBNull(idx) ? null : reader.GetDateTime(idx);
                        }

                        var rawMeta = new TickerMetadata(
                            GetString(ColShortName) ?? ticker,
                            GetString(ColLongName) ?? ticker,
                            GetString(ColRegion) ?? "",
                            GetString(ColSector) ?? "",
                            GetString(ColIndustry) ?? "",
                            GetString(ColCurrency) ?? "USD",
                            GetDecimal(ColCurrentPrice),
                            GetDecimal(ColLastClose)
                        )
                        {
                            ReturnOnEquity = GetDecimal(ColReturnOnEquity),
                            ReturnOnAssets = GetDecimal(ColReturnOnAssets),
                            GrossMargins = GetDecimal(ColGrossMargins),
                            OperatingMargins = GetDecimal(ColOperatingMargins),
                            ProfitMargins = GetDecimal(ColProfitMargins),
                            CurrentRatio = GetDecimal(ColCurrentRatio),
                            DebtToEquity = GetDecimal(ColDebtToEquity),
                            Ebitda = GetDecimal(ColEbitda),
                            FreeCashflow = GetDecimal(ColFreeCashflow),
                            OperatingCashflow = GetDecimal(ColOperatingCashflow),
                            TrailingPE = GetDecimal(ColTrailingPE),
                            ForwardPE = GetDecimal(ColForwardPE),
                            PriceToBook = GetDecimal(ColPriceToBook),
                            TrailingEps = GetDecimal(ColTrailingEps),
                            ForwardEps = GetDecimal(ColForwardEps),
                            BookValue = GetDecimal(ColBookValue),
                            SharesOutstanding = GetDecimal(ColSharesOutstanding),
                            FloatShares = GetDecimal(ColFloatShares),
                            ShortRatio = GetDecimal(ColShortRatio),
                            ShortPercentOfFloat = GetDecimal(ColShortPercentOfFloat),
                            HeldPercentInsiders = GetDecimal(ColHeldPercentInsiders),
                            HeldPercentInstitutions = GetDecimal(ColHeldPercentInstitutions),
                            LongBusinessSummary = GetString(ColLongBusinessSummary),
                            FullTimeEmployees = GetLong(ColFullTimeEmployees),
                            FiftyTwoWeekHigh = GetDecimal(ColFiftyTwoWeekHigh),
                            FiftyTwoWeekLow = GetDecimal(ColFiftyTwoWeekLow),
                            RevenueGrowth = GetDecimal(ColRevenueGrowth),
                            EarningsGrowth = GetDecimal(ColEarningsGrowth),
                            EnterpriseValue = GetDecimal(ColEnterpriseValue),
                            EnterpriseToEbitda = GetDecimal(ColEnterpriseToEbitda),
                            Beta = GetDecimal(ColBeta),
                            PayoutRatio = GetDecimal(ColPayoutRatio),
                            DividendRate = GetDecimal(ColDividendRate),
                            DividendYield = GetDecimal(ColDividendYield),
                            TotalDebt = GetDecimal(ColTotalDebt),
                            TotalCash = GetDecimal(ColTotalCash),
                            TotalRevenue = GetDecimal(ColTotalRevenue),
                            MarketCap = GetDecimal(ColMarketCap),
                            PbrCalculated = GetDecimal(ColPbrCalculated),
                            DividendYieldCalculated = GetDecimal(ColDividendYieldCalculated),
                            EarningsYield = GetDecimal(ColEarningsYield),
                            FcfYield = GetDecimal(ColFcfYield),
                            FcfMargin = GetDecimal(ColFcfMargin),
                            NetDebt = GetDecimal(ColNetDebt),
                            NetDebtToEbitda = GetDecimal(ColNetDebtToEbitda),
                            DividendCoverage = GetDecimal(ColDividendCoverage),
                            PctFromFiftyTwoWeekHigh = GetDecimal(ColPctFromFiftyTwoWeekHigh),
                            FloatRatio = GetDecimal(ColFloatRatio),
                            MarketCapPerEmployee = GetDecimal(ColMarketCapPerEmployee),
                            PegRatio = GetDecimal(ColPegRatio),
                            OperatingCashFlowYield = GetDecimal(ColOperatingCashFlowYield),
                            NetCashRatio = GetDecimal(ColNetCashRatio),
                            PriceToSalesTrailing12Months = GetDecimal(ColPriceToSalesTrailing12Months),
                            EnterpriseToRevenue = GetDecimal(ColEnterpriseToRevenue),
                            EbitdaMargins = GetDecimal(ColEbitdaMargins),
                            QuickRatio = GetDecimal(ColQuickRatio),
                            AverageVolume = GetDecimal(ColAverageVolume),
                            PriceToCashFlowRatio = GetDecimal(ColPriceToCashFlowRatio),
                            NetDebtEquityRatio = GetDecimal(ColNetDebtEquityRatio),
                            FiftyTwoWeekRangePosition = GetDecimal(ColFiftyTwoWeekRangePosition),
                            DailyTurnoverRate = GetDecimal(ColDailyTurnoverRate),
                            AverageTurnoverRate = GetDecimal(ColAverageTurnoverRate),
                            DailyFloatShareTurnoverRatio = GetDecimal(ColDailyFloatShareTurnoverRatio),
                            AverageFloatTurnover = GetDecimal(ColAverageFloatTurnover),
                            QuoteType = GetString(ColQuoteType),
                            ExchangeTimezoneName = GetString(ColExchangeTimezoneName),
                            GmtOffSetMilliseconds = GetLong(ColGmtOffSetMilliseconds),
                            ExDividendDate = GetLong(ColExDividendDate),
                            LastFiscalYearEnd = GetLong(ColLastFiscalYearEnd),
                            MostRecentQuarter = GetLong(ColMostRecentQuarter),
                            TargetHighPrice = GetDecimal(ColTargetHighPrice),
                            TargetLowPrice = GetDecimal(ColTargetLowPrice),
                            TargetMeanPrice = GetDecimal(ColTargetMeanPrice),
                            TargetMedianPrice = GetDecimal(ColTargetMedianPrice),
                            RecommendationKey = GetString(ColRecommendationKey),
                            RecommendationMean = GetDecimal(ColRecommendationMean),
                            NumberOfAnalystOpinions = GetLong(ColNumberOfAnalystOpinions),
                            MetadataLastUpdated = GetDateTime(ColMetadataLastUpdated),
                            Tag = GetString(ColTag)
                        };
                        var finalMeta = FundamentalsCalculator.CalculateDerived(rawMeta);
                        _logger.LogInformation("Successfully loaded metadata for {Ticker} from disk. ShortName: {ShortName}, ROE: {ROE}", ticker, finalMeta.ShortName, finalMeta.ReturnOnEquity);
                        return finalMeta;
                    }
                    _logger.LogWarning("No metadata row found for {Ticker} in Parquet file", ticker);
                    return TickerMetadata.Unknown;
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load metadata from disk for {Ticker}", ticker);
            return TickerMetadata.Unknown;
        }
    }

    public void InvalidateMetadataCache(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;
        _metadataCache.TryRemove(ticker, out _);
    }
}
