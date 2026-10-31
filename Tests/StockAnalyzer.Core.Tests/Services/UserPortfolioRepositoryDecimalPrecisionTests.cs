using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// IoHydrationWebAiReview T6 (owner D-1 = A): the four portfolio files store every number as DECIMAL(28,10) instead of DOUBLE, so a
/// <c>decimal</c> is never routed through a <c>double</c>. Values are rounded to ten decimals by the storage layer (half away from zero, owner Q-3'),
/// a legacy DOUBLE file reads as 15 significant digits rounded to ten decimals (owner Q-1'), and the calculation side is untouched.
/// The measured DuckDB behaviour behind the rules is in the step log (parameter casts round half away from zero, the appender truncates and accepts
/// out-of-range values silently, 18 integer digits fit).
/// </summary>
[Collection("CultureSensitive")]
public sealed class UserPortfolioRepositoryDecimalPrecisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_portfolio_decimal_" + Guid.NewGuid().ToString("N"));
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private string PortfolioDir => Path.Combine(_root, "Portfolios");
    private string[] FileNames => new[] { "positions.parquet", "transactions.parquet", "cash.parquet", "cash_balances.parquet" };

    public UserPortfolioRepositoryDecimalPrecisionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private UserPortfolioRepository NewRepository() => new(Path.Combine(_root, "portfolio.json"));

    private static readonly DateTime RateDate = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Portfolio TenDecimalPortfolio(Guid transactionId) => new(
        1000000.1234567891m,
        new Dictionary<string, Position>
        {
            ["AAPL"] = new Position("AAPL", 123456789.1234567891m, 0.1234567891m, false, new Money(98765.4321098765m, CurrencyCode.USD)),
        },
        new List<Transaction>
        {
            new(new DateTimeOffset(2026, 3, 2, 9, 15, 30, TimeSpan.Zero), TransactionType.Long, "AAPL", 12.1234567891m, 187.2500000001m, -2340.6250000002m, 5.5000000003m,
                "note", 200.0000000004m, 170.0000000005m, transactionId, null,
                new Money(187.2500000001m, CurrencyCode.USD), new Money(5.5000000003m, CurrencyCode.USD),
                new ExchangeRate(CurrencyCode.USD, CurrencyCode.JPY, 151.5000000006m, RateDate)),
        },
        cashBalances: new Dictionary<CurrencyCode, decimal> { [CurrencyCode.JPY] = 1000000.1234567891m, [CurrencyCode.USD] = 5000.0000000001m });

    private static void AssertSameNumbers(Portfolio expected, Portfolio actual)
    {
        Assert.Equal(expected.CashBalance, actual.CashBalance);
        Assert.Equal(expected.CashBalances.OrderBy(k => k.Key.Value).ToArray(), actual.CashBalances.OrderBy(k => k.Key.Value).ToArray());
        Assert.Equal(expected.Positions.Keys.OrderBy(k => k), actual.Positions.Keys.OrderBy(k => k));
        foreach (var (key, e) in expected.Positions)
        {
            var a = actual.Positions[key];
            Assert.Equal(e.Quantity, a.Quantity);
            Assert.Equal(e.AverageCostPerUnit, a.AverageCostPerUnit);
            Assert.Equal(e.AverageCost.Amount, a.AverageCost.Amount);
        }

        Assert.Equal(expected.History.Count, actual.History.Count);
        for (int i = 0; i < expected.History.Count; i++)
        {
            var e = expected.History[i];
            var a = actual.History[i];
            Assert.Equal(e.Quantity, a.Quantity);
            Assert.Equal(e.PricePerUnit, a.PricePerUnit);
            Assert.Equal(e.CashAmount, a.CashAmount);
            Assert.Equal(e.Fee, a.Fee);
            Assert.Equal(e.TargetPrice, a.TargetPrice);
            Assert.Equal(e.StopLoss, a.StopLoss);
            Assert.Equal(e.Price.Amount, a.Price.Amount);
            Assert.Equal(e.Commission.Amount, a.Commission.Amount);
            Assert.Equal(e.AppliedRate?.Rate, a.AppliedRate?.Rate);
        }
    }

    // --- the two rules ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.1234567891", "0.1234567891")]
    [InlineData("0.12345678915", "0.1234567892")]   // half goes away from zero
    [InlineData("0.12345678914", "0.1234567891")]
    [InlineData("-0.12345678915", "-0.1234567892")]
    [InlineData("0.00000000005", "0.0000000001")]
    [InlineData("0.00000000004", "0")]
    [InlineData("0.3333333333333333333333333333", "0.3333333333")]
    [InlineData("999999999999999999", "999999999999999999")]
    [InlineData("-999999999999999999", "-999999999999999999")]
    public void ToStoredDecimal_RoundsToTenDecimals_HalvesAwayFromZero(string value, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), UserPortfolioRepository.ToStoredDecimal(decimal.Parse(value, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("1000000000000000000")]
    [InlineData("-1000000000000000000")]
    [InlineData("999999999999999999.99999999995")]   // rounds up into the 19th integer digit
    public void ToStoredDecimal_RejectsAnIntegerPartLongerThanEighteenDigits(string value)
    {
        Assert.Throws<OverflowException>(() => UserPortfolioRepository.ToStoredDecimal(decimal.Parse(value, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(0.30000000000000004, "0.3")]                  // 0.1 + 0.2 in binary floating point
    [InlineData(0.3333333333333333, "0.3333333333")]          // 15 significant digits, then ten decimals
    [InlineData(187.25, "187.25")]
    [InlineData(123456789.12345679, "123456789.123457")]       // 15 significant digits leave six decimals here
    [InlineData(0.006666666666666667, "0.0066666667")]        // below 1 the ten decimals are the tighter limit (recorded in the step log)
    public void ReadStoredDecimal_NormalisesALegacyDoubleToFifteenDigitsAndTenDecimals(double legacy, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), UserPortfolioRepository.ReadStoredDecimal(legacy));
    }

    [Fact]
    public void ReadStoredDecimal_LeavesADecimalWithinTenDecimalsUnchanged()
    {
        Assert.Equal(123456789.1234567891m, UserPortfolioRepository.ReadStoredDecimal(123456789.1234567891m));
    }

    // --- the stored files ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveAndLoad_TenDecimalValues_RoundTripExactly_EvenWhereADoubleLosesDigits()
    {
        var original = TenDecimalPortfolio(Guid.NewGuid());
        using var repository = NewRepository();

        await repository.SavePortfolioAsync(original);
        var loaded = await repository.LoadPortfolioAsync();

        AssertSameNumbers(original, loaded);
        Assert.Equal(123456789.1234567891m, loaded.Positions["AAPL"].Quantity);   // 19 significant digits: a double keeps about 16
    }

    [Fact]
    public async Task Save_WritesDecimalColumnsInAllFourFiles()
    {
        using var repository = NewRepository();
        await repository.SavePortfolioAsync(TenDecimalPortfolio(Guid.NewGuid()));

        foreach (string file in FileNames)
        {
            var types = ColumnTypes(Path.Combine(PortfolioDir, file));
            Assert.DoesNotContain("DOUBLE", types.Values);
            Assert.Contains("DECIMAL(28,10)", types.Values);
        }
    }

    [Fact]
    public async Task Save_ValueWithMoreThanTenDecimals_IsStoredRoundedHalfAwayFromZero_ByBothWritePaths()
    {
        var id = Guid.NewGuid();
        var portfolio = new Portfolio(
            0m,
            new Dictionary<string, Position> { ["AAPL"] = new Position("AAPL", 1.00000000005m, 2.00000000015m) },   // parameter path
            new List<Transaction>
            {
                new(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), TransactionType.Long, "AAPL", 3.00000000005m, 4.00000000005m, -1m, 0m, null, null, null, id, null),   // appender path
            });
        using var repository = NewRepository();

        await repository.SavePortfolioAsync(portfolio);
        var loaded = await repository.LoadPortfolioAsync();

        Assert.Equal(1.0000000001m, loaded.Positions["AAPL"].Quantity);
        Assert.Equal(2.0000000002m, loaded.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(3.0000000001m, loaded.History[0].Quantity);          // the appender alone would have truncated this to ...0000
        Assert.Equal(4.0000000001m, loaded.History[0].PricePerUnit);
    }

    [Fact]
    public async Task Load_LegacyDoubleFiles_ReadNormalised_AndTheFirstSaveMigratesThemWithoutChangingTheValues()
    {
        WriteLegacyDoubleFiles();
        using var repository = NewRepository();

        var legacy = await repository.LoadPortfolioAsync();

        Assert.Equal(0.3m, legacy.Positions["AAPL"].Quantity);                       // 0.30000000000000004 in the file
        Assert.Equal(0.3333333333m, legacy.Positions["AAPL"].AverageCostPerUnit);    // 1/3 in the file
        Assert.Equal(187.25m, legacy.Positions["AAPL"].AverageCost.Amount);
        Assert.Equal(1000.5m, legacy.CashBalances[CurrencyCode.JPY]);
        var transaction = Assert.Single(legacy.History);
        Assert.Equal(0.3m, transaction.Quantity);
        Assert.Equal(0.3333333333m, transaction.PricePerUnit);
        Assert.Equal(0.0066666667m, transaction.Fee);
        Assert.Null(transaction.TargetPrice);
        Assert.Equal(170.25m, transaction.StopLoss);
        Assert.Equal(151.5m, transaction.AppliedRate!.Value.Rate);
        foreach (string file in FileNames) Assert.Contains("DOUBLE", ColumnTypes(Path.Combine(PortfolioDir, file)).Values);

        await repository.SavePortfolioAsync(legacy);
        var migrated = await repository.LoadPortfolioAsync();

        AssertSameNumbers(legacy, migrated);   // nothing moves between the read of the legacy file and the read after its first save
        foreach (string file in FileNames) Assert.DoesNotContain("DOUBLE", ColumnTypes(Path.Combine(PortfolioDir, file)).Values);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("transaction")]
    public async Task Save_ValueTooLargeForTheType_Throws_AndLeavesAllFourFilesAsTheyWere(string where)
    {
        using var repository = NewRepository();
        await repository.SavePortfolioAsync(TenDecimalPortfolio(Guid.NewGuid()));
        var before = FileNames.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(PortfolioDir, f)));

        var tooLarge = where == "position"
            ? new Portfolio(0m, new Dictionary<string, Position> { ["AAPL"] = new Position("AAPL", 1000000000000000000m, 1m) })
            : new Portfolio(0m, null, new List<Transaction>
            {
                new(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), TransactionType.Deposit, null, 0m, 0m, 1000000000000000000m, 0m, null, null, null, Guid.NewGuid(), null),
            });

        await Assert.ThrowsAsync<OverflowException>(async () => await repository.SavePortfolioAsync(tooLarge));

        foreach (string file in FileNames) Assert.Equal(before[file], File.ReadAllBytes(Path.Combine(PortfolioDir, file)));
        Assert.Equal(FileNames.OrderBy(f => f), Directory.GetFiles(PortfolioDir).Select(f => Path.GetFileName(f)!).OrderBy(f => f));   // no .tmp, .bak or marker left
        await repository.SavePortfolioAsync(TenDecimalPortfolio(Guid.NewGuid()));   // the store is still usable
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public async Task SaveAndLoad_DoNotDependOnTheThreadCulture(string culture)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        var original = TenDecimalPortfolio(Guid.NewGuid());
        using var repository = NewRepository();

        await repository.SavePortfolioAsync(original);
        var loaded = await repository.LoadPortfolioAsync();

        AssertSameNumbers(original, loaded);
    }

    // --- helpers ---------------------------------------------------------------------------------------------------------------------

    private static string SqlPath(string path) => path.Replace('\\', '/');

    private static Dictionary<string, string> ColumnTypes(string parquetFile)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{SqlPath(parquetFile)}')";
        using var reader = command.ExecuteReader();
        var types = new Dictionary<string, string>();
        while (reader.Read()) types[reader.GetString(0)] = reader.GetString(1);
        return types;
    }

    /// <summary>The four files exactly as the previous version wrote them: every number a DOUBLE.</summary>
    private void WriteLegacyDoubleFiles()
    {
        Directory.CreateDirectory(PortfolioDir);
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();

        void Run(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        const string zeroPointOne = "CAST(0.1 AS DOUBLE)";
        const string zeroPointTwo = "CAST(0.2 AS DOUBLE)";
        const string oneThird = "CAST(1 AS DOUBLE) / 3";
        const string oneOver150 = "CAST(1 AS DOUBLE) / 150";

        Run("CREATE TABLE cash (CashBalance DOUBLE);");
        Run("INSERT INTO cash VALUES (1000.5);");
        Run("CREATE TABLE cash_balances (Currency VARCHAR, Balance DOUBLE);");
        Run("INSERT INTO cash_balances VALUES ('JPY', 1000.5);");
        Run("CREATE TABLE positions (Ticker VARCHAR, Quantity DOUBLE, AverageCostPerUnit DOUBLE, IsShort BOOLEAN, AverageCostAmount DOUBLE, AverageCostCurrency VARCHAR);");
        Run($"INSERT INTO positions VALUES ('AAPL', {zeroPointOne} + {zeroPointTwo}, {oneThird}, false, 187.25, 'USD');");
        Run(@"CREATE TABLE transactions (
                ExecutedAt TIMESTAMP, Type INTEGER, Ticker VARCHAR, Quantity DOUBLE, PricePerUnit DOUBLE, CashAmount DOUBLE, Fee DOUBLE, Notes VARCHAR,
                TargetPrice DOUBLE, StopLoss DOUBLE, Id VARCHAR, RelatedTransactionId VARCHAR, PriceAmount DOUBLE, PriceCurrency VARCHAR,
                CommissionAmount DOUBLE, CommissionCurrency VARCHAR, AppliedRateRate DOUBLE, AppliedRateBase VARCHAR, AppliedRateQuote VARCHAR);");
        Run($@"INSERT INTO transactions VALUES (TIMESTAMP '2026-03-02 09:15:30', {(int)TransactionType.Long}, 'AAPL', {zeroPointOne} + {zeroPointTwo}, {oneThird}, -100.5, {oneOver150}, 'legacy',
                NULL, 170.25, '{Guid.NewGuid()}', NULL, 187.25, 'USD', 5.5, 'USD', 151.5, 'USD', 'JPY');");

        foreach (string table in new[] { "positions", "transactions", "cash", "cash_balances" })
        {
            Run($"COPY {table} TO '{SqlPath(Path.Combine(PortfolioDir, table + ".parquet"))}' (FORMAT PARQUET);");
        }
    }
}
