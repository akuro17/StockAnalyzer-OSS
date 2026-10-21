using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Notes;

/// <summary>
/// Boundary table of <see cref="NoteSelectionTextRules"/> (docs/sa_ai_context_NotesTextSelection section 5.2): one test per row,
/// run against real <see cref="NoteScopeSuggestion"/> records built through their own factories (no doubles).
/// </summary>
public class NoteSelectionTextRulesTests
{
    private static WatchlistNode Watchlist(string name) =>
        new(new WatchlistProfile(Guid.NewGuid(), name, IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, Array.Empty<WatchlistItem>()));

    private static PortfolioNode Portfolio(string name) =>
        new(new WatchlistProfile(Guid.NewGuid(), name, IndicatorColor.FromRgb(255, 0, 0), isPortfolio: true, Array.Empty<WatchlistItem>()));

    private static FilterNode Filter(string name) => new(new FilterSettings { Name = name });

    private static List<NoteScopeSuggestion> Suggestions() => new()
    {
        NoteScopeSuggestion.ForNoTickerSentinel(),
        NoteScopeSuggestion.ForTicker("AAPL"),
        NoteScopeSuggestion.ForTicker("7203.T"),
        NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Watchlist, Watchlist("Semiconductor")),
        NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Portfolio, Portfolio("My Portfolio")),
        NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Filter, Filter("Surging")),
        NoteScopeSuggestion.ForTag("Automotive"),
        NoteScopeSuggestion.ForHashtag("earnings"),
    };

    // ---- boundary table rows (ctx section 5.2) ----

    [Fact]
    public void Evaluate_Null_AllActionsUnavailable()
    {
        var snapshot = NoteSelectionTextRules.Evaluate(null, Suggestions());

        Assert.Equal(string.Empty, snapshot.RawText);
        Assert.Equal(string.Empty, snapshot.ApplyText);
        Assert.Null(snapshot.FilterSuggestion);
        Assert.False(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
        Assert.Equal(NoteSelectionBlockReason.EmptySelection, snapshot.BlockReason);
    }

    [Fact]
    public void Evaluate_EmptyString_AllActionsUnavailable()
    {
        var snapshot = NoteSelectionTextRules.Evaluate(string.Empty, Suggestions());

        Assert.Equal(string.Empty, snapshot.RawText);
        Assert.False(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
    }

    [Fact]
    public void Evaluate_WhitespaceOnly_CopyStaysAvailableButApplyDoesNot()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("   ", Suggestions());

        Assert.Equal("   ", snapshot.RawText);
        Assert.Equal(string.Empty, snapshot.ApplyText);
        Assert.Null(snapshot.FilterSuggestion);
        Assert.True(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
        Assert.Equal(NoteSelectionBlockReason.EmptySelection, snapshot.BlockReason);
    }

    [Fact]
    public void Evaluate_ObjectReplacementCharacterOnly_CopyStaysAvailableButApplyDoesNot()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("\uFFFC", Suggestions());

        Assert.Equal("\uFFFC", snapshot.RawText);
        Assert.Equal(string.Empty, snapshot.ApplyText);
        Assert.True(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
    }

    [Fact]
    public void Evaluate_SingleCharacterWithoutMatchingSuggestion_AppliesToSearchOnly()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("A", Suggestions());

        Assert.Equal("A", snapshot.RawText);
        Assert.Equal("A", snapshot.ApplyText);
        Assert.Null(snapshot.FilterSuggestion);
        Assert.True(snapshot.CanCopy);
        Assert.True(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
    }

    [Fact]
    public void Evaluate_SingleCharacterWithMatchingSuggestion_AppliesToBoth()
    {
        var suggestions = Suggestions();
        suggestions.Add(NoteScopeSuggestion.ForTicker("A"));

        var snapshot = NoteSelectionTextRules.Evaluate("A", suggestions);

        Assert.True(snapshot.CanApplyToSearch);
        Assert.True(snapshot.CanApplyToFilter);
        Assert.Equal(NoteScopeSuggestionKind.Ticker, snapshot.FilterSuggestion!.Kind);
    }

    [Fact]
    public void Evaluate_PaddedLowercaseTicker_TrimsAndResolvesToCanonicalTicker()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("  aapl  ", Suggestions());

        Assert.Equal("  aapl  ", snapshot.RawText);
        Assert.Equal("aapl", snapshot.ApplyText);
        Assert.Equal(NoteScopeSuggestionKind.Ticker, snapshot.FilterSuggestion!.Kind);
        Assert.Equal("AAPL", snapshot.FilterSuggestion.Value);
        Assert.True(snapshot.CanApplyToFilter);
    }

    [Fact]
    public void Evaluate_HashtagWithHashAndDifferentCase_ResolvesToTheHashtag()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("#Earnings", Suggestions());

        Assert.Equal("#Earnings", snapshot.ApplyText);
        Assert.Equal(NoteScopeSuggestionKind.Hashtag, snapshot.FilterSuggestion!.Kind);
        Assert.Equal("earnings", snapshot.FilterSuggestion.Value);
    }

    [Fact]
    public void Evaluate_HashtagWordWithoutHash_DoesNotResolveToTheHashtag()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("Earnings", Suggestions());

        Assert.Equal("Earnings", snapshot.ApplyText);
        Assert.Null(snapshot.FilterSuggestion);
        Assert.True(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u0085b")]
    public void Evaluate_SelectionContainingALineBreak_CopyOnly(string raw)
    {
        var snapshot = NoteSelectionTextRules.Evaluate(raw, Suggestions());

        Assert.Equal(raw, snapshot.RawText);
        Assert.Equal(string.Empty, snapshot.ApplyText);
        Assert.Null(snapshot.FilterSuggestion);
        Assert.True(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
        Assert.Equal(NoteSelectionBlockReason.MultiLine, snapshot.BlockReason);
    }

    [Fact]
    public void Evaluate_LineBreakOnlyAtTheEnds_IsTrimmedAwayAndStillApplies()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("\r\nAAPL\n", Suggestions());

        Assert.Equal("\r\nAAPL\n", snapshot.RawText);
        Assert.Equal("AAPL", snapshot.ApplyText);
        Assert.True(snapshot.CanApplyToSearch);
        Assert.True(snapshot.CanApplyToFilter);
    }

    [Fact]
    public void Evaluate_ObjectReplacementCharactersInsideText_AreRemovedFromTheApplyText()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("A\uFFFCAPL", Suggestions());

        Assert.Equal("A\uFFFCAPL", snapshot.RawText);
        Assert.Equal("AAPL", snapshot.ApplyText);
        Assert.Equal(NoteScopeSuggestionKind.Ticker, snapshot.FilterSuggestion!.Kind);
    }

    [Fact]
    public void Evaluate_NameSharedByTickerAndWatchlist_ResolvesToTheTicker()
    {
        var suggestions = Suggestions();
        suggestions.Insert(0, NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Watchlist, Watchlist("aapl")));

        var snapshot = NoteSelectionTextRules.Evaluate("AAPL", suggestions);

        Assert.Equal(NoteScopeSuggestionKind.Ticker, snapshot.FilterSuggestion!.Kind);
    }

    [Fact]
    public void Evaluate_SameKindSameName_KeepsTheFirstInListOrder()
    {
        var first = NoteScopeSuggestion.ForTicker("aapl");
        var second = NoteScopeSuggestion.ForTicker("AAPL");

        var snapshot = NoteSelectionTextRules.Evaluate("AAPL", new[] { first, second });

        Assert.Same(first, snapshot.FilterSuggestion);
    }

    [Fact]
    public void ResolveFilterSuggestion_NameSharedByEveryKind_FollowsThePriorityOrderRegardlessOfListOrder()
    {
        const string Name = "x";
        var all = new List<NoteScopeSuggestion>
        {
            NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Filter, Filter(Name)),
            NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Portfolio, Portfolio(Name)),
            NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Watchlist, Watchlist(Name)),
            NoteScopeSuggestion.ForTag(Name),
            NoteScopeSuggestion.ForTicker(Name),
        };

        // Hashtag display names carry '#', so they share the name only as "#x".
        var expectedOrder = new[]
        {
            NoteScopeSuggestionKind.Ticker,
            NoteScopeSuggestionKind.Tag,
            NoteScopeSuggestionKind.Watchlist,
            NoteScopeSuggestionKind.Portfolio,
            NoteScopeSuggestionKind.Filter,
        };

        foreach (var expectedKind in expectedOrder)
        {
            var resolved = NoteSelectionTextRules.ResolveFilterSuggestion(Name, all);
            Assert.Equal(expectedKind, resolved!.Kind);
            all.Remove(resolved);
        }

        Assert.Null(NoteSelectionTextRules.ResolveFilterSuggestion(Name, all));

        var hashtagAndTicker = new[] { NoteScopeSuggestion.ForHashtag("x"), NoteScopeSuggestion.ForTicker("#x") };
        Assert.Equal(NoteScopeSuggestionKind.Ticker, NoteSelectionTextRules.ResolveFilterSuggestion("#x", hashtagAndTicker)!.Kind);
        Assert.Equal(NoteScopeSuggestionKind.Hashtag, NoteSelectionTextRules.ResolveFilterSuggestion("#x", hashtagAndTicker.Take(1).ToList())!.Kind);
    }

    [Fact]
    public void ResolveFilterSuggestion_NoTickerSentinelLabel_ResolvesToNoTicker()
    {
        var label = LocalizationManager.Instance["Note_SearchSuggestion_NoTicker"];

        var resolved = NoteSelectionTextRules.ResolveFilterSuggestion(label, Suggestions());

        Assert.Equal(NoteScopeSuggestionKind.NoTicker, resolved!.Kind);
    }

    [Theory]
    [InlineData(NoteScopeSuggestionKind.Ticker, 0)]
    [InlineData(NoteScopeSuggestionKind.Hashtag, 1)]
    [InlineData(NoteScopeSuggestionKind.Tag, 2)]
    [InlineData(NoteScopeSuggestionKind.Watchlist, 3)]
    [InlineData(NoteScopeSuggestionKind.Portfolio, 4)]
    [InlineData(NoteScopeSuggestionKind.Filter, 5)]
    [InlineData(NoteScopeSuggestionKind.NoTicker, 6)]
    public void KindPriority_IsTheDocumentedOrder(NoteScopeSuggestionKind kind, int expected)
    {
        Assert.Equal(expected, NoteSelectionTextRules.KindPriority(kind));
    }

    [Fact]
    public void KindPriority_EveryDefinedKindHasAUniquePriority()
    {
        var kinds = Enum.GetValues<NoteScopeSuggestionKind>();

        Assert.Equal(kinds.Length, kinds.Select(NoteSelectionTextRules.KindPriority).Distinct().Count());
    }

    [Fact]
    public void KindPriority_UndefinedKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteSelectionTextRules.KindPriority((NoteScopeSuggestionKind)99));
    }

    [Fact]
    public void ResolveFilterSuggestion_EmptyList_ReturnsNull()
    {
        Assert.Null(NoteSelectionTextRules.ResolveFilterSuggestion("AAPL", Array.Empty<NoteScopeSuggestion>()));
    }

    // ---- disabled reasons (SA_UI_INTERACTION section 29: null while enabled) ----

    [Fact]
    public void DisabledReasons_AreNullWhileTheActionIsEnabled()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("AAPL", Suggestions());

        Assert.Null(snapshot.CopyDisabledReason);
        Assert.Null(snapshot.SearchDisabledReason);
        Assert.Null(snapshot.FilterDisabledReason);
    }

    [Fact]
    public void DisabledReasons_EmptySelection_ExplainWhy()
    {
        var snapshot = NoteSelectionTextRules.Evaluate(null, Suggestions());
        var expected = LocalizationManager.Instance["Note_Selection_Reason_Empty"];

        Assert.Equal(expected, snapshot.CopyDisabledReason);
        Assert.Equal(expected, snapshot.SearchDisabledReason);
        Assert.Equal(expected, snapshot.FilterDisabledReason);
        Assert.False(string.IsNullOrWhiteSpace(expected));
    }

    [Fact]
    public void DisabledReasons_MultiLine_CopyEnabledApplyExplained()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("a\nb", Suggestions());
        var expected = LocalizationManager.Instance["Note_Selection_Reason_MultiLine"];

        Assert.Null(snapshot.CopyDisabledReason);
        Assert.Equal(expected, snapshot.SearchDisabledReason);
        Assert.Equal(expected, snapshot.FilterDisabledReason);
        Assert.False(string.IsNullOrWhiteSpace(expected));
    }

    [Fact]
    public void DisabledReasons_NoMatch_OnlyTheFilterIsExplained()
    {
        var snapshot = NoteSelectionTextRules.Evaluate("zzz", Suggestions());

        Assert.Null(snapshot.CopyDisabledReason);
        Assert.Null(snapshot.SearchDisabledReason);
        Assert.Equal(LocalizationManager.Instance["Note_Selection_Reason_NoMatch"], snapshot.FilterDisabledReason);
    }

    [Fact]
    public void EvaluateCopyOnly_CopyWorksAndEveryApplyIsExplainedAsUnavailable()
    {
        var snapshot = NoteSelectionTextRules.EvaluateCopyOnly("AAPL");
        var expected = LocalizationManager.Instance["Note_Selection_Reason_Unavailable"];

        Assert.True(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
        Assert.Equal("AAPL", snapshot.RawText);
        Assert.Equal(expected, snapshot.SearchDisabledReason);
        Assert.Equal(expected, snapshot.FilterDisabledReason);
    }

    [Fact]
    public void EmptySnapshot_HasNothingAvailable()
    {
        var snapshot = NoteSelectionSnapshot.Empty;

        Assert.False(snapshot.CanCopy);
        Assert.False(snapshot.CanApplyToSearch);
        Assert.False(snapshot.CanApplyToFilter);
    }

    [Fact]
    public void NormalizeForApply_Boundaries()
    {
        Assert.Equal(string.Empty, NoteSelectionTextRules.NormalizeForApply(null));
        Assert.Equal(string.Empty, NoteSelectionTextRules.NormalizeForApply(string.Empty));
        Assert.Equal("a b", NoteSelectionTextRules.NormalizeForApply("\t a b \u00A0"));
        Assert.Equal("ab", NoteSelectionTextRules.NormalizeForApply("\uFFFCa\uFFFCb\uFFFC"));
    }

    /// <summary>V-7 (plan section 6): Evaluate scans the whole list once per menu opening; with a full-market list (5,000 tickers plus
    /// scope nodes, tags and hashtags) the median of 10 runs of the worst case (no match) must stay under the budget. The budget is a
    /// test-only acceptance threshold from the plan, not a product setting.</summary>
    [Fact]
    public void Evaluate_WorstCaseNoMatchOnAFullMarketList_StaysWithinTheBudget()
    {
        const int TickerCount = 5000;
        const int ScopeAndTagCount = 300;
        const int Runs = 10;
        const double BudgetMilliseconds = 50.0;

        var suggestions = new List<NoteScopeSuggestion> { NoteScopeSuggestion.ForNoTickerSentinel() };
        suggestions.AddRange(Enumerable.Range(0, TickerCount).Select(i => NoteScopeSuggestion.ForTicker($"T{i:D5}.T")));
        suggestions.AddRange(Enumerable.Range(0, ScopeAndTagCount).Select(i => NoteScopeSuggestion.ForTag($"tag{i}")));
        suggestions.AddRange(Enumerable.Range(0, ScopeAndTagCount).Select(i => NoteScopeSuggestion.ForHashtag($"hash{i}")));
        suggestions.AddRange(Enumerable.Range(0, ScopeAndTagCount).Select(i => NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Watchlist, Watchlist($"list{i}"))));

        NoteSelectionTextRules.Evaluate("warm-up", suggestions);

        var timings = new double[Runs];
        for (var i = 0; i < Runs; i++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = NoteSelectionTextRules.Evaluate("no such candidate", suggestions);
            stopwatch.Stop();
            Assert.Null(snapshot.FilterSuggestion);
            timings[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);
        var median = (timings[Runs / 2 - 1] + timings[Runs / 2]) / 2.0;
        Assert.True(median < BudgetMilliseconds, $"median {median:F3} ms exceeds {BudgetMilliseconds} ms");
    }

    [Fact]
    public void EveryReasonLocaleKey_ExistsAndIsNotJustTheKey()
    {
        foreach (var key in new[]
                 {
                     "Note_Selection_Reason_Empty", "Note_Selection_Reason_MultiLine",
                     "Note_Selection_Reason_NoMatch", "Note_Selection_Reason_Unavailable",
                 })
        {
            var text = LocalizationManager.Instance[key];
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
        }
    }
}
