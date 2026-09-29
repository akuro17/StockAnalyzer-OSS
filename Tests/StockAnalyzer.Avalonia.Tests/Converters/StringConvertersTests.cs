using StockAnalyzer.Avalonia.Converters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Converters;

public class StringConvertersTests
{
    [Theory]
    [InlineData("https://example.com/page", "example.com/page")]
    [InlineData("http://example.com/page", "example.com/page")]
    [InlineData("HTTPS://Example.com/Page", "Example.com/Page")]
    public void FormatUrlForDisplay_StripsHttpOrHttpsScheme_CaseInsensitive(string url, string expected)
    {
        Assert.Equal(expected, UrlDisplayConverter.FormatUrlForDisplay(url));
    }

    [Fact]
    public void FormatUrlForDisplay_ShortUrl_IsReturnedUnchangedAfterSchemeStrip()
    {
        Assert.Equal("short.io/x", UrlDisplayConverter.FormatUrlForDisplay("https://short.io/x"));
    }

    [Fact]
    public void FormatUrlForDisplay_LongUrl_IsTruncatedWithEllipsisAtMaxLength()
    {
        var longPath = new string('a', 60);
        var url = "https://example.com/" + longPath;

        var result = UrlDisplayConverter.FormatUrlForDisplay(url, maxLength: 40);

        Assert.EndsWith("...", result);
        Assert.Equal(43, result.Length); // 40 chars + "..."
        Assert.Equal(("example.com/" + longPath)[..40], result[..^3]);
    }

    [Fact]
    public void FormatUrlForDisplay_ExactlyAtMaxLength_IsNotTruncated()
    {
        var withoutScheme = new string('b', 40);
        var url = "http://" + withoutScheme;

        var result = UrlDisplayConverter.FormatUrlForDisplay(url, maxLength: 40);

        Assert.Equal(withoutScheme, result);
        Assert.DoesNotContain("...", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatUrlForDisplay_NullOrEmpty_ReturnsEmpty(string? url)
    {
        Assert.Equal(string.Empty, UrlDisplayConverter.FormatUrlForDisplay(url));
    }

    [Fact]
    public void FormatUrlForDisplay_NoScheme_IsUsedAsIs()
    {
        Assert.Equal("plain-text-no-scheme", UrlDisplayConverter.FormatUrlForDisplay("plain-text-no-scheme"));
    }
}
