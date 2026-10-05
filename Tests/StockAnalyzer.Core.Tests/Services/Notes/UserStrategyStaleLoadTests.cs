#nullable enable
using System;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

/// <summary>
/// A disk read that began before a save must never replace what the save put in the cache. The background persist of a save reads
/// the stored file first (<c>SaveStrategyMetadataAsync</c> asks for the existing metadata), and with a cold provider cache that read
/// registered the OLD stored strategy over the new one; the Notes column and the next read-modify-write then saw stale data.
/// (Found as the intermittent NotesCacheStrategyPreservationTests failure: expected the new note, got the 150 character legacy preview.)
/// </summary>
[Collection("UserStrategyMetadataRepository MarketDataProvider")]
public class UserStrategyStaleLoadTests
{
    private static string UniqueTicker() => $"STALE_LOAD_{Guid.NewGuid():N}";

    [Fact]
    public void ALoadRegisteredAfterASave_NeverReplacesTheSavedValues()
    {
        var repo = UserStrategyMetadataRepository.Instance;
        var ticker = UniqueTicker();
        repo.SaveStrategy(ticker, 100m, 120m, 90m, null, null, null, "new note", reminder: "new reminder");

        repo.RegisterLoadedStrategy(ticker, 50m, 60m, 40m, null, null, null, "old preview", reminder: "old reminder");

        var cached = repo.GetCachedStrategy(ticker)!;
        Assert.Equal((100m, 120m, 90m, "new note", "new reminder"), (cached.Long, cached.ExitLong, cached.StopLossLong, cached.Notes, cached.Reminder));
    }

    [Fact]
    public void AValueTheSaveCleared_IsNotBroughtBackByALateLoad()
    {
        var repo = UserStrategyMetadataRepository.Instance;
        var ticker = UniqueTicker();
        repo.SaveStrategy(ticker, 100m, null, null, null, null, null, null);

        repo.RegisterLoadedStrategy(ticker, 100m, 120m, 90m, null, null, null, "old preview");

        var cached = repo.GetCachedStrategy(ticker)!;
        Assert.Equal((100m, (decimal?)null, (decimal?)null, (string?)null), (cached.Long, cached.ExitLong, cached.StopLossLong, cached.Notes));
    }

    [Fact]
    public async Task ALoadRegisteredAfterSetNotes_NeverReplacesTheNewNote()
    {
        var repo = UserStrategyMetadataRepository.Instance;
        var ticker = UniqueTicker();
        await repo.SetNotesAsync(ticker, "new note");

        repo.RegisterLoadedStrategy(ticker, null, null, null, null, null, null, "old preview");

        Assert.Equal("new note", repo.GetCachedStrategy(ticker)!.Notes);
    }

    [Fact]
    public void WithoutASave_ALoadStillHydratesAndAReloadStillPicksUpTheFile()
    {
        var repo = UserStrategyMetadataRepository.Instance;
        var ticker = UniqueTicker();
        repo.RegisterLoadedStrategy(ticker, 100m, 120m, 90m, null, null, null, "first read");
        Assert.Equal("first read", repo.GetCachedStrategy(ticker)!.Notes);

        repo.RegisterLoadedStrategy(ticker, 100m, 120m, 90m, null, null, null, "file changed outside");

        Assert.Equal("file changed outside", repo.GetCachedStrategy(ticker)!.Notes);
    }
}
