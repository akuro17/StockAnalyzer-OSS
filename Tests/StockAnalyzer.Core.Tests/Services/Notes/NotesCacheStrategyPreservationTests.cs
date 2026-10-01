using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

/// <summary>
/// Regression coverage for the Notes cache write path erasing a ticker's other strategy fields. At app start the
/// repository's in-memory cache is empty, so a read-modify-write that used the non-waiting GetStrategy saw "no
/// strategy" and saved every field as null - and SaveStrategyMetadataAsync replaces Long/ExitLong/StopLossLong/Short/
/// ExitShort/StopLossShort/Notes/Reminder with what it is given. These tests use a real ParquetMarketDataProvider on a
/// temp directory, which requires assigning the process-wide UserStrategyMetadataRepository.MarketDataProvider, so
/// they share one collection with every other test that touches it.
/// </summary>
[Collection("UserStrategyMetadataRepository MarketDataProvider")]
public class NotesCacheStrategyPreservationTests
{
    private const decimal LongPrice = 100m;
    private const decimal ExitLongPrice = 120m;
    private const decimal StopLossLongPrice = 90m;
    private const string StoredReminder = "Review Q3 earnings call notes";

    /// <summary>How long a write path that does NOT wait for the metadata load gets to (wrongly) write before the
    /// load is released; a waiting implementation is simply still pending on the lock during this window.</summary>
    private static readonly TimeSpan BlockedObservationWindow = TimeSpan.FromSeconds(1);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_notes_strategy_preservation_" + Guid.NewGuid());
        private readonly ParquetMarketDataProvider? _previousProvider = UserStrategyMetadataRepository.MarketDataProvider;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var metadataDir = Path.Combine(_root, "metadata");
            Directory.CreateDirectory(metadataDir);
            var settings = Options.Create(new MarketDataSettings
            {
                DailyDataPath = PathDiscovery.ResolveDataPath(null, "Data/Daily"),
                MetadataPath = metadataDir
            });
            DbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
            Provider = new ParquetMarketDataProvider(DbManager, new Mock<IPythonService>().Object, settings);
            NotesDir = Path.Combine(_root, "notes");
            Directory.CreateDirectory(NotesDir);
        }

        public DuckDBConnectionManager DbManager { get; }
        public ParquetMarketDataProvider Provider { get; }
        public string NotesDir { get; }

        /// <summary>Persists strategy data on disk for <paramref name="ticker"/> and leaves both the provider's
        /// and the repository's in-memory caches empty for it, i.e. the state right after app start.</summary>
        public async Task StoreStrategyOnDiskAsync(string ticker, string legacyNotes)
        {
            var meta = new TickerMetadata(ticker, ticker + " Corp", "US", "Tech", "Software", "USD")
            {
                Long = LongPrice,
                ExitLong = ExitLongPrice,
                StopLossLong = StopLossLongPrice,
                Notes = legacyNotes,
                Reminder = StoredReminder
            };
            await Provider.SaveMetadataAsync(ticker, meta);
            Provider.InvalidateMetadataCache(ticker);
        }

        /// <summary>Puts a file that exists but is not a readable parquet file at the ticker's metadata path, so the
        /// provider's disk read FAILS (as opposed to "no file"), through the same exception path production uses.
        /// Returns the exact bytes written.</summary>
        public byte[] WriteUnreadableMetadataFile(string ticker)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("this is not a parquet file");
            File.WriteAllBytes(MetadataFilePath(ticker), bytes);
            Provider.InvalidateMetadataCache(ticker);
            return bytes;
        }

        public string MetadataFilePath(string ticker) => Path.Combine(_root, "metadata", ticker + ".meta.parquet");

        public void Activate() => UserStrategyMetadataRepository.MarketDataProvider = Provider;

        public void Dispose()
        {
            UserStrategyMetadataRepository.MarketDataProvider = _previousProvider;
            try { Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string UniqueTicker() => $"NOTE_KEEP_{Guid.NewGuid():N}";

    private static async Task<(NoteRepository Notes, TickerMetadataNotesCacheSynchronizer Synchronizer)> CreateSynchronizerAsync(string notesDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, notesDir);
        await new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance).InitializeAsync();
        var notes = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var synchronizer = new TickerMetadataNotesCacheSynchronizer(
            notes, UserStrategyMetadataRepository.Instance, NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);
        return (notes, synchronizer);
    }

    /// <summary>Runs <paramref name="operation"/> while the provider's disk load is held pending (the DuckDB global
    /// lock every metadata load needs is taken here), which is what an app-start load looks like to a caller whose
    /// ticker is not cached yet: <c>GetStrategy</c> returns null right away instead of the stored values.</summary>
    private static async Task RunWithDiskLoadPendingAsync(Fixture fixture, Func<Task> operation)
    {
        var heldLock = await fixture.DbManager.AcquireLockAsync("test: keep the metadata load pending");
        var running = operation();
        await Task.WhenAny(running, Task.Delay(BlockedObservationWindow));
        heldLock.Dispose();
        await running;
    }

    private static void AssertOtherStrategyFieldsPreserved(UserStrategyItem? strategy, string expectedNotes)
    {
        Assert.NotNull(strategy);
        Assert.Equal(LongPrice, strategy!.Long);
        Assert.Equal(ExitLongPrice, strategy.ExitLong);
        Assert.Equal(StopLossLongPrice, strategy.StopLossLong);
        Assert.Equal(StoredReminder, strategy.Reminder);
        Assert.Equal(expectedNotes, strategy.Notes);
    }

    [Fact]
    public async Task RecalculateAllNotesCachesAsync_ForUnloadedTicker_KeepsStoredStrategyFieldsAndReminder()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        var fullBody = new string('x', 300);
        await fixture.StoreStrategyOnDiskAsync(ticker, legacyNotes: new string('x', 150)); // legacy truncated preview
        fixture.Activate();
        var (notes, synchronizer) = await CreateSynchronizerAsync(fixture.NotesDir);
        await notes.CreateAsync(new Note(Guid.NewGuid(), fullBody, DateTime.Now, DateTime.Now) { RelatedTicker = ticker });
        Assert.Null(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker));

        var result = default(NotesCacheBackfillResult);
        await RunWithDiskLoadPendingAsync(fixture, async () => result = await synchronizer.RecalculateAllNotesCachesAsync());

        Assert.Equal(new NotesCacheBackfillResult(Total: 1, Rewritten: 1, Failed: 0), result);
        AssertOtherStrategyFieldsPreserved(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker), fullBody);
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_ForUnloadedTicker_KeepsStoredStrategyFieldsAndReminder()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        await fixture.StoreStrategyOnDiskAsync(ticker, legacyNotes: "old preview");
        fixture.Activate();
        var (notes, synchronizer) = await CreateSynchronizerAsync(fixture.NotesDir);
        await notes.CreateAsync(new Note(Guid.NewGuid(), "brand new article", DateTime.Now, DateTime.Now) { RelatedTicker = ticker });
        Assert.Null(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker));

        await RunWithDiskLoadPendingAsync(fixture, () => synchronizer.RecalculateNotesCacheAsync(ticker));

        AssertOtherStrategyFieldsPreserved(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker), "brand new article");
    }

    /// <summary>How long a (wrong) background write gets to reach the disk before the file is checked; the wait ends
    /// early the moment the file changes, so a correct implementation pays the full window, a broken one none of it.</summary>
    private static readonly TimeSpan WrongWriteObservationWindow = TimeSpan.FromSeconds(3);

    private static bool FileChangedWithin(string path, byte[] originalBytes)
        => SpinWait.SpinUntil(() => !File.ReadAllBytes(path).AsSpan().SequenceEqual(originalBytes), WrongWriteObservationWindow);

    [Fact]
    public async Task RecalculateNotesCacheAsync_WhenTheStoredMetadataCannotBeRead_DoesNotOverwriteTheStoredFile()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        var original = fixture.WriteUnreadableMetadataFile(ticker);
        fixture.Activate();
        var (notes, synchronizer) = await CreateSynchronizerAsync(fixture.NotesDir);
        await notes.CreateAsync(new Note(Guid.NewGuid(), "brand new article", DateTime.Now, DateTime.Now) { RelatedTicker = ticker });

        await synchronizer.RecalculateNotesCacheAsync(ticker); // must not throw into the note-save flow

        Assert.False(FileChangedWithin(fixture.MetadataFilePath(ticker), original),
            "an unreadable metadata file was replaced by a write that erased the other strategy fields");
        Assert.Null(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker));
    }

    [Fact]
    public async Task RecalculateAllNotesCachesAsync_WhenTheStoredMetadataCannotBeRead_DoesNotOverwriteTheStoredFile()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        var original = fixture.WriteUnreadableMetadataFile(ticker);
        fixture.Activate();
        var (notes, synchronizer) = await CreateSynchronizerAsync(fixture.NotesDir);
        await notes.CreateAsync(new Note(Guid.NewGuid(), "brand new article", DateTime.Now, DateTime.Now) { RelatedTicker = ticker });

        await synchronizer.RecalculateAllNotesCachesAsync();

        Assert.False(FileChangedWithin(fixture.MetadataFilePath(ticker), original),
            "an unreadable metadata file was replaced by a write that erased the other strategy fields");
        Assert.Null(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker));
    }

    [Fact]
    public async Task GetStrategyAsync_WhenTheStoredMetadataCannotBeRead_Throws_AndCachesNothing()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        fixture.WriteUnreadableMetadataFile(ticker);
        fixture.Activate();

        var ex = await Assert.ThrowsAsync<StrategyLoadFailedException>(() => UserStrategyMetadataRepository.Instance.GetStrategyAsync(ticker));

        Assert.Equal(ticker, ex.Ticker);
        Assert.Null(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker));
    }

    [Fact]
    public async Task GetStrategyAsync_WhenThereIsNoStoredFile_ReturnsNull_WithoutThrowing()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        fixture.Activate();

        Assert.Null(await UserStrategyMetadataRepository.Instance.GetStrategyAsync(ticker));
    }

    [Fact]
    public async Task RecalculateAllNotesCachesAsync_ConvertsTheReadableTickers_EvenWhenAnotherTickerCannotBeRead()
    {
        using var fixture = new Fixture();
        var readable = UniqueTicker();
        var unreadable = UniqueTicker();
        var fullBody = new string('y', 300);
        await fixture.StoreStrategyOnDiskAsync(readable, legacyNotes: new string('y', 150));
        var original = fixture.WriteUnreadableMetadataFile(unreadable);
        fixture.Activate();
        var (notes, synchronizer) = await CreateSynchronizerAsync(fixture.NotesDir);
        await notes.CreateAsync(new Note(Guid.NewGuid(), fullBody, DateTime.Now, DateTime.Now) { RelatedTicker = readable });
        await notes.CreateAsync(new Note(Guid.NewGuid(), "other article", DateTime.Now, DateTime.Now) { RelatedTicker = unreadable });

        var result = await synchronizer.RecalculateAllNotesCachesAsync();

        Assert.Equal(new NotesCacheBackfillResult(Total: 2, Rewritten: 1, Failed: 1), result);
        AssertOtherStrategyFieldsPreserved(UserStrategyMetadataRepository.Instance.GetCachedStrategy(readable), fullBody);
        Assert.False(FileChangedWithin(fixture.MetadataFilePath(unreadable), original));
    }

    /// <summary>Upper bound for a background write to reach the disk; polling ends the moment the condition holds.</summary>
    private static readonly TimeSpan PersistenceCeiling = TimeSpan.FromSeconds(20);

    private static async Task<decimal?> StoredLongAsync(Fixture fixture, string ticker)
    {
        fixture.Provider.InvalidateMetadataCache(ticker);
        return (await fixture.Provider.TryLoadStoredMetadataAsync(ticker)).Meta.Long;
    }

    [Fact]
    public async Task SaveStrategy_WhenSeveralSavesAreQueued_TheNewestOneIsTheOneOnDisk()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        fixture.Activate();
        var repository = UserStrategyMetadataRepository.Instance;

        // While the DuckDB lock is held every background write waits, so all four saves are queued together.
        var heldLock = await fixture.DbManager.AcquireLockAsync("test: queue the saves");
        for (var price = 1; price <= 4; price++)
        {
            repository.SaveStrategy(ticker, price, null, null, null);
        }
        heldLock.Dispose();

        // Positive condition only: the newest value reaches the disk and stays there.
        var reached = false;
        var deadline = DateTime.UtcNow + PersistenceCeiling;
        while (DateTime.UtcNow < deadline && !reached)
        {
            reached = await StoredLongAsync(fixture, ticker) == 4m;
            if (!reached) await Task.Delay(50);
        }

        Assert.True(reached, $"the newest save (Long = 4) never reached the disk; stored Long = {await StoredLongAsync(fixture, ticker)}");
    }

    [Fact]
    public async Task SetNotesAsync_KeepsEveryOtherField_AndPersistsThem()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        await fixture.StoreStrategyOnDiskAsync(ticker, legacyNotes: "old");
        fixture.Activate();

        await UserStrategyMetadataRepository.Instance.SetNotesAsync(ticker, "fresh");

        AssertOtherStrategyFieldsPreserved(UserStrategyMetadataRepository.Instance.GetCachedStrategy(ticker), "fresh");
        var reached = false;
        var deadline = DateTime.UtcNow + PersistenceCeiling;
        while (DateTime.UtcNow < deadline && !reached)
        {
            fixture.Provider.InvalidateMetadataCache(ticker);
            var stored = (await fixture.Provider.TryLoadStoredMetadataAsync(ticker)).Meta;
            reached = stored.Notes == "fresh" && stored.Long == LongPrice && stored.Reminder == StoredReminder;
            if (!reached) await Task.Delay(50);
        }

        Assert.True(reached, "the new Notes value and the untouched fields did not reach the disk together");
    }

    [Fact]
    public async Task GetStrategyAsync_ForUnloadedTicker_WaitsForDiskLoad()
    {
        using var fixture = new Fixture();
        var ticker = UniqueTicker();
        await fixture.StoreStrategyOnDiskAsync(ticker, legacyNotes: "n");
        fixture.Activate();

        var strategy = await UserStrategyMetadataRepository.Instance.GetStrategyAsync(ticker);

        AssertOtherStrategyFieldsPreserved(strategy, "n");
    }

    [Fact]
    public async Task GetStrategyAsync_WithoutProvider_BehavesLikeGetStrategy()
    {
        var previous = UserStrategyMetadataRepository.MarketDataProvider;
        UserStrategyMetadataRepository.MarketDataProvider = null;
        try
        {
            var ticker = UniqueTicker();
            Assert.Null(await UserStrategyMetadataRepository.Instance.GetStrategyAsync(ticker));

            UserStrategyMetadataRepository.Instance.SaveStrategy(ticker, 1m, 2m, 3m, "cached");
            var cached = await UserStrategyMetadataRepository.Instance.GetStrategyAsync(ticker);

            Assert.Equal("cached", cached!.Notes);
            Assert.Null(await UserStrategyMetadataRepository.Instance.GetStrategyAsync("  "));
        }
        finally
        {
            UserStrategyMetadataRepository.MarketDataProvider = previous;
        }
    }
}
