using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteFilterEngineTests
{
    private static Note MakeNote(string body, string? ticker = null, DateTime? createdAt = null, ImmutableArray<string>? hashtags = null) =>
        new(Guid.NewGuid(), body, createdAt ?? new DateTime(2026, 8, 12), createdAt ?? new DateTime(2026, 8, 12))
        {
            RelatedTicker = ticker,
            Hashtags = hashtags ?? ImmutableArray<string>.Empty,
        };

    [Fact]
    public void Matches_WithNoActiveFilters_MatchesEveryNote()
    {
        var withTicker = MakeNote("body", "7203.T");
        var withoutTicker = MakeNote("body");

        Assert.True(NoteFilterEngine.Matches(withTicker, NoteFilterCriteria.None));
        Assert.True(NoteFilterEngine.Matches(withoutTicker, NoteFilterCriteria.None));
    }

    [Fact]
    public void Matches_ScopeFilter_IsOrWithinScope()
    {
        var criteria = new NoteFilterCriteria
        {
            ScopeTickers = ImmutableHashSet.Create("6758.T", "9984.T"), // e.g. a Watchlist node resolved to its member tickers
        };

        Assert.True(NoteFilterEngine.Matches(MakeNote("body", "6758.T"), criteria));
        Assert.True(NoteFilterEngine.Matches(MakeNote("body", "9984.T"), criteria));
        Assert.False(NoteFilterEngine.Matches(MakeNote("body", "7203.T"), criteria));
    }

    [Fact]
    public void Matches_ScopeAndRegisteredTag_AreAndedAcrossCategories()
    {
        // Spec 5.1 example: (Scope = 半導体 OR AI) AND (登録タグ = 決算)
        var criteria = new NoteFilterCriteria
        {
            ScopeTickers = ImmutableHashSet.Create("6758.T", "9984.T"),
            RegisteredTagTickers = ImmutableHashSet.Create("9984.T"), // only 9984.T is tagged "決算"
        };

        Assert.True(NoteFilterEngine.Matches(MakeNote("body", "9984.T"), criteria));
        // In the Scope OR-set but not tagged "決算" -> fails the AND with the tag category.
        Assert.False(NoteFilterEngine.Matches(MakeNote("body", "6758.T"), criteria));

        // Tagged "決算" but not in the selected Scope -> also fails (AND requires both).
        var taggedButOutsideScope = new NoteFilterCriteria
        {
            ScopeTickers = ImmutableHashSet.Create("6758.T", "9984.T"),
            RegisteredTagTickers = ImmutableHashSet.Create("7203.T"),
        };
        Assert.False(NoteFilterEngine.Matches(MakeNote("body", "7203.T"), taggedButOutsideScope));
    }

    [Theory]
    [InlineData(true, false, false)]  // Scope active
    [InlineData(false, true, false)]  // RegisteredTag active
    [InlineData(false, false, true)]  // SelectedTicker active
    public void Matches_TickerLessNote_ExcludedFromEachTickerScopedDimension(bool scope, bool tag, bool selected)
    {
        var criteria = new NoteFilterCriteria
        {
            ScopeTickers = scope ? ImmutableHashSet.Create("7203.T") : null,
            RegisteredTagTickers = tag ? ImmutableHashSet.Create("7203.T") : null,
            SelectedTicker = selected ? "7203.T" : null,
        };

        var tickerLessNote = MakeNote("body without a ticker");

        Assert.False(NoteFilterEngine.Matches(tickerLessNote, criteria));
    }

    [Fact]
    public void Matches_NoTickerOnly_MatchesOnlyTickerLessNotes()
    {
        var criteria = new NoteFilterCriteria { NoTickerOnly = true };

        Assert.True(NoteFilterEngine.Matches(MakeNote("body"), criteria));
        Assert.False(NoteFilterEngine.Matches(MakeNote("body", "7203.T"), criteria));
    }

    [Fact]
    public void Matches_NoTickerOnlyCombinedWithTickerScopedFilter_MatchesNothing()
    {
        // A ticker-less Note can never belong to a scope, so ANDing NoTickerOnly with an
        // active Scope dimension is a logically-empty combination - no special-casing needed,
        // the two independent AND terms just both evaluate correctly on their own.
        var criteria = new NoteFilterCriteria
        {
            NoTickerOnly = true,
            ScopeTickers = ImmutableHashSet.Create("7203.T"),
        };

        Assert.False(NoteFilterEngine.Matches(MakeNote("body"), criteria));
        Assert.False(NoteFilterEngine.Matches(MakeNote("body", "7203.T"), criteria));
    }

    [Fact]
    public void Matches_PeriodFilter_UsesCreatedAtOnly()
    {
        var criteria = new NoteFilterCriteria
        {
            PeriodStartInclusive = new DateTime(2026, 8, 5),
            PeriodEndInclusive = new DateTime(2026, 8, 15),
        };

        var inRange = MakeNote("body", createdAt: new DateTime(2026, 8, 10));
        var beforeRange = MakeNote("body", createdAt: new DateTime(2026, 7, 1));
        var afterRange = MakeNote("body", createdAt: new DateTime(2026, 9, 1));

        Assert.True(NoteFilterEngine.Matches(inRange, criteria));
        Assert.False(NoteFilterEngine.Matches(beforeRange, criteria));
        Assert.False(NoteFilterEngine.Matches(afterRange, criteria));
    }

    [Fact]
    public void Matches_SearchText_IsCaseInsensitiveSubstringMatchOnBody()
    {
        var criteria = new NoteFilterCriteria { SearchText = "EV" };

        Assert.True(NoteFilterEngine.Matches(MakeNote("The EV market is growing"), criteria));
        Assert.True(NoteFilterEngine.Matches(MakeNote("the ev market is growing"), criteria));
        Assert.False(NoteFilterEngine.Matches(MakeNote("unrelated content"), criteria));
    }

    [Fact]
    public void Matches_TickerLessNote_StillMatchesPeriodAndSearchFilters()
    {
        // spec 5.1: ticker-independent Notes are excluded only from ticker-scoped filters; period,
        // search and hashtag still apply normally.
        var criteria = new NoteFilterCriteria
        {
            PeriodStartInclusive = new DateTime(2026, 8, 1),
            SearchText = "macro",
        };

        var note = MakeNote("macro outlook, no specific ticker", createdAt: new DateTime(2026, 8, 12));

        Assert.True(NoteFilterEngine.Matches(note, criteria));
    }

    [Fact]
    public void Matches_SelectedHashtag_RequiresExactOrdinalMatchWithinHashtags()
    {
        var criteria = new NoteFilterCriteria { SelectedHashtag = "決算" };

        var tagged = MakeNote("body", hashtags: ImmutableArray.Create("決算", "半導体"));
        var untagged = MakeNote("body", hashtags: ImmutableArray.Create("半導体"));
        var noHashtags = MakeNote("body");

        Assert.True(NoteFilterEngine.Matches(tagged, criteria));
        Assert.False(NoteFilterEngine.Matches(untagged, criteria));
        Assert.False(NoteFilterEngine.Matches(noHashtags, criteria));
    }

    [Fact]
    public void Matches_NoSelectedHashtag_ImposesNoConstraint()
    {
        var criteria = new NoteFilterCriteria { SelectedHashtag = null };

        Assert.True(NoteFilterEngine.Matches(MakeNote("body"), criteria));
        Assert.True(NoteFilterEngine.Matches(MakeNote("body", hashtags: ImmutableArray.Create("決算")), criteria));
    }

    [Fact]
    public void Matches_SelectedHashtagAndScope_AreAndedTogether()
    {
        // (Scope = 9984.T) AND (Hashtag = 決算): a Note tagged 決算 but outside the scope must fail.
        var criteria = new NoteFilterCriteria
        {
            ScopeTickers = ImmutableHashSet.Create("9984.T"),
            SelectedHashtag = "決算",
        };

        var inScopeAndTagged = MakeNote("body", "9984.T", hashtags: ImmutableArray.Create("決算"));
        var inScopeButUntagged = MakeNote("body", "9984.T");
        var taggedButOutOfScope = MakeNote("body", "7203.T", hashtags: ImmutableArray.Create("決算"));

        Assert.True(NoteFilterEngine.Matches(inScopeAndTagged, criteria));
        Assert.False(NoteFilterEngine.Matches(inScopeButUntagged, criteria));
        Assert.False(NoteFilterEngine.Matches(taggedButOutOfScope, criteria));
    }

    [Fact]
    public void Filter_AppliesMatchesAcrossCollection()
    {
        var notes = new[]
        {
            MakeNote("a", "7203.T"),
            MakeNote("b", "9984.T"),
            MakeNote("c"),
        };
        var criteria = new NoteFilterCriteria { SelectedTicker = "7203.T" };

        var result = NoteFilterEngine.Filter(notes, criteria).ToList();

        Assert.Single(result);
        Assert.Equal("a", result[0].Body);
    }
}
