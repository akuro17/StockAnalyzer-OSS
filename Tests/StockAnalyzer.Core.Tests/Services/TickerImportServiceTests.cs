using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class TickerImportServiceTests
{
    private readonly TickerImportService _service;

    public TickerImportServiceTests()
    {
        _service = new TickerImportService();
    }

    [Fact]
    public async Task ImportTickersAsync_WithValidSingleLine_ReturnsTickers()
    {
        // Arrange
        var content = "AAPL\nMSFT\nTSLA";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("AAPL", result);
        Assert.Contains("MSFT", result);
        Assert.Contains("TSLA", result);
    }

    [Fact]
    public async Task ImportTickersAsync_WithCommaSeparated_ReturnsTickers()
    {
        // Arrange
        var content = "AAPL,MSFT,TSLA";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("AAPL", result);
        Assert.Contains("MSFT", result);
        Assert.Contains("TSLA", result);
    }

    [Fact]
    public async Task ImportTickersAsync_WithMixedDelimiters_ReturnsTickers()
    {
        // Arrange
        var content = "AAPL, MSFT\tTSLA GOOG";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Contains("AAPL", result);
        Assert.Contains("MSFT", result);
        Assert.Contains("TSLA", result);
        Assert.Contains("GOOG", result);
    }

    [Fact]
    public async Task ImportTickersAsync_WithDuplicates_ReturnsUniqueTickers()
    {
        // Arrange
        var content = "AAPL\naapl\nAAPL";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Single(result);
        Assert.Equal("AAPL", result[0]);
    }

    [Fact]
    public async Task ImportTickersAsync_WithNormalization_ReturnsNormalizedTickers()
    {
        // Arrange
        var content = "aapl.us\n7203.t";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("AAPL-US", result);
        Assert.Contains("7203-T", result);
    }

    [Fact]
    public async Task ImportTickersAsync_WithFourDigitJapaneseCode_AppendsTSuffix()
    {
        // Arrange
        var content = "7203\n6758\n9984";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("7203-T", result);
        Assert.Contains("6758-T", result);
        Assert.Contains("9984-T", result);
    }

    [Fact]
    public async Task ImportTickersAsync_WithInvalidFormats_SkipsInvalid()
    {
        // Arrange
        var content = "AAPL\nTOO_LONG_TICKER_NAME_123\n$INVALID\nMSFT";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("AAPL", result);
        Assert.Contains("MSFT", result);
    }

    [Fact]
    public async Task ImportTickersAsync_WithEmptyLines_SkipsEmpty()
    {
        // Arrange
        var content = "AAPL\n\n   \nMSFT";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var result = await _service.ImportTickersAsync(stream);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("AAPL", result);
        Assert.Contains("MSFT", result);
    }

    [Theory]
    [InlineData("CL=F", "CL=F")]
    [InlineData("gc=f", "GC=F")]
    [InlineData("HG=F", "HG=F")]
    public async Task ImportTickersAsync_WithContinuousFuturesSymbol_ReturnsTheUpperCaseSymbol(string token, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(token));

        var result = await _service.ImportTickersAsync(stream);

        Assert.Equal(new[] { expected }, result);
    }

    [Fact]
    public async Task ImportTickersAsync_FuturesSymbolsWithDescriptions_KeepsTheSymbolsAndRejectsTheDescriptionTokens()
    {
        var content = "CL=F (WTI crude)\nGC=F (gold)\nHG=F (copper)";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _service.ImportTickersAsync(stream);

        Assert.Equal(new[] { "CL=F", "GC=F", "HG=F" }, result.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    // The import bound (TickerSymbolLimits.ImportMaxLength = 16) also applies to the vendor-suffixed forms; the accepted rows pin the wiring of that constant from below.
    [Theory]
    [InlineData("AAAAAAAAAAAAAA=F", true)]   // 14 + 2 = 16 = the bound
    [InlineData("AAAAAAAAAAAAAAA=F", false)] // 15 + 2 = 17
    [InlineData("AAAAAAAAAAAAAA=X", true)]
    [InlineData("AAAAAAAAAAAAAAA=X", false)]
    public async Task ImportTickersAsync_VendorSuffixedSymbol_FollowsTheImportLengthBound(string token, bool accepted)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(token));

        var result = await _service.ImportTickersAsync(stream);

        Assert.Equal(accepted ? new[] { token } : Array.Empty<string>(), result);
    }

    [Theory]
    [InlineData("JPY=X", "JPY=X")]
    [InlineData("eurusd=x", "EURUSD=X")]
    [InlineData("USDJPY=X", "USDJPY=X")]
    public async Task ImportTickersAsync_WithCurrencyPairSymbol_ReturnsTheUpperCaseSymbol(string token, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(token));

        var result = await _service.ImportTickersAsync(stream);

        Assert.Equal(new[] { expected }, result);
    }

    [Theory]
    [InlineData("=X")]
    [InlineData("6E=X")]
    [InlineData("USD1=X")]
    [InlineData("USD-JPY=X")]
    [InlineData("EURUSD==X")]
    [InlineData("EURUSD=Y")]
    public async Task ImportTickersAsync_MalformedCurrencyPairSymbols_StayRejected(string token)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(token));

        var result = await _service.ImportTickersAsync(stream);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("=F")]
    [InlineData("CL=")]
    [InlineData("CL==F")]
    [InlineData("CL=F=F")]
    [InlineData("A=B")]
    public async Task ImportTickersAsync_MalformedEqualsSymbols_StayRejected(string token)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(token));

        var result = await _service.ImportTickersAsync(stream);

        Assert.Empty(result);
    }
}
