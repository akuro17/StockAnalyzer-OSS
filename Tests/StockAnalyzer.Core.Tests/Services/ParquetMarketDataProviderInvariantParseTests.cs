using System;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Text-to-decimal parsing of Python metadata must not depend on the thread culture, and the default master ticker list must be
/// created without a BOM and without leftover temporary files (IoHydrationWebAiReview, T5). The application sets en-US at
/// startup; a test host does not, so these tests set the cultures explicitly.
/// </summary>
[Collection("CultureSensitive")]
public sealed class ParquetMarketDataProviderInvariantParseTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _originalCulture;

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void ParseInvariantDecimal_IgnoresTheThreadCulture(string culture)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        Assert.Equal(1234.5m, ParquetMarketDataProvider.ParseInvariantDecimal("1234.5"));
        Assert.Equal(1234.5m, ParquetMarketDataProvider.ParseInvariantDecimal("1,234.5"));
        Assert.Equal(-0.25m, ParquetMarketDataProvider.ParseInvariantDecimal(" -0.25 "));
    }

    [Fact]
    public void ParseInvariantDecimal_DocumentsWhatTheCultureDependentCallGot()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal(12345m, Convert.ToDecimal("1234.5")); // the former behaviour under a comma-decimal culture
        Assert.Equal(1234.5m, ParquetMarketDataProvider.ParseInvariantDecimal("1234.5"));
    }

    [Theory]
    [InlineData("1e5")]
    [InlineData("$12.5")]
    [InlineData("(12.5)")]
    [InlineData("")]
    [InlineData("None")]
    public void ParseInvariantDecimal_RejectsWhatTheCurrentParserRejects(string text)
    {
        Assert.Throws<FormatException>(() => ParquetMarketDataProvider.ParseInvariantDecimal(text));
    }

    [Fact]
    public void ParseInvariantDecimal_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ParquetMarketDataProvider.ParseInvariantDecimal(null!));
    }

    [Fact]
    public void DefaultTickerList_IsCreatedWithoutBomAndWithoutLeftovers()
    {
        using var fixture = ReadOnlyDataTreeFixture.Create(5, "MSFT");
        string directory = Path.Combine(Path.GetTempPath(), "sa_ticker_default_" + Guid.NewGuid().ToString("N"));
        string tickerList = Path.Combine(directory, "tickers.json");
        try
        {
            using var db = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
            var settings = new MarketDataSettings { DailyDataPath = fixture.Settings.DailyDataPath, TickerListPath = tickerList };

            _ = new ParquetMarketDataProvider(db, UnavailablePythonService.Instance, Options.Create(settings), NullLogger<ParquetMarketDataProvider>.Instance);

            byte[] bytes = File.ReadAllBytes(tickerList);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("[]", System.Text.Encoding.UTF8.GetString(bytes).Trim());
            Assert.Equal(new[] { "tickers.json" }, Array.ConvertAll(Directory.GetFiles(directory), Path.GetFileName));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}

[CollectionDefinition("CultureSensitive", DisableParallelization = true)]
public sealed class CultureSensitiveCollection { }
