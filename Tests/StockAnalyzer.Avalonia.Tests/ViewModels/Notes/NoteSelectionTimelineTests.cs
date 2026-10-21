using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services.Notes;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Notes;

/// <summary>
/// The timeline side of "Apply to Filter" / "Apply to Search" for a selected Note body text
/// (docs/sa_ai_context_NotesTextSelection sections 5.4 and 5.5): <see cref="INoteSelectionTarget"/> as implemented by the real
/// <see cref="NoteTimelineViewModel"/>, and the pass-through on <see cref="NoteTimelineItemViewModel"/>.
/// </summary>
public class NoteSelectionTimelineTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_selection_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static Note MakeNote(string body, ImmutableArray<string>? hashtags = null)
    {
        var now = DateTime.Now;
        return new Note(Guid.NewGuid(), body, now, now) { Hashtags = hashtags ?? ImmutableArray<string>.Empty };
    }

    private static async Task<(NoteTimelineViewModel Timeline, NoteRepository NoteRepository)> CreateLoadedTimelineAsync(string tempDir)
    {
        var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
        var watchlistManager = new FakeWatchlistManager(new[] { watchlist });
        var marketDataProvider = new FakeMarketDataProvider(
            availableTickers: new[] { "7203.T", "9984.T" },
            tickerToTag: new Dictionary<string, string> { ["7203.T"] = "Automotive" });

        var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, watchlistManager, marketDataProvider);
        await noteRepository.CreateAsync(MakeNote("tagged #earnings", ImmutableArray.Create("earnings")));
        await timeline.LoadAvailableFilterOptionsAsync();
        await timeline.RefreshAsync();
        return (timeline, noteRepository);
    }

    [Fact]
    public async Task ApplyToSearch_ReplacesTheSearchTextWithTheTrimmedSelection()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;
            timeline.SearchText = "old";

            target.ApplyToSearch(target.Evaluate("  tagged  "));
            await timeline.RefreshAsync();

            Assert.Equal("tagged", timeline.SearchText);
            Assert.Equal("tagged", timeline.FilterCriteria.SearchText);
            Assert.Single(timeline.DisplayedNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    public async Task ApplyToSearch_UnusableSelection_LeavesTheSearchTextAlone(string raw)
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;
            timeline.SearchText = "keep";

            target.ApplyToSearch(target.Evaluate(raw));

            Assert.Equal("keep", timeline.SearchText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToFilter_Ticker_AddsTheChipWithTheCanonicalSpellingAndKeepsTheSearchText()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;
            timeline.SearchText = "keep";

            var snapshot = target.Evaluate(" 7203.t ");
            Assert.True(snapshot.CanApplyToFilter);
            target.ApplyToFilter(snapshot);

            Assert.Equal(new[] { "7203.T" }, timeline.SelectedTickerCodes);
            Assert.Equal("keep", timeline.SearchText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToFilter_Hashtag_FiltersTheTimelineLikeThePickedSuggestion()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository) = await CreateLoadedTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("plain"));
            await timeline.RefreshAsync();
            Assert.Equal(2, timeline.DisplayedNotes.Count);
            INoteSelectionTarget target = timeline;

            target.ApplyToFilter(target.Evaluate("#Earnings"));
            await timeline.RefreshAsync();

            Assert.Equal("earnings", timeline.FilterCriteria.SelectedHashtag);
            Assert.Equal("#earnings", timeline.SearchBoxDisplayText);
            Assert.Single(timeline.DisplayedNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToFilter_Watchlist_SelectsTheScopeNode()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;

            target.ApplyToFilter(target.Evaluate("semiconductor"));

            Assert.Equal("Semiconductor", timeline.SelectedScopeNode!.DisplayName);
            Assert.Equal("Semiconductor", timeline.SearchBoxDisplayText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToFilter_RegisteredTag_SetsTheTagDimension()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;

            target.ApplyToFilter(target.Evaluate("Automotive"));

            Assert.Equal("Automotive", timeline.SearchBoxDisplayText);
            Assert.NotNull(timeline.FilterCriteria.RegisteredTagTickers);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToFilter_AfterATicker_PickingAHashtagClearsTheTickerChips()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;

            target.ApplyToFilter(target.Evaluate("7203.T"));
            Assert.Single(timeline.SelectedTickerCodes);
            target.ApplyToFilter(target.Evaluate("#earnings"));

            Assert.Empty(timeline.SelectedTickerCodes);
            Assert.Equal("earnings", timeline.FilterCriteria.SelectedHashtag);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToFilter_TwoTickers_AccumulateAsChips()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;

            target.ApplyToFilter(target.Evaluate("7203.T"));
            target.ApplyToFilter(target.Evaluate("9984.T"));

            Assert.Equal(new[] { "7203.T", "9984.T" }, timeline.SelectedTickerCodes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToFilter_NoMatchingCandidate_ChangesNothing()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;
            var criteriaBefore = timeline.FilterCriteria;

            var snapshot = target.Evaluate("no such thing");
            Assert.False(snapshot.CanApplyToFilter);
            Assert.True(snapshot.CanApplyToSearch);
            target.ApplyToFilter(snapshot);

            Assert.Same(criteriaBefore, timeline.FilterCriteria);
            Assert.Empty(timeline.SelectedTickerCodes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyToSearch_DoesNotTouchTheFilterDimensions()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            INoteSelectionTarget target = timeline;
            target.ApplyToFilter(target.Evaluate("7203.T"));

            target.ApplyToSearch(target.Evaluate("tagged"));

            Assert.Equal(new[] { "7203.T" }, timeline.SelectedTickerCodes);
            Assert.Equal("tagged", timeline.SearchText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task DisplayedItem_RelaysSelectionActionsToTheOwningTimeline()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _) = await CreateLoadedTimelineAsync(tempDir);
            var item = timeline.DisplayedNotes.Single();

            var snapshot = item.EvaluateSelection("9984.T");
            Assert.True(snapshot.CanApplyToFilter);
            item.ApplySelectionToFilter(snapshot);
            Assert.Equal(new[] { "9984.T" }, timeline.SelectedTickerCodes);

            item.ApplySelectionToSearch(item.EvaluateSelection("  tagged "));
            Assert.Equal("tagged", timeline.SearchText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ItemWithoutASelectionTarget_OnlyCopyIsAvailableAndApplyIsANoOp()
    {
        var item = new NoteTimelineItemViewModel(
            MakeNote("body"), false, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { });

        var snapshot = item.EvaluateSelection("AAPL");

        Assert.True(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
        Assert.Equal(LocalizationManager.Instance["Note_Selection_Reason_Unavailable"], snapshot.SearchDisabledReason);

        item.ApplySelectionToFilter(snapshot);
        item.ApplySelectionToSearch(snapshot);
    }
}
