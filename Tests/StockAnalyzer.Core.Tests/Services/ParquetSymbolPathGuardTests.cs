using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A ticker name becomes a file name in the data folder. A name that is not a plain file name (separators, "..", reserved characters, device names)
/// must not resolve to a file outside the folder, and the destructive <c>DeleteTickerDataFromDateAsync</c> must refuse it before it touches any file.
/// </summary>
public sealed class ParquetSymbolPathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _victimPath;

    public ParquetSymbolPathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sa_symbol_guard_" + Guid.NewGuid());
        _dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDir);
        // A real parquet file next to (not inside) the data folder: the target a traversal symbol would aim at.
        ParquetFixtureBuilder.SeedDailyOhlcv(_root, "victim");
        _victimPath = Path.Combine(_root, "victim.parquet");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    public static TheoryData<string> UnsafeSymbols => new()
    {
        "../victim", "..\\victim", "sub/victim", "sub\\victim", "C:victim", "CON", "NUL", "a|b", "a*b", "a?b", "victim.", " ",
    };

    [Theory]
    [MemberData(nameof(UnsafeSymbols))]
    public void ResolveFilePath_UnsafeSymbol_ResolvesToNotFound(string symbol)
    {
        Assert.Null(ParquetMarketDataProvider.ResolveFilePath(_dataDir, symbol));
    }

    [Theory]
    [InlineData("../victim")]
    [InlineData("..\\victim")]
    public void ResolveFilePath_TraversalSymbol_DoesNotReachTheFileOutsideTheDataFolder(string symbol)
    {
        Assert.True(File.Exists(_victimPath));

        Assert.Null(ParquetMarketDataProvider.ResolveFilePath(_dataDir, symbol));
    }

    [Theory]
    [InlineData("MSFT", "MSFT")]
    [InlineData("^GSPC", "^GSPC")]
    [InlineData("BRK-B", "BRK-B")]
    [InlineData("7203.T", "7203.T")]
    [InlineData("BRK.B", "BRK-B")]   // the existing '.'/'-' alias lookup is unchanged
    [InlineData("GSPC", "^GSPC")]    // the existing '^' alias lookup is unchanged
    public void ResolveFilePath_OrdinarySymbol_ResolvesAsBefore(string symbol, string existingStem)
    {
        File.WriteAllBytes(Path.Combine(_dataDir, existingStem + ".parquet"), Array.Empty<byte>());

        string? resolved = ParquetMarketDataProvider.ResolveFilePath(_dataDir, symbol);

        Assert.Equal(Path.Combine(_dataDir, existingStem + ".parquet").Replace("\\", "/"), resolved);
    }

    [Theory]
    [InlineData("../victim")]
    [InlineData("..\\victim")]
    [InlineData("sub/victim")]
    public async Task DeleteTickerDataFromDateAsync_UnsafeSymbol_ReturnsZeroAndLeavesTheOutsideFileUntouched(string symbol)
    {
        byte[] before = await File.ReadAllBytesAsync(_victimPath);
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = _dataDir });
        using var dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        var provider = new ParquetMarketDataProvider(dbManager, new MockPythonServiceBase(), settings, NullLogger<ParquetMarketDataProvider>.Instance);

        int deleted = await provider.DeleteTickerDataFromDateAsync(symbol, new DateTime(2024, 1, 1));

        Assert.Equal(0, deleted);
        Assert.Equal(before, await File.ReadAllBytesAsync(_victimPath));
        Assert.False(File.Exists(_victimPath + ".tmp"));
    }
}
