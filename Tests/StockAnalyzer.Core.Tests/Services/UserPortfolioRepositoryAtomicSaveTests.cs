using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A portfolio is stored as four parquet files. A save writes four temporary files and then swaps them in; a failure while swapping must not leave some
/// files new and others old (the loader reads the four as one state). The guarantee: after any save that fails, and after a restart that follows a crash
/// in the middle of the swap, the four files are all the previous state; a save that completes leaves all four new.
/// </summary>
public sealed class UserPortfolioRepositoryAtomicSaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_portfolio_atomic_" + Guid.NewGuid().ToString("N"));
    private readonly string _portfolioDir;
    private readonly string _customPath;

    public UserPortfolioRepositoryAtomicSaveTests()
    {
        Directory.CreateDirectory(_root);
        _customPath = Path.Combine(_root, "portfolio.json");   // a file path: the repository stores into <its folder>\Portfolios
        _portfolioDir = Path.Combine(_root, "Portfolios");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static readonly string[] FileNames = { "positions.parquet", "transactions.parquet", "cash.parquet", "cash_balances.parquet" };

    private static Portfolio StateA() => new(1000m,
        new Dictionary<string, Position> { { "AAPL", new Position("AAPL", 1m, 100m) } });

    private static Portfolio StateB() => new(2000m,
        new Dictionary<string, Position> { { "MSFT", new Position("MSFT", 2m, 400m) } });

    private string PathOf(string fileName) => Path.Combine(_portfolioDir, fileName);

    private string[] LeftoverFiles() => Directory.GetFiles(_portfolioDir)
        .Where(file => file.EndsWith(".bak", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal)
            || Path.GetFileName(file) == "portfolio.swap")
        .Select(Path.GetFileName).OfType<string>().ToArray();

    private static void AssertIsStateA(Portfolio loaded)
    {
        Assert.Equal(1000m, loaded.CashBalance);
        Assert.Contains("AAPL", loaded.Positions.Keys);
        Assert.DoesNotContain("MSFT", loaded.Positions.Keys);
    }

    private static void AssertIsStateB(Portfolio loaded)
    {
        Assert.Equal(2000m, loaded.CashBalance);
        Assert.Contains("MSFT", loaded.Positions.Keys);
        Assert.DoesNotContain("AAPL", loaded.Positions.Keys);
    }

    [Fact]
    public async Task Save_WhenALaterFileCannotBeReplaced_LeavesAllFourFilesInThePreviousState()
    {
        using var repository = new UserPortfolioRepository(_customPath);
        await repository.SavePortfolioAsync(StateA());

        // cash.parquet is the third file swapped in: holding it open makes the swap fail after positions and transactions were already replaced.
        using (new FileStream(PathOf("cash.parquet"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => repository.SavePortfolioAsync(StateB()).AsTask());
        }

        AssertIsStateA(await repository.LoadPortfolioAsync());
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task Save_WhenItCompletes_LeavesAllFourFilesNewAndNoBackupOrMarker()
    {
        using var repository = new UserPortfolioRepository(_customPath);
        await repository.SavePortfolioAsync(StateA());

        await repository.SavePortfolioAsync(StateB());

        AssertIsStateB(await repository.LoadPortfolioAsync());
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task Save_AfterAFailedSave_CanSucceedAgain()
    {
        using var repository = new UserPortfolioRepository(_customPath);
        await repository.SavePortfolioAsync(StateA());
        using (new FileStream(PathOf("cash.parquet"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => repository.SavePortfolioAsync(StateB()).AsTask());
        }

        await repository.SavePortfolioAsync(StateB());

        AssertIsStateB(await repository.LoadPortfolioAsync());
        Assert.Empty(LeftoverFiles());
    }

    /// <summary>The disk state of a process that died after it swapped the first two files in: positions and transactions new, cash and balances old,
    /// the replaced files kept as <c>.bak</c>, and the marker that says a swap was in progress.</summary>
    private async Task ArrangeCrashAfterTwoSwapsAsync()
    {
        using (var repository = new UserPortfolioRepository(_customPath))
        {
            await repository.SavePortfolioAsync(StateA());
        }

        string stash = Path.Combine(_root, "stash_a");
        Directory.CreateDirectory(stash);
        foreach (string name in FileNames) File.Copy(PathOf(name), Path.Combine(stash, name));

        using (var repository = new UserPortfolioRepository(_customPath))
        {
            await repository.SavePortfolioAsync(StateB());
        }

        // Rebuild the crashed disk state from the two complete states: files 1-2 stay new, files 3-4 go back to A, files 1-2 have their A copy as .bak.
        File.Copy(Path.Combine(stash, "positions.parquet"), PathOf("positions.parquet.bak"));
        File.Copy(Path.Combine(stash, "transactions.parquet"), PathOf("transactions.parquet.bak"));
        File.Copy(Path.Combine(stash, "cash.parquet"), PathOf("cash.parquet"), overwrite: true);
        File.Copy(Path.Combine(stash, "cash_balances.parquet"), PathOf("cash_balances.parquet"), overwrite: true);
        File.WriteAllLines(PathOf("portfolio.swap"), FileNames.Select(name => "1|" + name));
    }

    [Fact]
    public async Task Load_AfterACrashInTheMiddleOfTheSwap_RestoresThePreviousState()
    {
        await ArrangeCrashAfterTwoSwapsAsync();
        using var repository = new UserPortfolioRepository(_customPath);

        var loaded = await repository.LoadPortfolioAsync();

        AssertIsStateA(loaded);
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task Save_AfterACrashInTheMiddleOfTheSwap_RecoversFirstAndThenSavesTheNewState()
    {
        await ArrangeCrashAfterTwoSwapsAsync();
        using var repository = new UserPortfolioRepository(_customPath);

        await repository.SavePortfolioAsync(StateB());

        AssertIsStateB(await repository.LoadPortfolioAsync());
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task Load_BackupFilesWithoutAMarker_AreIgnoredAndRemoved()
    {
        // The swap finished (the marker is the commit point and was deleted); only the best-effort cleanup of the backups did not run.
        using (var repository = new UserPortfolioRepository(_customPath))
        {
            await repository.SavePortfolioAsync(StateB());
        }
        foreach (string name in FileNames) File.WriteAllBytes(PathOf(name + ".bak"), new byte[] { 1, 2, 3 });
        using var reloaded = new UserPortfolioRepository(_customPath);

        var loaded = await reloaded.LoadPortfolioAsync();

        AssertIsStateB(loaded);
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task Load_CrashDuringTheFirstEverSave_ReturnsAnEmptyPortfolioAndRemovesTheNewFiles()
    {
        // First save of a fresh portfolio: nothing existed before, so the marker lists the files as new (flag 0); positions was already moved in.
        Directory.CreateDirectory(_portfolioDir);
        File.WriteAllBytes(PathOf("positions.parquet"), new byte[] { 1 });
        File.WriteAllLines(PathOf("portfolio.swap"), FileNames.Select(name => "0|" + name));
        using var repository = new UserPortfolioRepository(_customPath);

        var loaded = await repository.LoadPortfolioAsync();

        Assert.Empty(loaded.Positions);
        Assert.False(File.Exists(PathOf("positions.parquet")));
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task Load_MarkerCutOffAtAnyLength_KeepsThePreviousStateAndRemovesTheMarker()
    {
        // The marker is written before the first file is replaced, so a process that died while writing it has changed nothing yet.
        // Whatever prefix of the marker reached the disk, the four files must still read as the previous state.
        using (var writer = new UserPortfolioRepository(_customPath))
        {
            await writer.SavePortfolioAsync(StateA());
        }

        string fullMarker = string.Join('\n', FileNames.Select(name => "1|" + name)) + "\n";
        using var repository = new UserPortfolioRepository(_customPath);
        for (int length = 0; length <= fullMarker.Length; length++)
        {
            File.WriteAllText(PathOf("portfolio.swap"), fullMarker.Substring(0, length));

            AssertIsStateA(await repository.LoadPortfolioAsync());
            Assert.Empty(LeftoverFiles());
        }
    }

    [Fact]
    public async Task Load_MarkerNamingAnUnknownFile_NeverTouchesThatFile()
    {
        string outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "keep");
        Directory.CreateDirectory(_portfolioDir);
        File.WriteAllLines(PathOf("portfolio.swap"), new[] { "0|..\\outside.txt", "0|../outside.txt", "0|outside.txt" });
        using var repository = new UserPortfolioRepository(_customPath);

        await repository.LoadPortfolioAsync();

        Assert.True(File.Exists(outside));
        Assert.Equal("keep", File.ReadAllText(outside));
    }
}
