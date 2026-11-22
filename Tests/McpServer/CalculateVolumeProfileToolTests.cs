using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class CalculateVolumeProfileToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "VPTEST";
    private const string FlatSymbol = "VPFLAT";
    private const string ThirdsSymbol = "VPTHIRD";
    private const string ZeroVolumeSymbol = "VPZERO";
    private const int ThirdsDays = 99;
    private const int ZeroVolumeDays = 40;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "vptest");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + (i % 10) * 2m).ToList();
    private readonly List<decimal> _flatCloses = Enumerable.Repeat(100m, Days).ToList();

    public CalculateVolumeProfileToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, _closes);
        SeedZeroRangeParquet(_fixture.DailyDirectory, FlatSymbol, 100m, Days);
        SeedFlatBarsParquet(_fixture.DailyDirectory, ThirdsSymbol, ThirdsCloses(), 1000);
        SeedFlatBarsParquet(_fixture.DailyDirectory, ZeroVolumeSymbol, ZeroVolumeCloses(), 0);
    }

    /// <summary>99 bars at three price levels, 33 bars each (high = low = close): the Time-at-Price profile of 3 bins holds exactly 33 / 33 / 33.</summary>
    private static List<decimal> ThirdsCloses() => Enumerable.Range(0, ThirdsDays).Select(i => 100m + (i % 3) * 10m).ToList();

    /// <summary>40 bars at ten different prices (high = low = close) that all traded zero volume.</summary>
    private static List<decimal> ZeroVolumeCloses() => Enumerable.Range(0, ZeroVolumeDays).Select(i => 100m + (i % 10) * 2m).ToList();

    private static List<CoreCandleData> FlatBars(IReadOnlyList<decimal> closes, long volume)
    {
        var date = new DateTime(2024, 1, 1);
        return closes.Select((close, i) => new CoreCandleData(date.AddDays(i), close, close, close, close, volume)).ToList();
    }

    private static void SeedFlatBarsParquet(string dir, string ticker, IReadOnlyList<decimal> closes, long volume)
    {
        var fixturePath = Path.Combine(dir, $"{ticker}.parquet").Replace("\\", "/");
        var rows = new List<string>();
        var date = new DateTime(2024, 1, 1);
        foreach (decimal close in closes)
        {
            rows.Add(FormattableString.Invariant($"(DATE '{date:yyyy-MM-dd}', {close}, {close}, {close}, {close}, {volume})"));
            date = date.AddDays(1);
        }

        using var connection = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            COPY (
                SELECT * FROM (VALUES
                    {string.Join(",\n                    ", rows)}
                ) AS t(date, open, high, low, close, volume)
            ) TO '{fixturePath}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    private static void SeedZeroRangeParquet(string dir, string ticker, decimal price, int days)
    {
        var fixturePath = Path.Combine(dir, $"{ticker}.parquet").Replace("\\", "/");
        var rows = new List<string>();
        var date = new DateTime(2024, 1, 1);
        for (int i = 0; i < days; i++)
        {
            rows.Add(FormattableString.Invariant($"(DATE '{date:yyyy-MM-dd}', {price}, {price}, {price}, {price}, 1000)"));
            date = date.AddDays(1);
        }

        using var connection = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            COPY (
                SELECT * FROM (VALUES
                    {string.Join(",\n                    ", rows)}
                ) AS t(date, open, high, low, close, volume)
            ) TO '{fixturePath}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private List<CoreCandleData> Candles()
    {
        var date = new DateTime(2024, 1, 1);
        return _closes.Select((close, i) => new CoreCandleData(date.AddDays(i), close - 0.2m, close + 0.5m, close - 0.5m, close, 1000)).ToList();
    }

    private readonly StringWriter _logWriter = new();

    private async Task<McpMessageDispatcher> StartAsync(bool compactDefaults = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(_logWriter), compactDefaults: compactDefaults);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = Symbol, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_volume_profile", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

    private JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), $"{McpTestSupport.Structured(response)}\nServer log:\n{_logWriter}");
        return McpTestSupport.Structured(response);
    }

    private static string ErrorCode(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    [Fact]
    public async Task VolumeProfile_VolumeBasis_EqualsDirectCoreVolumeAnalysis()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"basis\":\"Volume\",\"binCount\":20,\"valueAreaPercent\":0.70"));

        Assert.Equal("VPTEST", result.GetProperty("symbol").GetString());
        Assert.Equal("Volume", result.GetProperty("basis").GetString());
        Assert.Equal(Days, result.GetProperty("barCount").GetInt32());

        // Direct Core calculation
        var coreCandles = Candles();
        var expectedBins = VolumeAnalysis.CalculateProfile(coreCandles, 20, VolumeDistributionMode.Proportional);
        var (expectedVah, expectedVal) = VolumeAnalysis.CalculateValueArea(expectedBins, 0.70);

        decimal expectedPoc = 0m;
        long maxVol = -1;
        for (int i = 0; i < expectedBins.Count; i++)
        {
            if (expectedBins[i].TotalVolume > maxVol)
            {
                maxVol = expectedBins[i].TotalVolume;
                expectedPoc = expectedBins[i].Price;
            }
        }

        Assert.Equal(expectedVah, result.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(expectedVal, result.GetProperty("valueArea").GetProperty("low").GetDecimal());
        Assert.Equal(expectedPoc, result.GetProperty("poc").GetProperty("price").GetDecimal());

        JsonElement[] bins = result.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Equal(expectedBins.Count, bins.Length);
        for (int i = 0; i < expectedBins.Count; i++)
        {
            Assert.Equal(expectedBins[i].LowerBound, bins[i].GetProperty("priceLow").GetDecimal());
            Assert.Equal(expectedBins[i].UpperBound, bins[i].GetProperty("priceHigh").GetDecimal());
            Assert.Equal(expectedBins[i].TotalVolume, bins[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task VolumeProfile_TimeAtPriceBasis_EqualsDirectCoreTimeAtPriceAnalysis()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"basis\":\"TimeAtPrice\",\"binCount\":15,\"valueAreaPercent\":0.70"));

        Assert.Equal("VPTEST", result.GetProperty("symbol").GetString());
        Assert.Equal("TimeAtPrice", result.GetProperty("basis").GetString());

        var coreCandles = Candles();
        var expectedBins = TimeAtPriceAnalysis.CalculateProfile(coreCandles, 15, PriceType.Close);
        var (expectedVah, expectedVal) = TimeAtPriceAnalysis.CalculateValueArea(expectedBins, 0.70);

        decimal expectedPoc = 0m;
        long maxVol = -1;
        for (int i = 0; i < expectedBins.Count; i++)
        {
            if (expectedBins[i].TotalVolume > maxVol)
            {
                maxVol = expectedBins[i].TotalVolume;
                expectedPoc = expectedBins[i].Price;
            }
        }

        Assert.Equal(expectedVah, result.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(expectedVal, result.GetProperty("valueArea").GetProperty("low").GetDecimal());
        Assert.Equal(expectedPoc, result.GetProperty("poc").GetProperty("price").GetDecimal());

        JsonElement[] bins = result.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Equal(expectedBins.Count, bins.Length);
        for (int i = 0; i < expectedBins.Count; i++)
        {
            Assert.Equal(expectedBins[i].LowerBound, bins[i].GetProperty("priceLow").GetDecimal());
            Assert.Equal(expectedBins[i].UpperBound, bins[i].GetProperty("priceHigh").GetDecimal());
            Assert.Equal(expectedBins[i].TotalVolume, bins[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task VolumeProfile_MutationTest_FutureBarsDoNotAffectHistoricalOutput()
    {
        var coreCandles = Candles();
        var cutIndex = 50;
        var endDate = coreCandles[cutIndex].Timestamp.Date.ToString("yyyy-MM-dd");

        var dispatcher = await StartAsync();
        JsonElement resultBefore = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"binCount\":20"));

        // Mutate candles strictly after endDate in the data store
        var modifiedCloses = new List<decimal>(_closes);
        for (int i = cutIndex + 1; i < modifiedCloses.Count; i++)
        {
            modifiedCloses[i] = 9999m + i;
        }
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, modifiedCloses);

        // Call again with the same endDate
        JsonElement resultAfter = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"binCount\":20"));

        Assert.Equal(resultBefore.GetProperty("poc").GetProperty("price").GetDecimal(), resultAfter.GetProperty("poc").GetProperty("price").GetDecimal());
        Assert.Equal(resultBefore.GetProperty("valueArea").GetProperty("high").GetDecimal(), resultAfter.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(resultBefore.GetProperty("valueArea").GetProperty("low").GetDecimal(), resultAfter.GetProperty("valueArea").GetProperty("low").GetDecimal());

        JsonElement[] binsBefore = resultBefore.GetProperty("bins").EnumerateArray().ToArray();
        JsonElement[] binsAfter = resultAfter.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Equal(binsBefore.Length, binsAfter.Length);
        for (int i = 0; i < binsBefore.Length; i++)
        {
            Assert.Equal(binsBefore[i].GetProperty("volume").GetInt64(), binsAfter[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task VolumeProfile_RangeBars_SlicesTrailingBars()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"rangeBars\":30,\"binCount\":20"));

        Assert.Equal(30, result.GetProperty("barCount").GetInt32());
    }

    [Fact]
    public async Task VolumeProfile_RangeZero_DegeneratesToSingleBin()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"allowUnregistered\":true", symbol: FlatSymbol));

        Assert.Equal(FlatSymbol, result.GetProperty("symbol").GetString());
        Assert.Equal(1, result.GetProperty("binCount").GetInt32());
        Assert.Equal(100m, result.GetProperty("poc").GetProperty("price").GetDecimal());
        Assert.Equal(100m, result.GetProperty("valueArea").GetProperty("high").GetDecimal());
        Assert.Equal(100m, result.GetProperty("valueArea").GetProperty("low").GetDecimal());

        JsonElement[] bins = result.GetProperty("bins").EnumerateArray().ToArray();
        Assert.Single(bins);
        Assert.Equal(100m, bins[0].GetProperty("priceLow").GetDecimal());
        Assert.Equal(100m, bins[0].GetProperty("priceHigh").GetDecimal());
        Assert.Equal(Days * 1000, bins[0].GetProperty("volume").GetInt64());
        Assert.Equal(Days * 1000, result.GetProperty("totalBarVolume").GetInt64());
        Assert.Equal(0, result.GetProperty("truncatedVolume").GetInt64());
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(48)]
    public async Task VolumeProfile_VolumeBasis_ReportsHowMuchVolumeTheWholeUnitSplitDropped(int binCount)
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, $",\"basis\":\"Volume\",\"binCount\":{binCount}"));

        // Reference: the same bars through the Core distribution. Each bar's volume is split in whole units over the touched bins.
        List<CoreCandleData> candles = Candles();
        List<VolumeBin> expectedBins = VolumeAnalysis.CalculateProfile(candles, binCount, VolumeDistributionMode.Proportional);
        long expectedTotal = candles.Sum(c => c.Volume);
        long expectedProfile = expectedBins.Sum(b => b.TotalVolume);
        Assert.True(expectedTotal - expectedProfile > 0, "the fixture drops no volume; the check would be vacuous");

        Assert.Equal(expectedTotal, result.GetProperty("totalBarVolume").GetInt64());
        Assert.Equal(expectedProfile, result.GetProperty("profileVolume").GetInt64());
        Assert.Equal(expectedTotal - expectedProfile, result.GetProperty("truncatedVolume").GetInt64());
        // The reported bins are the profile: they add up to profileVolume, and the distribution itself is unchanged.
        Assert.Equal(expectedProfile, result.GetProperty("bins").EnumerateArray().Sum(b => b.GetProperty("volume").GetInt64()));
    }

    [Fact]
    public async Task VolumeProfile_TimeAtPriceBasis_CountsBarsAndLosesNothing()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"basis\":\"TimeAtPrice\",\"binCount\":20"));

        Assert.Equal(Days, result.GetProperty("totalBarVolume").GetInt64());
        Assert.Equal(Days, result.GetProperty("profileVolume").GetInt64());
        Assert.Equal(0, result.GetProperty("truncatedVolume").GetInt64());
    }

    [Fact]
    public async Task VolumeProfile_InvalidArguments_ThrowsToolException()
    {
        var dispatcher = await StartAsync();

        // binCount < 2
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"binCount\":1")));
        // binCount > max (200)
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"binCount\":201")));
        // rangeBars > max (10000)
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"rangeBars\":10001")));
        // valueAreaPercent <= 0
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"valueAreaPercent\":0.0")));
        // valueAreaPercent > 1.0
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"valueAreaPercent\":1.01")));
        // invalid basis
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"basis\":\"InvalidBasis\"")));
        // timeFrame required
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_calculate_volume_profile", $"{{\"symbol\":\"{Symbol}\"}}")!));
    }

    // ---- bin ranking (includeRanking / rankingLimit) ----

    // The number of places comes from the tool (one definition); the thirds test below pins the literal 0.333333 / 0.666667 / 1 independently.
    private static decimal WrittenShare(decimal value) => Math.Round(value, CalculateVolumeProfileTool.RankingShareDecimalPlaces, MidpointRounding.AwayFromZero);

    [Fact]
    public async Task Ranking_NotRequested_AddsNoRankingKeys_AndTheOtherKeysAreUnchanged()
    {
        var dispatcher = await StartAsync();
        JsonElement plain = Result(await CallAsync(dispatcher, ",\"binCount\":20"));
        JsonElement falseFlag = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"includeRanking\":false"));

        foreach (JsonElement result in new[] { plain, falseFlag })
        {
            Assert.False(result.TryGetProperty("ranking", out _));
            Assert.False(result.TryGetProperty("rankingCount", out _));
            Assert.False(result.TryGetProperty("rankingColumns", out _));
            Assert.False(result.TryGetProperty("rankingRows", out _));
        }

        Assert.Equal(plain.GetRawText(), falseFlag.GetRawText());
    }

    [Theory]
    [InlineData("Volume", 20)]
    [InlineData("TimeAtPrice", 15)]
    public async Task Ranking_EqualsTheCoreRankingOfTheSameBins(string basis, int binCount)
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, $",\"basis\":\"{basis}\",\"binCount\":{binCount},\"includeRanking\":true"));

        List<VolumeBin> bins = basis == "Volume"
            ? VolumeAnalysis.CalculateProfile(Candles(), binCount, VolumeDistributionMode.Proportional)
            : TimeAtPriceAnalysis.CalculateProfile(Candles(), binCount, PriceType.Close);
        IReadOnlyList<VolumeBinRank> expected = VolumeProfileRanking.Rank(bins);

        JsonElement[] rows = result.GetProperty("ranking").EnumerateArray().ToArray();
        Assert.Equal(expected.Count, result.GetProperty("rankingCount").GetInt32());
        Assert.Equal(expected.Count, rows.Length);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Rank, rows[i].GetProperty("rank").GetInt32());
            Assert.Equal(expected[i].LowerBound, rows[i].GetProperty("priceLow").GetDecimal());
            Assert.Equal(expected[i].UpperBound, rows[i].GetProperty("priceHigh").GetDecimal());
            Assert.Equal(expected[i].Price, rows[i].GetProperty("price").GetDecimal());
            Assert.Equal(expected[i].Volume, rows[i].GetProperty("volume").GetInt64());
            Assert.Equal(WrittenShare(expected[i].Share), rows[i].GetProperty("share").GetDecimal());
            Assert.Equal(WrittenShare(expected[i].CumulativeShare), rows[i].GetProperty("cumulativeShare").GetDecimal());
        }

        // Rank 1 is the POC the tool already reports, and the last cumulative share of the whole ranking is exactly 1.
        Assert.Equal(result.GetProperty("poc").GetProperty("price").GetDecimal(), rows[0].GetProperty("price").GetDecimal());
        Assert.Equal(1m, rows[^1].GetProperty("cumulativeShare").GetDecimal());
        // The bins array is still written, unchanged.
        Assert.Equal(bins.Count, result.GetProperty("bins").GetArrayLength());
    }

    [Fact]
    public async Task Ranking_Limit_KeepsTheFirstRows_AndTheSharesStillUseTheTotalOfAllBins()
    {
        var dispatcher = await StartAsync();
        JsonElement full = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"includeRanking\":true"));
        JsonElement limited = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"includeRanking\":true,\"rankingLimit\":3"));

        Assert.Equal(3, limited.GetProperty("rankingCount").GetInt32());
        JsonElement[] fullRows = full.GetProperty("ranking").EnumerateArray().ToArray();
        JsonElement[] rows = limited.GetProperty("ranking").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(fullRows[i].GetRawText(), rows[i].GetRawText());
        }

        Assert.True(rows[2].GetProperty("cumulativeShare").GetDecimal() < 1m);
        // The bins themselves are not cut by the ranking limit.
        Assert.Equal(20, limited.GetProperty("bins").GetArrayLength());
    }

    [Fact]
    public async Task Ranking_LimitAboveTheRankedRowCountButWithinTheRange_ReturnsAllRows()
    {
        var dispatcher = await StartAsync();
        JsonElement full = Result(await CallAsync(dispatcher, ",\"binCount\":24,\"includeRanking\":true"));
        JsonElement atMax = Result(await CallAsync(dispatcher, ",\"binCount\":24,\"includeRanking\":true,\"rankingLimit\":200"));

        Assert.Equal(full.GetProperty("rankingCount").GetInt32(), atMax.GetProperty("rankingCount").GetInt32());
        Assert.True(atMax.GetProperty("rankingCount").GetInt32() <= 24);
        Assert.Equal(full.GetProperty("ranking").GetRawText(), atMax.GetProperty("ranking").GetRawText());
    }

    [Fact]
    public async Task Ranking_LimitOne_ReturnsOnlyThePointOfControl()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":24,\"includeRanking\":true,\"rankingLimit\":1"));

        JsonElement row = Assert.Single(result.GetProperty("ranking").EnumerateArray().ToArray());
        Assert.Equal(1, row.GetProperty("rank").GetInt32());
        Assert.Equal(result.GetProperty("poc").GetProperty("price").GetDecimal(), row.GetProperty("price").GetDecimal());
    }

    [Theory]
    // rankingLimit without includeRanking true: INVALID_ARGUMENT first, whatever the value (so 201 is NOT LIMIT_EXCEEDED here: the order proof).
    [InlineData("", 10, "INVALID_ARGUMENT")]
    [InlineData(",\"includeRanking\":false", 10, "INVALID_ARGUMENT")]
    [InlineData(",\"includeRanking\":false", 201, "INVALID_ARGUMENT")]
    [InlineData("", 201, "INVALID_ARGUMENT")]
    [InlineData(",\"includeRanking\":false", 0, "INVALID_ARGUMENT")]
    // includeRanking true: the range check, exactly as binCount.
    [InlineData(",\"includeRanking\":true", 0, "INVALID_ARGUMENT")]
    [InlineData(",\"includeRanking\":true", -1, "INVALID_ARGUMENT")]
    [InlineData(",\"includeRanking\":true", 201, "LIMIT_EXCEEDED")]
    public async Task Ranking_LimitValidation_IsCheckedInTheFixedOrder(string prefix, int rankingLimit, string expectedCode)
    {
        var dispatcher = await StartAsync();

        Assert.Equal(expectedCode, ErrorCode(await CallAsync(dispatcher, $"{prefix},\"rankingLimit\":{rankingLimit}")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public async Task Ranking_LimitAtTheEdgesOfTheRange_IsAccepted(int rankingLimit)
    {
        var dispatcher = await StartAsync();

        JsonElement result = Result(await CallAsync(dispatcher, $",\"includeRanking\":true,\"rankingLimit\":{rankingLimit}"));
        Assert.True(result.GetProperty("rankingCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Ranking_LimitRangeAndCodes_AreTheSameAsBinCount()
    {
        var dispatcher = await StartAsync();

        // binCount keeps its pinned behaviour (INVALID_ARGUMENT below its minimum, LIMIT_EXCEEDED above the maximum 200), and rankingLimit uses the same maximum and codes.
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"binCount\":201")));
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"includeRanking\":true,\"rankingLimit\":201")));
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"binCount\":0")));
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"includeRanking\":true,\"rankingLimit\":0")));
    }

    [Fact]
    public async Task Ranking_CompactRows_WritesRankingAsColumnsAndRows()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"includeRanking\":true,\"compactRows\":true"));

        Assert.Equal(0, result.GetProperty("ranking").GetArrayLength());
        Assert.Equal(
            new[] { "rank", "priceLow", "priceHigh", "price", "volume", "share", "cumulativeShare" },
            result.GetProperty("rankingColumns").EnumerateArray().Select(c => c.GetString()).ToArray());

        JsonElement[] rows = result.GetProperty("rankingRows").EnumerateArray().ToArray();
        IReadOnlyList<VolumeBinRank> expected = VolumeProfileRanking.Rank(VolumeAnalysis.CalculateProfile(Candles(), 20, VolumeDistributionMode.Proportional));
        Assert.Equal(expected.Count, rows.Length);
        Assert.Equal(expected.Count, result.GetProperty("rankingCount").GetInt32());
        for (int i = 0; i < expected.Count; i++)
        {
            JsonElement[] cells = rows[i].EnumerateArray().ToArray();
            Assert.Equal(7, cells.Length);
            Assert.Equal(expected[i].Rank, cells[0].GetInt32());
            Assert.Equal(expected[i].Price, cells[3].GetDecimal());
            Assert.Equal(expected[i].Volume, cells[4].GetInt64());
            Assert.Equal(WrittenShare(expected[i].Share), cells[5].GetDecimal());
            Assert.Equal(WrittenShare(expected[i].CumulativeShare), cells[6].GetDecimal());
        }
    }

    [Fact]
    public async Task Ranking_IncludeBinsFalse_StillWritesTheRanking_AndCompactRowsWithoutBinsStaysRefused()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"includeRanking\":true,\"includeBins\":false"));

        Assert.False(result.TryGetProperty("bins", out _));
        Assert.True(result.GetProperty("rankingCount").GetInt32() > 0);
        Assert.Equal(result.GetProperty("rankingCount").GetInt32(), result.GetProperty("ranking").GetArrayLength());

        // The existing rule is unchanged: compactRows formats the bins array only.
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"includeRanking\":true,\"includeBins\":false,\"compactRows\":true")));
    }

    [Fact]
    public async Task Ranking_ThreeEqualBins_ShareIsRoundedToSixPlaces_AndTheLastCumulativeShareIsExactlyOne()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"basis\":\"TimeAtPrice\",\"binCount\":3,\"includeRanking\":true,\"allowUnregistered\":true", symbol: ThirdsSymbol));

        // Reference: the same bars through Core give 33 / 33 / 33 bars (1/3 each), tie order = lower price first.
        IReadOnlyList<VolumeBinRank> core = VolumeProfileRanking.Rank(TimeAtPriceAnalysis.CalculateProfile(FlatBars(ThirdsCloses(), 1000), 3, PriceType.Close));
        Assert.All(core, r => Assert.Equal(33, r.Volume));

        JsonElement[] rows = result.GetProperty("ranking").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0.333333m, 0.333333m, 0.333333m }, rows.Select(r => r.GetProperty("share").GetDecimal()).ToArray());
        Assert.Equal(new[] { 0.333333m, 0.666667m, 1m }, rows.Select(r => r.GetProperty("cumulativeShare").GetDecimal()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("rank").GetInt32()).ToArray());
        // Core itself is not rounded: the exact fraction is kept there.
        Assert.Equal(33m / 99m, core[0].Share);
    }

    [Theory]
    [InlineData(false, ",\"decimalPlaces\":0")]
    [InlineData(false, ",\"decimalPlaces\":2")]
    [InlineData(true, "")]
    public async Task Ranking_ShareAndCumulativeShare_DoNotDependOnDecimalPlacesOrCompactDefaults(bool compactDefaults, string extra)
    {
        var dispatcher = await StartAsync(compactDefaults);
        JsonElement result = Result(await CallAsync(dispatcher, $",\"basis\":\"TimeAtPrice\",\"binCount\":3,\"includeRanking\":true,\"allowUnregistered\":true{extra}", symbol: ThirdsSymbol));

        // compactDefaults makes compactRows the default (ranking as rows) and decimalPlaces 4; the plain call writes row objects. Read both forms.
        decimal[] shares;
        decimal[] cumulative;
        if (result.GetProperty("ranking").GetArrayLength() > 0)
        {
            JsonElement[] rows = result.GetProperty("ranking").EnumerateArray().ToArray();
            shares = rows.Select(r => r.GetProperty("share").GetDecimal()).ToArray();
            cumulative = rows.Select(r => r.GetProperty("cumulativeShare").GetDecimal()).ToArray();
        }
        else
        {
            JsonElement[][] rows = result.GetProperty("rankingRows").EnumerateArray().Select(r => r.EnumerateArray().ToArray()).ToArray();
            shares = rows.Select(r => r[5].GetDecimal()).ToArray();
            cumulative = rows.Select(r => r[6].GetDecimal()).ToArray();
        }

        Assert.Equal(new[] { 0.333333m, 0.333333m, 0.333333m }, shares);
        Assert.Equal(new[] { 0.333333m, 0.666667m, 1m }, cumulative);
    }

    [Fact]
    public async Task Ranking_Prices_FollowDecimalPlaces_WhileVolumesAreNeverRounded()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"includeRanking\":true,\"decimalPlaces\":1"));

        IReadOnlyList<VolumeBinRank> expected = VolumeProfileRanking.Rank(VolumeAnalysis.CalculateProfile(Candles(), 20, VolumeDistributionMode.Proportional));
        JsonElement[] rows = result.GetProperty("ranking").EnumerateArray().ToArray();
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(Math.Round(expected[i].LowerBound, 1, MidpointRounding.AwayFromZero), rows[i].GetProperty("priceLow").GetDecimal());
            Assert.Equal(Math.Round(expected[i].UpperBound, 1, MidpointRounding.AwayFromZero), rows[i].GetProperty("priceHigh").GetDecimal());
            Assert.Equal(Math.Round(expected[i].Price, 1, MidpointRounding.AwayFromZero), rows[i].GetProperty("price").GetDecimal());
            Assert.Equal(expected[i].Volume, rows[i].GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task Ranking_AllVolumesZero_IsEmpty_WhileTheExistingPocStillReportsTheFirstBin()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":10,\"includeRanking\":true,\"allowUnregistered\":true", symbol: ZeroVolumeSymbol));

        Assert.Equal(0, result.GetProperty("rankingCount").GetInt32());
        Assert.Equal(0, result.GetProperty("ranking").GetArrayLength());
        Assert.Equal(0, result.GetProperty("profileVolume").GetInt64());

        // The existing poc field is unchanged (compatibility): the price of the first bin.
        List<VolumeBin> bins = VolumeAnalysis.CalculateProfile(FlatBars(ZeroVolumeCloses(), 0), 10, VolumeDistributionMode.Proportional);
        Assert.Equal(bins[0].Price, result.GetProperty("poc").GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Ranking_SingleBinProfile_IsRankOne()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"binCount\":20,\"includeRanking\":true,\"allowUnregistered\":true", symbol: FlatSymbol));

        JsonElement row = Assert.Single(result.GetProperty("ranking").EnumerateArray().ToArray());
        Assert.Equal(1, row.GetProperty("rank").GetInt32());
        Assert.Equal(100m, row.GetProperty("price").GetDecimal());
        Assert.Equal(Days * 1000, row.GetProperty("volume").GetInt64());
        Assert.Equal(1m, row.GetProperty("share").GetDecimal());
        Assert.Equal(1m, row.GetProperty("cumulativeShare").GetDecimal());
    }
}
