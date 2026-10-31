using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Constraint check V-2 (SA_ARCHITECTURE_RULES section 3, "Failed Read is not no data"): a portfolio that could not be read must never be replaced by
/// whatever the caller built on top of the empty result. The read still answers with an empty portfolio (contract kept), the next save throws
/// <see cref="IOException"/> and leaves the four files as they were, and a later successful read re-enables saving. A damaged row in the new transaction
/// layout must not make the legacy fallback add the rows it had already read a second time.
/// </summary>
public sealed class UserPortfolioRepositoryLoadFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_portfolio_loadfail_" + Guid.NewGuid().ToString("N"));
    private string PortfolioDir => Path.Combine(_root, "Portfolios");
    private static readonly string[] FileNames = { "positions.parquet", "transactions.parquet", "cash.parquet", "cash_balances.parquet" };

    public UserPortfolioRepositoryLoadFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private UserPortfolioRepository NewRepository() => new(Path.Combine(_root, "portfolio.json"));

    private static Transaction Deposit(DateTimeOffset at, decimal amount) =>
        new(at, TransactionType.Deposit, null, 0m, 0m, amount, 0m, null, null, null, Guid.NewGuid(), null);

    private static Portfolio TwoDeposits() => new(
        30000m,
        history: new List<Transaction> { Deposit(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), 10000m), Deposit(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), 20000m) });

    private static Portfolio OneDeposit() => new(5m, history: new List<Transaction> { Deposit(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), 5m) });

    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(PortfolioDir).ToDictionary(f => Path.GetFileName(f)!, f => File.ReadAllBytes(f));

    private void AssertFilesUnchanged(Dictionary<string, byte[]> expected)
    {
        foreach (var (file, bytes) in expected) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(PortfolioDir, file)));
        Assert.Equal(expected.Keys.OrderBy(f => f), Directory.GetFiles(PortfolioDir).Select(f => Path.GetFileName(f)!).OrderBy(f => f));   // no .tmp, .bak or marker
    }

    [Fact]
    public async Task Save_AfterAReadThatFailedBecauseAFileIsDamaged_Throws_AndKeepsAllFourFiles()
    {
        using var repository = NewRepository();
        await repository.SavePortfolioAsync(TwoDeposits());
        var original = Snapshot();
        File.WriteAllBytes(Path.Combine(PortfolioDir, "positions.parquet"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var damaged = Snapshot();

        var loaded = await repository.LoadPortfolioAsync();

        Assert.Empty(loaded.History);   // the read contract is unchanged: an unreadable store answers with an empty portfolio
        await Assert.ThrowsAsync<IOException>(async () => await repository.SavePortfolioAsync(OneDeposit()));
        AssertFilesUnchanged(damaged);

        File.WriteAllBytes(Path.Combine(PortfolioDir, "positions.parquet"), original["positions.parquet"]);   // the cause is repaired
        var reloaded = await repository.LoadPortfolioAsync();
        Assert.Equal(2, reloaded.History.Count);
        await repository.SavePortfolioAsync(OneDeposit());   // a successful read lifts the block
        Assert.Single((await repository.LoadPortfolioAsync()).History);
    }

    [Fact]
    public async Task Save_AfterAReadThatFailedBecauseAFileIsLocked_Throws_AndKeepsAllFourFiles()
    {
        if (!OperatingSystem.IsWindows()) return;   // an exclusive handle blocks DuckDB's read on Windows

        using var repository = NewRepository();
        await repository.SavePortfolioAsync(TwoDeposits());
        var original = Snapshot();

        using (new FileStream(Path.Combine(PortfolioDir, "transactions.parquet"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = await repository.LoadPortfolioAsync();
            Assert.Empty(loaded.History);
            await Assert.ThrowsAnyAsync<IOException>(async () => await repository.SavePortfolioAsync(OneDeposit()));
        }

        AssertFilesUnchanged(original);
        Assert.Equal(2, (await repository.LoadPortfolioAsync()).History.Count);
    }

    [Fact]
    public async Task Save_WhenNothingWasStoredYet_StillWorks_AfterAnEmptyRead()
    {
        using var repository = NewRepository();

        Assert.Empty((await repository.LoadPortfolioAsync()).History);
        await repository.SavePortfolioAsync(OneDeposit());

        Assert.Single((await repository.LoadPortfolioAsync()).History);
    }

    private static Portfolio UsdTradeAndADeposit() => new(
        30000m,
        history: new List<Transaction>
        {
            new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TransactionType.Long, "AAPL", 10m, 150m, -1500m, 1m, null, null, null, Guid.NewGuid(), null,
                price: new Money(150m, CurrencyCode.USD), commission: new Money(1m, CurrencyCode.USD)),
            Deposit(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), 20000m),
        });

    [Fact]
    public async Task Load_WhenALaterTransactionRowIsDamagedInTheNewLayout_FailsTheReadInsteadOfDroppingTheNewColumns()
    {
        using var repository = NewRepository();
        await repository.SavePortfolioAsync(UsdTradeAndADeposit());
        RewriteTransactionsWithANullCurrencyInTheSecondRow();
        var damaged = Snapshot();

        var loaded = await repository.LoadPortfolioAsync();

        Assert.Empty(loaded.History);   // not a silent read of both rows without the currency columns (which would turn the USD price of row 1 into the default currency)
        await Assert.ThrowsAsync<IOException>(async () => await repository.SavePortfolioAsync(OneDeposit()));
        AssertFilesUnchanged(damaged);
    }

    [Theory]
    [InlineData("positions.parquet")]
    [InlineData("transactions.parquet")]
    [InlineData("cash.parquet")]
    public async Task Load_WhenOnlySomeOfTheThreeCoreFilesExist_IsAFailedRead_AndTheNextSaveKeepsTheRest(string missing)
    {
        using var repository = NewRepository();
        await repository.SavePortfolioAsync(TwoDeposits());
        File.Delete(Path.Combine(PortfolioDir, missing));
        var remaining = Snapshot();

        Assert.Empty((await repository.LoadPortfolioAsync()).History);
        await Assert.ThrowsAsync<IOException>(async () => await repository.SavePortfolioAsync(OneDeposit()));

        AssertFilesUnchanged(remaining);
    }

    [Fact]
    public async Task Load_FilesWrittenBeforeTheMultiCurrencyColumns_AreReadInTheLegacyLayout()
    {
        Directory.CreateDirectory(PortfolioDir);
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            void Run(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            string Target(string name) => Path.Combine(PortfolioDir, name + ".parquet").Replace('\\', '/');

            Run("CREATE TABLE cash (CashBalance DOUBLE); INSERT INTO cash VALUES (1000.5);");
            Run("CREATE TABLE positions (Ticker VARCHAR, Quantity DOUBLE, AverageCostPerUnit DOUBLE, IsShort BOOLEAN); INSERT INTO positions VALUES ('AAPL', 10, 150.25, false);");
            Run("CREATE TABLE transactions (ExecutedAt TIMESTAMP, Type INTEGER, Ticker VARCHAR, Quantity DOUBLE, PricePerUnit DOUBLE, CashAmount DOUBLE, Fee DOUBLE, Notes VARCHAR, " +
                "TargetPrice DOUBLE, StopLoss DOUBLE, Id VARCHAR, RelatedTransactionId VARCHAR);");
            Run($"INSERT INTO transactions VALUES (TIMESTAMP '2026-03-02 09:15:30', {(int)TransactionType.Long}, 'AAPL', 10, 150.25, -1502.5, 1, NULL, NULL, 140, '{Guid.NewGuid()}', NULL);");
            foreach (string table in new[] { "cash", "positions", "transactions" }) Run($"COPY {table} TO '{Target(table)}' (FORMAT PARQUET);");
        }
        using var repository = NewRepository();

        var loaded = await repository.LoadPortfolioAsync();

        Assert.Equal(1000.5m, loaded.CashBalance);
        Assert.Equal(150.25m, loaded.Positions["AAPL"].AverageCostPerUnit);
        var transaction = Assert.Single(loaded.History);
        Assert.Equal(150.25m, transaction.PricePerUnit);
        Assert.Equal(140m, transaction.StopLoss);
        await repository.SavePortfolioAsync(loaded);   // a legacy file is a normal state: saving works and migrates it
        Assert.Single((await repository.LoadPortfolioAsync()).History);
    }

    /// <summary>Row 2 keeps every column except <c>PriceCurrency</c>, which becomes NULL: a read of the new layout fails on that row.</summary>
    private void RewriteTransactionsWithANullCurrencyInTheSecondRow()
    {
        string path = Path.Combine(PortfolioDir, "transactions.parquet");
        string sql = path.Replace('\\', '/');
        string rewritten = sql + ".rewritten";
        using (var connection = new DuckDBConnection("DataSource=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "COPY (SELECT * EXCLUDE (rn) REPLACE (CASE WHEN rn = 2 THEN NULL ELSE PriceCurrency END AS PriceCurrency) " +
                $"FROM (SELECT *, row_number() OVER (ORDER BY ExecutedAt) AS rn FROM read_parquet('{sql}'))) TO '{rewritten}' (FORMAT PARQUET);";
            command.ExecuteNonQuery();
        }

        File.Move(rewritten, path, overwrite: true);
    }
}
