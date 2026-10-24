using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
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
            // A save that died in the middle of the file swap is undone first, so the four files are read as one consistent state.
            try
            {
                RecoverIncompleteSwap();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore the portfolio files after an unfinished save.");
            }

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

    private const string SwapMarkerFileName = "portfolio.swap";
    private const string BackupSuffix = ".bak";

    private string SwapMarkerPath => Path.Combine(Path.GetDirectoryName(_positionsPath)!, SwapMarkerFileName);

    /// <summary>
    /// Replaces the four portfolio files with their freshly written temporary files so that a reader sees either all four old or all four new.
    /// A marker file (the commit point) lists which targets existed before; each existing target is replaced with its old content kept as <c>.bak</c>.
    /// If a replacement fails, or the process dies, <see cref="RecoverIncompleteSwap"/> puts the old content back. The marker is deleted only after the
    /// last replacement, then the backups are removed (best effort: backups without a marker are never used).
    /// </summary>
    private void SwapInTemporaryFiles(IReadOnlyList<(string Temp, string Target)> pairs)
    {
        // A plain write is enough for the marker: it is written before the first replacement, so a marker cut off by a crash belongs to a swap that has
        // changed nothing yet, and recovery ignores cut or unknown lines (Load_MarkerCutOffAtAnyLength_KeepsThePreviousStateAndRemovesTheMarker).
        File.WriteAllLines(SwapMarkerPath, pairs.Select(p => (File.Exists(p.Target) ? "1|" : "0|") + Path.GetFileName(p.Target)));
        try
        {
            foreach (var (temp, target) in pairs)
            {
                if (File.Exists(target))
                {
                    File.Replace(temp, target, target + BackupSuffix);
                }
                else
                {
                    File.Move(temp, target);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swapping the portfolio files failed; restoring the previous state.");
            try
            {
                RecoverIncompleteSwap();
            }
            catch (Exception recoveryException)
            {
                // The marker is still there, so the next load or save repeats the recovery.
                _logger.LogError(recoveryException, "Restoring the previous portfolio state failed; it will be retried on the next load or save.");
            }

            throw;
        }

        File.Delete(SwapMarkerPath);
        foreach (var (_, target) in pairs)
        {
            try { File.Delete(target + BackupSuffix); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete the portfolio backup {BackupFile}.", Path.GetFileName(target) + BackupSuffix); }
        }
    }

    /// <summary>
    /// Undoes a swap that did not finish: with a marker present every listed target that existed before gets its <c>.bak</c> back, every target that did not
    /// exist before is deleted, then the marker goes. Backups left without a marker belong to a swap that did finish and are deleted. Only the four known
    /// file names are ever touched; a marker line naming anything else is ignored.
    /// </summary>
    private void RecoverIncompleteSwap()
    {
        string directory = Path.GetDirectoryName(_positionsPath)!;
        if (!Directory.Exists(directory)) return;   // nothing was ever saved here

        var targets = new[] { _positionsPath, _transactionsPath, _cashPath, _cashBalancesPath }
            .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);

        if (File.Exists(SwapMarkerPath))
        {
            foreach (string line in File.ReadAllLines(SwapMarkerPath))
            {
                int separator = line.IndexOf('|');
                if (separator != 1 || !targets.TryGetValue(line[(separator + 1)..], out string? target)) continue;

                if (line[0] == '1')
                {
                    string backup = target + BackupSuffix;
                    if (File.Exists(backup)) File.Move(backup, target, overwrite: true);
                }
                else if (line[0] == '0' && File.Exists(target))
                {
                    File.Delete(target);
                }
            }

            File.Delete(SwapMarkerPath);
            _logger.LogInformation("Restored the previous portfolio files after an unfinished save ({Directory}).", Path.GetFileName(directory));
        }

        foreach (string target in targets.Values)
        {
            try { File.Delete(target + BackupSuffix); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete the portfolio backup {BackupFile}.", Path.GetFileName(target) + BackupSuffix); }
        }
    }

    /// <summary>
    /// Writes every transaction into <c>temp_transactions</c> with the DuckDB appender (column order and value mapping are those of the table definition).
    /// One statement per row cost about 0.7 ms each (5,000 transactions: 3.5 s); the appender writes the same rows in a few milliseconds.
    /// </summary>
    private static void AppendTransactions(DbConnection dbConnection, IReadOnlyList<Transaction> history)
    {
        if (dbConnection is not DuckDBConnection duckConnection)
        {
            throw new InvalidOperationException("Connection must be a DuckDBConnection to append transactions.");
        }

        using var appender = duckConnection.CreateAppender("temp_transactions");
        foreach (var tx in history)
        {
            var row = appender.CreateRow();
            row.AppendValue(tx.ExecutedAt.UtcDateTime);
            row.AppendValue((int)tx.Type);
            AppendNullable(row, tx.Ticker);
            row.AppendValue(Convert.ToDouble(tx.Quantity));
            row.AppendValue(Convert.ToDouble(tx.PricePerUnit));
            row.AppendValue(Convert.ToDouble(tx.CashAmount));
            row.AppendValue(Convert.ToDouble(tx.Fee));
            AppendNullable(row, tx.Notes);
            AppendNullable(row, tx.TargetPrice);
            AppendNullable(row, tx.StopLoss);
            row.AppendValue(tx.Id.ToString());
            AppendNullable(row, tx.RelatedTransactionId?.ToString());
            row.AppendValue(Convert.ToDouble(tx.Price.Amount));
            row.AppendValue(tx.Price.Currency.Value);
            row.AppendValue(Convert.ToDouble(tx.Commission.Amount));
            row.AppendValue(tx.Commission.Currency.Value);
            AppendNullable(row, tx.AppliedRate?.Rate);
            AppendNullable(row, tx.AppliedRate?.BaseCurrency.Value);
            AppendNullable(row, tx.AppliedRate?.QuoteCurrency.Value);
            row.EndRow();
        }
    }

    private static void AppendNullable(DuckDBAppenderRow row, string? value)
    {
        if (value is null) row.AppendNullValue();
        else row.AppendValue(value);
    }

    private static void AppendNullable(DuckDBAppenderRow row, decimal? value)
    {
        if (value.HasValue) row.AppendValue(Convert.ToDouble(value.Value));
        else row.AppendNullValue();
    }

    private async Task SavePortfolioInternalAsync(Portfolio portfolio, CancellationToken ct)
    {
        RecoverIncompleteSwap();
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
                        command.CommandText = $"COPY temp_cash TO '{FormatDuckDbPath(tempCash)}' (FORMAT PARQUET);";
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
                        command.CommandText = $"COPY temp_cash_balances TO '{FormatDuckDbPath(tempCashBalances)}' (FORMAT PARQUET);";
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
                        command.CommandText = $"COPY temp_positions TO '{FormatDuckDbPath(tempPositions)}' (FORMAT PARQUET);";
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
                    AppendTransactions(dbConnection, portfolio.History);

                    using (var command = dbConnection.CreateCommand())
                    {
                        command.CommandText = $"COPY temp_transactions TO '{FormatDuckDbPath(tempTransactions)}' (FORMAT PARQUET);";
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

            SwapInTemporaryFiles(new[]
            {
                (tempPositions, _positionsPath),
                (tempTransactions, _transactionsPath),
                (tempCash, _cashPath),
                (tempCashBalances, _cashBalancesPath),
            });
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
                    command.CommandText = $"SELECT Currency, Balance FROM read_parquet('{FormatDuckDbPath(_cashBalancesPath)}')";
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
                    command.CommandText = $"SELECT CashBalance FROM read_parquet('{FormatDuckDbPath(_cashPath)}')";
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
                    command.CommandText = $"SELECT Ticker, Quantity, AverageCostPerUnit, IsShort, AverageCostAmount, AverageCostCurrency FROM read_parquet('{FormatDuckDbPath(_positionsPath)}')";
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
                    command.CommandText = $"SELECT Ticker, Quantity, AverageCostPerUnit, IsShort FROM read_parquet('{FormatDuckDbPath(_positionsPath)}')";
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
                    command.CommandText = $@"SELECT 
                        ExecutedAt, Type, Ticker, Quantity, PricePerUnit, CashAmount, Fee, Notes, TargetPrice, StopLoss, Id, RelatedTransactionId,
                        PriceAmount, PriceCurrency, CommissionAmount, CommissionCurrency, AppliedRateRate, AppliedRateBase, AppliedRateQuote
                        FROM read_parquet('{FormatDuckDbPath(_transactionsPath)}')";
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
                    command.CommandText = $@"SELECT 
                        ExecutedAt, Type, Ticker, Quantity, PricePerUnit, CashAmount, Fee, Notes, TargetPrice, StopLoss, Id, RelatedTransactionId 
                        FROM read_parquet('{FormatDuckDbPath(_transactionsPath)}')";
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

    private static string FormatDuckDbPath(string path) =>
        ParquetMarketDataProvider.FormatDuckDbPath(path);
}
