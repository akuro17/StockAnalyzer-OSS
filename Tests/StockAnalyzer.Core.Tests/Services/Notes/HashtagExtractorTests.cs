using System.Linq;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class HashtagExtractorTests
{
    [Fact]
    public void Extract_JapaneseAndAlphanumericMixed_ExtractsAllInOrder()
    {
        var body = "中国市場について考察。現在の販売台数を注視 #自動車 #中国 #EV2024";

        var tags = HashtagExtractor.Extract(body);

        Assert.Equal(new[] { "自動車", "中国", "ev2024" }, tags);
    }

    [Fact]
    public void Extract_DuplicateCaseInsensitiveAsciiTags_AreMergedIntoOne()
    {
        var body = "#EV good day, #ev also good, and #Ev too";

        var tags = HashtagExtractor.Extract(body);

        Assert.Single(tags);
        Assert.Equal("ev", tags[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Extract_NullOrEmptyBody_ReturnsEmpty(string? body)
    {
        var tags = HashtagExtractor.Extract(body);

        Assert.Empty(tags);
    }

    [Fact]
    public void Extract_BodyWithNoHashtags_ReturnsEmpty()
    {
        var tags = HashtagExtractor.Extract("plain text with no tags at all");

        Assert.Empty(tags);
    }

    [Fact]
    public void Extract_MoreThan30DistinctTags_StopsAtThirty()
    {
        var body = string.Join(" ", Enumerable.Range(1, 35).Select(i => $"#tag{i}"));

        var tags = HashtagExtractor.Extract(body);

        Assert.Equal(30, tags.Length);
        Assert.Contains("tag1", tags);
        Assert.Contains("tag30", tags);
        Assert.DoesNotContain("tag31", tags);
        Assert.DoesNotContain("tag35", tags);
    }

    [Fact]
    public void Extract_TagLongerThan50Characters_IsTruncatedTo50()
    {
        var longTag = "#" + new string('a', 60);

        var tags = HashtagExtractor.Extract(longTag);

        Assert.Single(tags);
        Assert.Equal(50, tags[0].Length);
        Assert.Equal(new string('a', 50), tags[0]);
    }

    [Fact]
    public void Extract_FullWidthAndHalfWidthVariants_AreNotUnified()
    {
        // Known v1 limitation (spec section 3.1): full-width/half-width normalization is not
        // performed, so a half-width "#ev" and a full-width "#ｅｖ" are treated as distinct tags.
        var body = "#ev #ｅｖ"; // "#ev" and "#" + fullwidth "ev"

        var tags = HashtagExtractor.Extract(body);

        Assert.Equal(2, tags.Length);
        Assert.Contains("ev", tags);
        Assert.Contains("ｅｖ", tags);
    }

    [Fact]
    public void Extract_DoesNotRemoveHashtagsFromConceptualBody()
    {
        // The extractor only reads Body; it never mutates or strips hashtags from it (spec 3.1).
        const string body = "本文 #keep このまま残る";

        var tags = HashtagExtractor.Extract(body);

        Assert.Single(tags);
        Assert.Equal("keep", tags[0]);
        Assert.Contains("#keep", body);
    }

    [Fact]
    public void Extract_TagsWithLeadingTrailingWhitespaceInMatch_AreTrimmed()
    {
        var body = "#tag  \t next word";

        var tags = HashtagExtractor.Extract(body);

        Assert.Single(tags);
        Assert.Equal("tag", tags[0]);
    }

    [Fact]
    public void Tokenize_MixedPlainAndHashtagText_YieldsAlternatingTokensInOrder()
    {
        var tokens = HashtagExtractor.Tokenize("見て #EV 良い").ToList();

        Assert.Equal(3, tokens.Count);
        Assert.Equal(("見て ", (string?)null), (tokens[0].Text, tokens[0].NormalizedHashtag));
        Assert.Equal(("#EV", "ev"), (tokens[1].Text, tokens[1].NormalizedHashtag));
        Assert.Equal((" 良い", (string?)null), (tokens[2].Text, tokens[2].NormalizedHashtag));
    }

    [Fact]
    public void Tokenize_BodyStartingAndEndingWithHashtags_HasNoEmptyBoundaryTokens()
    {
        var tokens = HashtagExtractor.Tokenize("#start middle #end").ToList();

        Assert.Equal(3, tokens.Count);
        Assert.Equal("#start", tokens[0].Text);
        Assert.Equal(" middle ", tokens[1].Text);
        Assert.Equal("#end", tokens[2].Text);
    }

    [Fact]
    public void Tokenize_NullOrEmptyBody_YieldsNoTokens()
    {
        Assert.Empty(HashtagExtractor.Tokenize(null));
        Assert.Empty(HashtagExtractor.Tokenize(""));
    }

    [Fact]
    public void Tokenize_BodyWithNoHashtags_YieldsSinglePlainToken()
    {
        var tokens = HashtagExtractor.Tokenize("plain text with no tags at all").ToList();

        Assert.Single(tokens);
        Assert.Null(tokens[0].NormalizedHashtag);
    }

    [Fact]
    public void Tokenize_NormalizesEachHashtagTokenIdenticallyToExtract()
    {
        // Every occurrence Tokenize captures for a tag must normalize to the exact same value
        // Extract would have produced for it - same regex, same NormalizeTag helper (SSoT).
        var body = "#EV good, #ev also, #" + new string('a', 60);

        var extracted = HashtagExtractor.Extract(body);
        var tokenized = HashtagExtractor.Tokenize(body).Where(t => t.NormalizedHashtag is not null).Select(t => t.NormalizedHashtag).Distinct().ToList();

        Assert.Equal(extracted.OrderBy(t => t), tokenized.OrderBy(t => t));
    }

    [Fact]
    public void Tokenize_DoesNotDeduplicateOrCapUnlikeExtract()
    {
        // Tokenize must preserve every occurrence (for accurate inline rendering), even where
        // Extract would have merged duplicates or stopped at MaxHashtagCount.
        var body = "#ev #ev #ev";

        var tokens = HashtagExtractor.Tokenize(body).Where(t => t.NormalizedHashtag is not null).ToList();

        Assert.Equal(3, tokens.Count);
        Assert.All(tokens, t => Assert.Equal("ev", t.NormalizedHashtag));
    }
}
