using System.Linq;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class UrlExtractorTests
{
    [Fact]
    public void Extract_SingleUrlInSentence_TrimsTrailingPunctuation()
    {
        var body = "決算資料はこちら(https://example.com/ir/2026.pdf)を参照。";

        var urls = UrlExtractor.Extract(body);

        Assert.Single(urls);
        Assert.Equal("https://example.com/ir/2026.pdf", urls[0]);
    }

    [Fact]
    public void Extract_MultipleUrls_ReturnsAllInOrder()
    {
        var body = "参考: https://example.com/a and https://example.com/b.";

        var urls = UrlExtractor.Extract(body);

        Assert.Equal(new[] { "https://example.com/a", "https://example.com/b" }, urls);
    }

    [Fact]
    public void Extract_DuplicateUrl_ReturnsOnlyOnce()
    {
        var body = "https://example.com/a and again https://example.com/a";

        var urls = UrlExtractor.Extract(body);

        Assert.Single(urls);
    }

    [Fact]
    public void Extract_NonHttpScheme_IsIgnored()
    {
        var body = "javascript:alert(1) and ftp://example.com/file and https://example.com/ok";

        var urls = UrlExtractor.Extract(body);

        Assert.Single(urls);
        Assert.Equal("https://example.com/ok", urls[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no urls here")]
    public void Extract_NoUrls_ReturnsEmpty(string? body)
    {
        var urls = UrlExtractor.Extract(body);

        Assert.Empty(urls);
    }

    [Fact]
    public void Tokenize_MixedPlainAndUrlText_YieldsAlternatingTokensInOrder()
    {
        var tokens = UrlExtractor.Tokenize("見て https://example.com/a 良い").ToList();

        Assert.Equal(3, tokens.Count);
        Assert.Equal(("見て ", (string?)null), (tokens[0].Text, tokens[0].NormalizedUrl));
        Assert.Equal(("https://example.com/a", "https://example.com/a"), (tokens[1].Text, tokens[1].NormalizedUrl));
        Assert.Equal((" 良い", (string?)null), (tokens[2].Text, tokens[2].NormalizedUrl));
    }

    [Fact]
    public void Tokenize_TrailingSentencePunctuation_IsSplitIntoItsOwnPlainToken()
    {
        var tokens = UrlExtractor.Tokenize("決算資料はこちら(https://example.com/ir/2026.pdf)を参照。").ToList();

        var urlToken = Assert.Single(tokens, t => t.NormalizedUrl is not null);
        Assert.Equal("https://example.com/ir/2026.pdf", urlToken.Text);
        Assert.Equal("https://example.com/ir/2026.pdf", urlToken.NormalizedUrl);

        // The closing ")" trimmed off the match must survive as plain text immediately after the
        // URL token, not silently vanish - re-concatenating every token must reproduce the body.
        var reconstructed = string.Concat(tokens.Select(t => t.Text));
        Assert.Equal("決算資料はこちら(https://example.com/ir/2026.pdf)を参照。", reconstructed);
    }

    [Fact]
    public void Tokenize_NonHttpScheme_YieldsWholeMatchAsPlainToken()
    {
        var tokens = UrlExtractor.Tokenize("see javascript:alert(1) here").ToList();

        Assert.DoesNotContain(tokens, t => t.NormalizedUrl is not null);
        Assert.Equal("see javascript:alert(1) here", string.Concat(tokens.Select(t => t.Text)));
    }

    [Fact]
    public void Tokenize_UrlWithFragment_KeepsHashPartInsideTheSingleUrlToken()
    {
        // The '#' in a URL fragment must not be split off into its own token - callers that layer
        // hashtag tokenization on top of this must only ever see it applied to non-URL text.
        var tokens = UrlExtractor.Tokenize("https://example.com/page#section 続き").ToList();

        var urlToken = Assert.Single(tokens, t => t.NormalizedUrl is not null);
        Assert.Equal("https://example.com/page#section", urlToken.Text);
    }

    [Fact]
    public void Tokenize_NormalizesEachUrlTokenIdenticallyToExtract()
    {
        var body = "https://example.com/a and https://example.com/a again";

        var extracted = UrlExtractor.Extract(body);
        var tokenized = UrlExtractor.Tokenize(body).Where(t => t.NormalizedUrl is not null).Select(t => t.NormalizedUrl).Distinct().ToList();

        Assert.Equal(extracted.OrderBy(u => u), tokenized.OrderBy(u => u));
    }

    [Fact]
    public void Tokenize_NullOrEmptyBody_YieldsNoTokens()
    {
        Assert.Empty(UrlExtractor.Tokenize(null));
        Assert.Empty(UrlExtractor.Tokenize(""));
    }

    [Fact]
    public void Tokenize_BodyWithNoUrls_YieldsSinglePlainToken()
    {
        var tokens = UrlExtractor.Tokenize("no urls here").ToList();

        Assert.Single(tokens);
        Assert.Null(tokens[0].NormalizedUrl);
    }
}
