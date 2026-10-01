using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

/// <summary>
/// The one-time conversion of the Notes caches to the full-body format: it runs only while the persistent marker is
/// behind, and the marker advances only after a pass without a failed ticker. Uses the process-wide strategy repository,
/// so it shares the collection of every test that touches its static state.
/// </summary>
[Collection("UserStrategyMetadataRepository MarketDataProvider")]
public class NotesCacheFormatMigrationTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_notes_cacheformat_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static string UniqueTicker() => $"NOTE_FMT_{Guid.NewGuid():N}";

    private sealed record Rig(
        NoteRepository Notes,
        NotesCacheFormatMarker Marker,
        NotesCacheFormatMigration Migration,
        string MarkerPath);

    private static async Task<Rig> CreateAsync(string tempDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        await new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance).InitializeAsync();
        var notes = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var synchronizer = new TickerMetadataNotesCacheSynchronizer(
            notes, UserStrategyMetadataRepository.Instance, NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);
        var marker = new NotesCacheFormatMarker(connectionManager, NullLogger<NotesCacheFormatMarker>.Instance);
        var migration = new NotesCacheFormatMigration(synchronizer, marker, NullLogger<NotesCacheFormatMigration>.Instance);
        return new Rig(notes, marker, migration, Path.Combine(tempDir, NotesCacheFormatMarker.FileName));
    }

    [Fact]
    public async Task RunIfNeededAsync_WithoutMarker_ConvertsOnce_AndLaterRunsDoNothing()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var rig = await CreateAsync(tempDir);
            var ticker = UniqueTicker();
            var fullBody = new string('x', 300);
            await rig.Notes.CreateAsync(new Note(Guid.NewGuid(), fullBody, DateTime.Now, DateTime.Now) { RelatedTicker = ticker });
            UserStrategyMetadataRepository.Instance.SaveStrategy(ticker, null, null, null, null, null, null, new string('x', 150)); // old truncated format
            Assert.Equal(0, rig.Marker.ReadVersion());

            var first = await rig.Migration.RunIfNeededAsync();

            Assert.Equal(new NotesCacheBackfillResult(Total: 1, Rewritten: 1, Failed: 0), first);
            Assert.Equal(fullBody, UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
            Assert.Equal(NotesCacheFormatMigration.CurrentFormatVersion, rig.Marker.ReadVersion());

            // A stale cache after the marker is set is NOT touched again: the conversion runs once, not on every start.
            UserStrategyMetadataRepository.Instance.SaveStrategy(ticker, null, null, null, null, null, null, "stale again");
            var second = await rig.Migration.RunIfNeededAsync();

            Assert.Null(second);
            Assert.Equal("stale again", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RunIfNeededAsync_WhenATickerFails_KeepsTheMarkerBehind_AndTheNextRunRetries()
    {
        var tempDir = CreateIsolatedTempDirectory();
        var failingTicker = UniqueTicker();
        void ThrowForFailingTicker(string changed)
        {
            if (string.Equals(changed, failingTicker, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("subscriber failure");
            }
        }

        try
        {
            var rig = await CreateAsync(tempDir);
            var goodTicker = UniqueTicker();
            await rig.Notes.CreateAsync(new Note(Guid.NewGuid(), "good article", DateTime.Now, DateTime.Now) { RelatedTicker = goodTicker });
            await rig.Notes.CreateAsync(new Note(Guid.NewGuid(), "failing article", DateTime.Now, DateTime.Now) { RelatedTicker = failingTicker });

            UserStrategyMetadataRepository.Instance.StrategyChanged += ThrowForFailingTicker;
            NotesCacheBackfillResult? failedPass;
            try
            {
                failedPass = await rig.Migration.RunIfNeededAsync();
            }
            finally
            {
                UserStrategyMetadataRepository.Instance.StrategyChanged -= ThrowForFailingTicker;
            }

            // One ticker failing neither stops the other one nor advances the marker.
            Assert.Equal(1, failedPass!.Value.Failed);
            Assert.Equal(1, failedPass.Value.Rewritten);
            Assert.Equal("good article", UserStrategyMetadataRepository.Instance.GetStrategy(goodTicker)!.Notes);
            Assert.Equal(0, rig.Marker.ReadVersion());

            var retry = await rig.Migration.RunIfNeededAsync();

            Assert.Equal(0, retry!.Value.Failed);
            Assert.Equal(NotesCacheFormatMigration.CurrentFormatVersion, rig.Marker.ReadVersion());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Marker_WhenTheFileIsMissingOrMalformed_ReadsAsNotConverted()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var rig = await CreateAsync(tempDir);
            Assert.Equal(0, rig.Marker.ReadVersion());

            File.WriteAllText(rig.MarkerPath, "{ this is not json");
            Assert.Equal(0, rig.Marker.ReadVersion());

            File.WriteAllText(rig.MarkerPath, "{\"Version\": -3}");
            Assert.Equal(0, rig.Marker.ReadVersion());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Marker_WriteVersion_RoundTripsAndReplacesAnExistingFileWithoutLeavingATemporaryFile()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var rig = await CreateAsync(tempDir);

            rig.Marker.WriteVersion(1);
            Assert.Equal(1, rig.Marker.ReadVersion());
            rig.Marker.WriteVersion(2);

            Assert.Equal(2, rig.Marker.ReadVersion());
            Assert.False(File.Exists(rig.MarkerPath + ".tmp"));
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Marker.WriteVersion(0));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
