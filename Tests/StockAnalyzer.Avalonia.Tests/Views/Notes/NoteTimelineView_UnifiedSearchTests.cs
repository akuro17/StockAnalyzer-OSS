using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>
/// Real-UI regression coverage for sa_implement Task 3 (unified Ticker/Watchlist/Portfolio/Filter/Tag
/// search box, sa_analysis_report.md §10): mounts the actual NoteTimelineView.axaml so the real
/// AutoCompleteBox - its FilterMode="Custom" binding, the code-behind-wired ItemFilter/ItemSelector,
/// the SearchSuggestionItemTemplate DataTemplate, and the SelectionChanged wiring to
/// NoteTimelineViewModel.SelectSuggestion - are exercised end to end, not just at the ViewModel level.
/// Replaces the old NoteTimelineView_ScopeMenuTests.cs, whose target controls (ScopeMenuButton/
/// ScopeTreeView, the "Tag ▼" ListBox) no longer exist after this task removed them.
///
/// FakeDialogService/FakeWatchlistManager/FakeMarketDataProvider/FakeTickerStateStore, and the
/// NoteTimelineViewModel construction helper itself, live in
/// StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTestDoubles.cs (sa_constraint_check Phase 2: shared
/// across the whole Notes test suite). IDispatcherService uses the project-wide
/// StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService.
/// </summary>
public class NoteTimelineView_UnifiedSearchTests
{
    private static async Task<(NoteTimelineViewModel Timeline, Window Window, StockAnalyzer.Avalonia.Views.Notes.NoteTimelineView View, WatchlistNode WatchlistNode)> MountViewAsync(
        string tempDir, Func<NoteRepository, Task>? seedNotesAsync = null)
    {
        var marketDataProvider = new FakeMarketDataProvider(
            availableTickers: new[] { "7203.T", "9984.T" },
            tickerToTag: new Dictionary<string, string> { ["7203.T"] = "Automotive" });

        var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
        var portfolio = new WatchlistProfile(Guid.NewGuid(), "My Portfolio", IndicatorColor.FromRgb(255, 0, 0), isPortfolio: true,
            new[] { new WatchlistItem("9984.T", DateTimeOffset.UtcNow) });
        var tickerStateStore = new FakeTickerStateStore();
        tickerStateStore.Groups.Add(new AllTickersNode("All Tickers"));
        var watchlistNode = new WatchlistNode(watchlist);
        tickerStateStore.Groups.Add(watchlistNode);
        tickerStateStore.Groups.Add(new PortfolioNode(portfolio));
        tickerStateStore.Groups.Add(new FilterNode(new StockAnalyzer.Core.Models.Settings.FilterSettings { Name = "Surging" }));

        var (timeline, noteRepository, _) = await CreateTimelineAsync(
            tempDir,
            watchlistManager: new FakeWatchlistManager(new[] { watchlist, portfolio }),
            marketDataProvider: marketDataProvider,
            tickerStateStore: tickerStateStore);
        await timeline.LoadAvailableFilterOptionsAsync();

        if (seedNotesAsync is not null)
        {
            await seedNotesAsync(noteRepository);
            // Deterministically populate AvailableHashtags/AvailableSearchSuggestions (rebuilt inside
            // RefreshCoreAsync from activeNotes) before mounting, rather than racing the constructor's
            // own fire-and-forget initial RefreshAsync call.
            await timeline.RefreshAsync();
        }

        var view = new StockAnalyzer.Avalonia.Views.Notes.NoteTimelineView { DataContext = timeline };
        var window = new Window { Content = view, Width = 480, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        return (timeline, window, view, watchlistNode);
    }

    /// <summary>Task A (SAで実装, Note Tab Enhancements) added NoteScopeSuggestionKind.NoTicker as an
    /// always-present sixth suggestion kind (未分類 - notes with no linked ticker), so the box's
    /// ItemsSource now carries six kinds, not five.</summary>
    [AvaloniaFact]
    public async Task SearchSuggestionBox_ItemsSource_ContainsAllSixSuggestionKinds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_unified_search_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (_, window, view, _) = await MountViewAsync(tempDir);
            try
            {
                var searchBox = view.FindControl<AutoCompleteBox>("SearchSuggestionBox");
                Assert.NotNull(searchBox);

                var suggestions = Assert.IsAssignableFrom<IEnumerable<NoteScopeSuggestion>>(searchBox!.ItemsSource).ToList();
                var kinds = suggestions.Select(s => s.Kind).ToHashSet();

                Assert.Equal(
                    new HashSet<NoteScopeSuggestionKind>
                    {
                        NoteScopeSuggestionKind.Ticker,
                        NoteScopeSuggestionKind.Watchlist,
                        NoteScopeSuggestionKind.Portfolio,
                        NoteScopeSuggestionKind.Filter,
                        NoteScopeSuggestionKind.Tag,
                        NoteScopeSuggestionKind.NoTicker,
                    },
                    kinds);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (7th round) fix request #2: the unified search box must filter by
    /// prefix only, matching every other AutoCompleteBox in the app (FilterMode="StartsWith") -
    /// typing a substring that appears mid-string ("conductor", from "Semiconductor") must NOT
    /// surface that suggestion, only a genuine prefix ("Semi") may. Invokes the real ItemFilter
    /// delegate wired onto the mounted AutoCompleteBox in code-behind, not a reimplementation.</summary>
    [AvaloniaFact]
    public async Task SearchSuggestionBox_ItemFilter_MatchesPrefixOnly_NotMidStringSubstring()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_unified_search_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (_, window, view, _) = await MountViewAsync(tempDir);
            try
            {
                var searchBox = view.FindControl<AutoCompleteBox>("SearchSuggestionBox");
                Assert.NotNull(searchBox);
                Assert.NotNull(searchBox!.ItemFilter);

                var suggestions = ((IEnumerable<NoteScopeSuggestion>)searchBox.ItemsSource!).ToList();
                var watchlistSuggestion = suggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Watchlist);
                Assert.Equal("Semiconductor", watchlistSuggestion.DisplayName);

                Assert.False(searchBox.ItemFilter!("conductor", watchlistSuggestion), "mid-string substring must not match");
                Assert.True(searchBox.ItemFilter!("Semi", watchlistSuggestion), "a genuine prefix must still match");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression coverage for the Flyout-reentrancy crash lesson learned earlier this
    /// feature (sa_step_log_NoteFeature.md): the old scope-menu Flyout crashed when a selection was
    /// applied from inside its own popup's event handler. The unified box no longer manually opens/
    /// closes any popup (AutoCompleteBox owns its own dropdown lifecycle), but this proves selecting
    /// a suggestion via the real SelectedItem-set path (exactly what a user's click drives) still
    /// applies correctly and does not crash the process.</summary>
    [AvaloniaFact]
    public async Task SelectingWatchlistSuggestion_UpdatesSelectedScopeNode_AndDoesNotCrash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_unified_search_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view, watchlistNode) = await MountViewAsync(tempDir);
            try
            {
                var searchBox = view.FindControl<AutoCompleteBox>("SearchSuggestionBox");
                Assert.NotNull(searchBox);

                var suggestions = ((IEnumerable<NoteScopeSuggestion>)searchBox!.ItemsSource!).ToList();
                var watchlistSuggestion = suggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Watchlist);

                searchBox.SelectedItem = watchlistSuggestion;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Assert.Same(watchlistNode, timeline.SelectedScopeNode);
                Assert.Equal("Semiconductor", searchBox.Text);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [AvaloniaFact]
    public async Task SelectingTagSuggestion_FiltersTimeline_AndDoesNotCrash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_unified_search_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view, _) = await MountViewAsync(tempDir);
            try
            {
                var searchBox = view.FindControl<AutoCompleteBox>("SearchSuggestionBox");
                Assert.NotNull(searchBox);

                var suggestions = ((IEnumerable<NoteScopeSuggestion>)searchBox!.ItemsSource!).ToList();
                var tagSuggestion = suggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Tag);

