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
}
