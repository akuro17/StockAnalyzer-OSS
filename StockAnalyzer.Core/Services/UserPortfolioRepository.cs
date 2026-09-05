using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Factories;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

public sealed class UserPortfolioRepository : IUserPortfolioRepository, IDisposable
{
    private readonly string _positionsPath;
    private readonly string _transactionsPath;
    private readonly string _cashPath;
    private readonly string _cashBalancesPath;
    private readonly DuckDBConnectionManager _dbManager;
    private readonly bool _ownsDbManager;
    private readonly ILogger<UserPortfolioRepository> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    public UserPortfolioRepository(string? customPath = null, ILogger<UserPortfolioRepository>? logger = null)
        : this(new DuckDBConnectionManager(), customPath, logger)
    {
        _ownsDbManager = true;
    }

    public UserPortfolioRepository(
        DuckDBConnectionManager dbManager,
        string? customPath = null,
        ILogger<UserPortfolioRepository>? logger = null)
    {
        _logger = logger ?? NullLogger<UserPortfolioRepository>.Instance;
        _dbManager = dbManager;
        _ownsDbManager = false;

        string portfolioDir;
        if (!string.IsNullOrEmpty(customPath))
        {
            string basePath;
            if (Directory.Exists(customPath))
            {
                basePath = customPath;
            }
            else
            {
                basePath = Path.GetDirectoryName(customPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            }
            portfolioDir = Path.Combine(basePath, "Portfolios");
            if (!Directory.Exists(portfolioDir))
            {
                Directory.CreateDirectory(portfolioDir);
            }
            _positionsPath = Path.Combine(portfolioDir, "positions.parquet");
            _transactionsPath = Path.Combine(portfolioDir, "transactions.parquet");
            _cashPath = Path.Combine(portfolioDir, "cash.parquet");
            _cashBalancesPath = Path.Combine(portfolioDir, "cash_balances.parquet");
        }
        else
        {
            _positionsPath = Common.PathDiscovery.ResolvePortfolioPath("positions.parquet");
            _transactionsPath = Common.PathDiscovery.ResolvePortfolioPath("transactions.parquet");
            _cashPath = Common.PathDiscovery.ResolvePortfolioPath("cash.parquet");
            _cashBalancesPath = Common.PathDiscovery.ResolvePortfolioPath("cash_balances.parquet");
        }
    }

    public async ValueTask<Portfolio> LoadPortfolioAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UserPortfolioRepository));
        
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_positionsPath) && File.Exists(_transactionsPath) && File.Exists(_cashPath))
            {
                try
                {
                    var portfolio = await LoadPortfolioFromParquetAsync(ct).ConfigureAwait(false);
                    if (portfolio != null)
                    {
                        return portfolio;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load portfolio from Parquet.");
                }
            }

            return PortfolioFactory.Empty;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async ValueTask SavePortfolioAsync(Portfolio portfolio, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UserPortfolioRepository));
        if (portfolio == null) throw new ArgumentNullException(nameof(portfolio));
        
        ct.ThrowIfCancellationRequested();

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SavePortfolioInternalAsync(portfolio, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task SavePortfolioInternalAsync(Portfolio portfolio, CancellationToken ct)
    {
        _logger.LogInformation("Saving portfolio. Positions path: {PositionsPath}, Transactions path: {TransactionsPath}, Cash path: {CashPath}, CashBalances path: {CashBalancesPath}", _positionsPath, _transactionsPath, _cashPath, _cashBalancesPath);
        var positionsDir = Path.GetDirectoryName(_positionsPath);
        if (!string.IsNullOrEmpty(positionsDir) && !Directory.Exists(positionsDir))
        {
            _logger.LogInformation("Creating directory: {Dir}", positionsDir);
            Directory.CreateDirectory(positionsDir);
        }

        var tempPositions = _positionsPath + ".tmp";
        var tempTransactions = _transactionsPath + ".tmp";
        var tempCash = _cashPath + ".tmp";
        var tempCashBalances = _cashBalancesPath + ".tmp";

        // Cleanup leftovers
        try { if (File.Exists(tempPositions)) File.Delete(tempPositions); } catch { }
        try { if (File.Exists(tempTransactions)) File.Delete(tempTransactions); } catch { }
        try { if (File.Exists(tempCash)) File.Delete(tempCash); } catch { }
        try { if (File.Exists(tempCashBalances)) File.Delete(tempCashBalances); } catch { }

        try
        {
            using (await _dbManager.AcquireLockAsync("SavePortfolio", ct).ConfigureAwait(false))
            {
                var connection = _dbManager.GetConnection();
                if (connection is not DbConnection dbConnection)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
                }

                // A. Save JPY Cash Balance (Legacy)
                using (var command = dbConnection.CreateCommand())
                {
                    command.CommandText = "CREATE TEMP TABLE temp_cash (CashBalance DOUBLE);";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                try
                {
                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = "INSERT INTO temp_cash VALUES ($1);";
                        var p = command.CreateParameter();
                        p.Value = Convert.ToDouble(portfolio.CashBalance);
                        command.Parameters.Add(p);
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = $"COPY temp_cash TO '{tempCash.Replace("\\", "/")}' (FORMAT PARQUET);";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = "DROP TABLE IF EXISTS temp_cash;";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                // A2. Save Multi-Currency Cash Balances
                using (var command = dbConnection.CreateCommand())
                {
                    command.CommandText = "CREATE TEMP TABLE temp_cash_balances (Currency VARCHAR, Balance DOUBLE);";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                try
                {
                    foreach (var kvp in portfolio.CashBalances)
                    {
                        using (var command = dbConnection.CreateCommand())
                        {
                            command.CommandText = "INSERT INTO temp_cash_balances VALUES ($1, $2);";
                            var p1 = command.CreateParameter(); p1.Value = kvp.Key.Value; command.Parameters.Add(p1);
                            var p2 = command.CreateParameter(); p2.Value = Convert.ToDouble(kvp.Value); command.Parameters.Add(p2);
                            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                    }

                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = $"COPY temp_cash_balances TO '{tempCashBalances.Replace("\\", "/")}' (FORMAT PARQUET);";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = "DROP TABLE IF EXISTS temp_cash_balances;";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                // B. Save Positions
                using (var command = dbConnection.CreateCommand())
                {
                    command.CommandText = "CREATE TEMP TABLE temp_positions (Ticker VARCHAR, Quantity DOUBLE, AverageCostPerUnit DOUBLE, IsShort BOOLEAN, AverageCostAmount DOUBLE, AverageCostCurrency VARCHAR);";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                try
                {
                    foreach (var pos in portfolio.Positions.Values)
                    {
                        using (var command = dbConnection.CreateCommand())
                        {
                            command.CommandText = "INSERT INTO temp_positions VALUES ($1, $2, $3, $4, $5, $6);";
                            
                            var p1 = command.CreateParameter(); p1.Value = pos.Ticker; command.Parameters.Add(p1);
                            var p2 = command.CreateParameter(); p2.Value = Convert.ToDouble(pos.Quantity); command.Parameters.Add(p2);
                            var p3 = command.CreateParameter(); p3.Value = Convert.ToDouble(pos.AverageCostPerUnit); command.Parameters.Add(p3);
                            var p4 = command.CreateParameter(); p4.Value = pos.IsShort; command.Parameters.Add(p4);
                            var p5 = command.CreateParameter(); p5.Value = Convert.ToDouble(pos.AverageCost.Amount); command.Parameters.Add(p5);
                            var p6 = command.CreateParameter(); p6.Value = pos.AverageCost.Currency.Value; command.Parameters.Add(p6);
                            
                            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                    }

                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = $"COPY temp_positions TO '{tempPositions.Replace("\\", "/")}' (FORMAT PARQUET);";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = "DROP TABLE IF EXISTS temp_positions;";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                // C. Save Transactions
                using (var command = dbConnection.CreateCommand())
                {
                    command.CommandText = @"CREATE TEMP TABLE temp_transactions (
                        ExecutedAt TIMESTAMP,
                        Type INTEGER,
                        Ticker VARCHAR,
                        Quantity DOUBLE,
                        PricePerUnit DOUBLE,
                        CashAmount DOUBLE,
                        Fee DOUBLE,
                        Notes VARCHAR,
                        TargetPrice DOUBLE,
                        StopLoss DOUBLE,
                        Id VARCHAR,
                        RelatedTransactionId VARCHAR,
                        PriceAmount DOUBLE,
                        PriceCurrency VARCHAR,
                        CommissionAmount DOUBLE,
                        CommissionCurrency VARCHAR,
                        AppliedRateRate DOUBLE,
                        AppliedRateBase VARCHAR,
                        AppliedRateQuote VARCHAR
                    );";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                try
                {
                    foreach (var tx in portfolio.History)
                    {
                        using (var command = dbConnection.CreateCommand())
                        {
                            command.CommandText = "INSERT INTO temp_transactions VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19);";
                            
                            var p1 = command.CreateParameter(); p1.Value = tx.ExecutedAt.UtcDateTime; command.Parameters.Add(p1);
                            var p2 = command.CreateParameter(); p2.Value = (int)tx.Type; command.Parameters.Add(p2);
                            var p3 = command.CreateParameter(); p3.Value = (object?)tx.Ticker ?? DBNull.Value; command.Parameters.Add(p3);
                            var p4 = command.CreateParameter(); p4.Value = Convert.ToDouble(tx.Quantity); command.Parameters.Add(p4);
                            var p5 = command.CreateParameter(); p5.Value = Convert.ToDouble(tx.PricePerUnit); command.Parameters.Add(p5);
                            var p6 = command.CreateParameter(); p6.Value = Convert.ToDouble(tx.CashAmount); command.Parameters.Add(p6);
                            var p7 = command.CreateParameter(); p7.Value = Convert.ToDouble(tx.Fee); command.Parameters.Add(p7);
                            var p8 = command.CreateParameter(); p8.Value = (object?)tx.Notes ?? DBNull.Value; command.Parameters.Add(p8);
                            var p9 = command.CreateParameter(); p9.Value = tx.TargetPrice.HasValue ? Convert.ToDouble(tx.TargetPrice.Value) : DBNull.Value; command.Parameters.Add(p9);
                            var p10 = command.CreateParameter(); p10.Value = tx.StopLoss.HasValue ? Convert.ToDouble(tx.StopLoss.Value) : DBNull.Value; command.Parameters.Add(p10);
                            var p11 = command.CreateParameter(); p11.Value = tx.Id.ToString(); command.Parameters.Add(p11);
                            var p12 = command.CreateParameter(); p12.Value = tx.RelatedTransactionId.HasValue ? tx.RelatedTransactionId.Value.ToString() : DBNull.Value; command.Parameters.Add(p12);
                            var p13 = command.CreateParameter(); p13.Value = Convert.ToDouble(tx.Price.Amount); command.Parameters.Add(p13);
                            var p14 = command.CreateParameter(); p14.Value = tx.Price.Currency.Value; command.Parameters.Add(p14);
                            var p15 = command.CreateParameter(); p15.Value = Convert.ToDouble(tx.Commission.Amount); command.Parameters.Add(p15);
                            var p16 = command.CreateParameter(); p16.Value = tx.Commission.Currency.Value; command.Parameters.Add(p16);
                            var p17 = command.CreateParameter(); p17.Value = tx.AppliedRate.HasValue ? Convert.ToDouble(tx.AppliedRate.Value.Rate) : DBNull.Value; command.Parameters.Add(p17);
                            var p18 = command.CreateParameter(); p18.Value = tx.AppliedRate.HasValue ? tx.AppliedRate.Value.BaseCurrency.Value : DBNull.Value; command.Parameters.Add(p18);
                            var p19 = command.CreateParameter(); p19.Value = tx.AppliedRate.HasValue ? tx.AppliedRate.Value.QuoteCurrency.Value : DBNull.Value; command.Parameters.Add(p19);
                            
                            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                    }

                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = $"COPY temp_transactions TO '{tempTransactions.Replace("\\", "/")}' (FORMAT PARQUET);";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = "DROP TABLE IF EXISTS temp_transactions;";
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
            }

            if (!File.Exists(tempPositions) ||
                !File.Exists(tempTransactions) ||
                !File.Exists(tempCash) ||
                !File.Exists(tempCashBalances))
            {
                throw new InvalidOperationException("Failed to save one or more Parquet files correctly.");
            }

            File.Move(tempPositions, _positionsPath, overwrite: true);
            File.Move(tempTransactions, _transactionsPath, overwrite: true);
            File.Move(tempCash, _cashPath, overwrite: true);
            File.Move(tempCashBalances, _cashBalancesPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception thrown in SavePortfolioInternalAsync");
            try { if (File.Exists(tempPositions)) File.Delete(tempPositions); } catch { }
            try { if (File.Exists(tempTransactions)) File.Delete(tempTransactions); } catch { }
            try { if (File.Exists(tempCash)) File.Delete(tempCash); } catch { }
            try { if (File.Exists(tempCashBalances)) File.Delete(tempCashBalances); } catch { }
            throw;
        }
    }

    private async Task<Portfolio?> LoadPortfolioFromParquetAsync(CancellationToken ct)
    {
        decimal cashBalance = 0m;
        var cashBalances = new Dictionary<CurrencyCode, decimal>();
        var positions = new Dictionary<string, Position>();
        var history = new List<Transaction>();

        using (await _dbManager.AcquireLockAsync("LoadPortfolio", ct).ConfigureAwait(false))
        {
            var connection = _dbManager.GetConnection();
            if (connection is not DbConnection dbConnection)
            {
                throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
            }

            // 1. Read Cash
            if (File.Exists(_cashBalancesPath))
            {
                using (var command = dbConnection.CreateCommand())
                {
                    command.CommandText = $"SELECT Currency, Balance FROM read_parquet('{_cashBalancesPath.Replace("\\", "/")}')";
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var cur = new CurrencyCode(reader.GetString(0));
                        var bal = Convert.ToDecimal(reader.GetValue(1));
                        cashBalances[cur] = bal;
                    }
                }
                cashBalance = cashBalances.GetValueOrDefault(CurrencyCode.JPY, 0m);
            }
            else
            {
                using (var command = dbConnection.CreateCommand())
                {
                    command.CommandText = $"SELECT CashBalance FROM read_parquet('{_cashPath.Replace("\\", "/")}')";
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        cashBalance = Convert.ToDecimal(reader.GetValue(0));
                    }
                }
                cashBalances[CurrencyCode.JPY] = cashBalance;
            }

            // 2. Read Positions
            using (var command = dbConnection.CreateCommand())
            {
                try
                {
                    command.CommandText = $"SELECT Ticker, Quantity, AverageCostPerUnit, IsShort, AverageCostAmount, AverageCostCurrency FROM read_parquet('{_positionsPath.Replace("\\", "/")}')";
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var ticker = reader.GetString(0);
                        var quantity = Convert.ToDecimal(reader.GetValue(1));
                        var avgCost = Convert.ToDecimal(reader.GetValue(2));
                        var isShort = reader.GetBoolean(3);
                        var costAmt = Convert.ToDecimal(reader.GetValue(4));
                        var costCur = new CurrencyCode(reader.GetString(5));
                        var key = ticker + (isShort ? "_Short" : "");
                        positions[key] = new Position(ticker, quantity, avgCost, isShort, new Money(costAmt, costCur));
                    }
                }
                catch
                {
                    // Fallback to legacy
                    command.CommandText = $"SELECT Ticker, Quantity, AverageCostPerUnit, IsShort FROM read_parquet('{_positionsPath.Replace("\\", "/")}')";
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var ticker = reader.GetString(0);
                        var quantity = Convert.ToDecimal(reader.GetValue(1));
                        var avgCost = Convert.ToDecimal(reader.GetValue(2));
                        var isShort = reader.GetBoolean(3);
                        var key = ticker + (isShort ? "_Short" : "");
                        positions[key] = new Position(ticker, quantity, avgCost, isShort);
                    }
                }
            }

            // 3. Read Transactions
            using (var command = dbConnection.CreateCommand())
            {
                try
                {
                    command.CommandText = @"SELECT 
                        ExecutedAt, Type, Ticker, Quantity, PricePerUnit, CashAmount, Fee, Notes, TargetPrice, StopLoss, Id, RelatedTransactionId,
                        PriceAmount, PriceCurrency, CommissionAmount, CommissionCurrency, AppliedRateRate, AppliedRateBase, AppliedRateQuote
                        FROM read_parquet('" + _transactionsPath.Replace("\\", "/") + "')";
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var executedAt = new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero);
                        var type = (TransactionType)reader.GetInt32(1);
                        var ticker = reader.IsDBNull(2) ? null : reader.GetString(2);
                        var quantity = Convert.ToDecimal(reader.GetValue(3));
                        var price = Convert.ToDecimal(reader.GetValue(4));
                        var cash = Convert.ToDecimal(reader.GetValue(5));
                        var fee = Convert.ToDecimal(reader.GetValue(6));
                        var notes = reader.IsDBNull(7) ? null : reader.GetString(7);
                        
                        decimal? targetPrice = reader.IsDBNull(8) ? null : Convert.ToDecimal(reader.GetValue(8));
                        decimal? stopLoss = reader.IsDBNull(9) ? null : Convert.ToDecimal(reader.GetValue(9));
                        
                        var id = Guid.Parse(reader.GetString(10));
                        Guid? relatedId = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11));

                        var prcAmt = Convert.ToDecimal(reader.GetValue(12));
                        var prcCur = new CurrencyCode(reader.GetString(13));
                        var comAmt = Convert.ToDecimal(reader.GetValue(14));
                        var comCur = new CurrencyCode(reader.GetString(15));

                        ExchangeRate? rate = null;
                        if (!reader.IsDBNull(16))
                        {
                            var rateVal = Convert.ToDecimal(reader.GetValue(16));
                            var rateBase = new CurrencyCode(reader.GetString(17));
                            var rateQuote = new CurrencyCode(reader.GetString(18));
                            rate = new ExchangeRate(rateBase, rateQuote, rateVal, executedAt.DateTime);
                        }

                        history.Add(new Transaction(
                            executedAt, type, ticker, quantity, price, cash, fee, notes, targetPrice, stopLoss, id, relatedId,
                            price: new Money(prcAmt, prcCur),
                            commission: new Money(comAmt, comCur),
                            appliedRate: rate
                        ));
                    }
                }
                catch
                {
                    // Fallback to legacy
                    command.CommandText = @"SELECT 
                        ExecutedAt, Type, Ticker, Quantity, PricePerUnit, CashAmount, Fee, Notes, TargetPrice, StopLoss, Id, RelatedTransactionId 
                        FROM read_parquet('" + _transactionsPath.Replace("\\", "/") + "')";
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var executedAt = new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero);
                        var type = (TransactionType)reader.GetInt32(1);
                        var ticker = reader.IsDBNull(2) ? null : reader.GetString(2);
                        var quantity = Convert.ToDecimal(reader.GetValue(3));
                        var price = Convert.ToDecimal(reader.GetValue(4));
                        var cash = Convert.ToDecimal(reader.GetValue(5));
                        var fee = Convert.ToDecimal(reader.GetValue(6));
                        var notes = reader.IsDBNull(7) ? null : reader.GetString(7);
                        
                        decimal? targetPrice = reader.IsDBNull(8) ? null : Convert.ToDecimal(reader.GetValue(8));
                        decimal? stopLoss = reader.IsDBNull(9) ? null : Convert.ToDecimal(reader.GetValue(9));
                        
                        var id = Guid.Parse(reader.GetString(10));
                        Guid? relatedId = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11));

                        history.Add(new Transaction(
                            executedAt, type, ticker, quantity, price, cash, fee, notes, targetPrice, stopLoss, id, relatedId
                        ));
                    }
                }
            }
        }

        return new Portfolio(cashBalance, positions, history, cashBalances: cashBalances);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsDbManager)
        {
            _dbManager.Dispose();
        }
        _ioLock.Dispose();
        _disposed = true;
    }
}