                searchBox.SelectedItem = tagSuggestion;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Automotive", searchBox.Text);
                Assert.NotNull(timeline.FilterCriteria.RegisteredTagTickers);
                Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2 (Note Hashtag Filtering), real-UI regression: a Hashtag entry
    /// appears in the real mounted AutoCompleteBox's ItemsSource once a Note carrying that hashtag
    /// exists, and selecting it via the real SelectedItem-set path filters the displayed timeline -
    /// same end-to-end proof as SelectingTagSuggestion_FiltersTimeline_AndDoesNotCrash, but for the
    /// Note-body-derived Hashtag dimension instead of the ticker-level registered Tag.</summary>
    [AvaloniaFact]
    public async Task SelectingHashtagSuggestion_FiltersTimeline_AndDoesNotCrash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_unified_search_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view, _) = await MountViewAsync(tempDir, async noteRepository =>
            {
                await noteRepository.CreateAsync(new Note(Guid.NewGuid(), "tagged", DateTime.Now, DateTime.Now)
                {
                    Hashtags = ImmutableArray.Create("earnings"),
                });
                await noteRepository.CreateAsync(new Note(Guid.NewGuid(), "untagged", DateTime.Now, DateTime.Now));
            });
            try
            {
                var searchBox = view.FindControl<AutoCompleteBox>("SearchSuggestionBox");
                Assert.NotNull(searchBox);

                var suggestions = ((IEnumerable<NoteScopeSuggestion>)searchBox!.ItemsSource!).ToList();
                var hashtagSuggestion = suggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Hashtag);
                Assert.Equal("#earnings", hashtagSuggestion.DisplayName);

                searchBox.SelectedItem = hashtagSuggestion;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("#earnings", searchBox.Text);
                Assert.Equal("earnings", timeline.FilterCriteria.SelectedHashtag);
                Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);

                await timeline.RefreshAsync();
                Assert.Single(timeline.DisplayedNotes);
                Assert.Equal("tagged", timeline.DisplayedNotes[0].Note.Body);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (5th round) fix request #6, real-UI regression: manually clearing the
    /// real AutoCompleteBox's Text (exactly what a user deleting all typed characters does) must
    /// fire the code-behind's TextChanged handler and actually reset the active Watchlist filter -
    /// proving the wiring in NoteTimelineView.axaml.cs, not just the ViewModel-level
    /// ClearActiveSuggestionFilter method in isolation.</summary>
    [AvaloniaFact]
    public async Task ClearingSearchSuggestionBoxTextByHand_ResetsActiveWatchlistFilter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_unified_search_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view, watchlistNode) = await MountViewAsync(tempDir);
            try
            {
                var searchBox = view.FindControl<AutoCompleteBox>("SearchSuggestionBox");
                Assert.NotNull(searchBox);

                var suggestions = ((IEnumerable<NoteScopeSuggestion>)searchBox!.ItemsSource!).ToList();
                var watchlistSuggestion = suggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Watchlist);
                searchBox.SelectedItem = watchlistSuggestion;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Assert.Same(watchlistNode, timeline.SelectedScopeNode);

                // Act: simulate the user deleting all typed characters, exactly as backspacing to
                // empty would - this is what used to leave the old filter silently stuck active.
                searchBox.Text = string.Empty;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
