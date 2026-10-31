using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class FindSupportResistanceToolTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "SRTEST";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "srtest");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + (decimal)Math.Sin(i / 5.0) * 10m).ToList();

    public FindSupportResistanceToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, _closes);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string extra = "", string symbol = Symbol, string timeFrame = "D1")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_find_support_resistance", $"{{\"symbol\":\"{symbol}\",\"timeFrame\":\"{timeFrame}\"{extra}}}"))!;

    private static JsonElement Result(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response);
    }

    private static string ErrorCode(JsonDocument response)
    {
        Assert.True(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.ErrorCodeOf(response);
    }

    [Fact]
    public async Task FindSupportResistance_EqualsDirectSsaSupportResistanceEngine()
    {
        var dispatcher = await StartAsync();
        JsonElement result = Result(await CallAsync(dispatcher, ",\"mode\":\"StructuralPivots\",\"embeddingDimension\":15,\"maxLevels\":10"));

        Assert.Equal("SRTEST", result.GetProperty("symbol").GetString());
        Assert.Equal("StructuralPivots", result.GetProperty("mode").GetString());
        Assert.Equal(Days, result.GetProperty("barCount").GetInt32());

        // Direct Core engine calculation
        var date = new DateTime(2024, 1, 1);
        double[] samples = _closes.Select(c => (double)c).ToArray();
        DateTime[] timestamps = Enumerable.Range(0, Days).Select(i => date.AddDays(i)).ToArray();
        double currentPrice = (double)_closes[^1];

        var expectedResult = SsaSupportResistanceEngine.Calculate(
            samples,
            timestamps,
            mode: SsaSupportResistanceMode.StructuralPivots,
            embeddingDimension: 15,
            numComponents: 2,
            autoRank: true,
            detrendMode: SsaDetrendMode.LeastSquaresLinear,
            maxLevelsPerSide: 11,
            clusterTolerance: 0.5m,
            multiplier: 2.0m,
            currentPrice: currentPrice);

        if (expectedResult.ActiveSupport.HasValue)
        {
            Assert.Equal((decimal)expectedResult.ActiveSupport.Value, result.GetProperty("activeSupport").GetDecimal());
        }
        if (expectedResult.ActiveResistance.HasValue)
        {
            Assert.Equal((decimal)expectedResult.ActiveResistance.Value, result.GetProperty("activeResistance").GetDecimal());
        }

        JsonElement[] levels = result.GetProperty("levels").EnumerateArray().ToArray();
        Assert.NotEmpty(levels);

        var expectedAll = new List<SsaSupportResistanceLevel>();
        if (expectedResult.ResistanceLevels != null) expectedAll.AddRange(expectedResult.ResistanceLevels);
        if (expectedResult.SupportLevels != null) expectedAll.AddRange(expectedResult.SupportLevels);
        var expectedSorted = expectedAll.OrderByDescending(l => l.StrengthScore).ThenByDescending(l => l.Price).Take(10).ToList();

        Assert.Equal(expectedSorted.Count, levels.Length);
        for (int i = 0; i < expectedSorted.Count; i++)
        {
            Assert.Equal((decimal)expectedSorted[i].Price, levels[i].GetProperty("price").GetDecimal());
            Assert.Equal(expectedSorted[i].IsResistance ? "Resistance" : "Support", levels[i].GetProperty("kind").GetString());
            Assert.Equal(expectedSorted[i].Hits, levels[i].GetProperty("hits").GetInt32());
            Assert.Equal(expectedSorted[i].StrengthScore, levels[i].GetProperty("strengthScore").GetDouble());
        }

        // Verify sorted by strength descending
        for (int i = 1; i < levels.Length; i++)
        {
            double prevStrength = levels[i - 1].GetProperty("strengthScore").GetDouble();
            double curStrength = levels[i].GetProperty("strengthScore").GetDouble();
            Assert.True(prevStrength >= curStrength);
        }
    }

    [Fact]
    public async Task FindSupportResistance_MutationTest_FutureBarsDoNotAffectHistoricalLevels()
    {
        var date = new DateTime(2024, 1, 1);
        var cutIndex = 60;
        var endDate = date.AddDays(cutIndex).ToString("yyyy-MM-dd");

        var dispatcher = await StartAsync();
        JsonElement resultBefore = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"mode\":\"StructuralPivots\""));

        // Mutate bars strictly after endDate
        var modifiedCloses = new List<decimal>(_closes);
        for (int i = cutIndex + 1; i < modifiedCloses.Count; i++)
        {
            modifiedCloses[i] = 9999m + i;
        }
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, modifiedCloses);

        JsonElement resultAfter = Result(await CallAsync(dispatcher, $",\"endDate\":\"{endDate}\",\"mode\":\"StructuralPivots\""));

        Assert.Equal(resultBefore.GetProperty("currentPrice").GetDecimal(), resultAfter.GetProperty("currentPrice").GetDecimal());

        JsonElement[] levelsBefore = resultBefore.GetProperty("levels").EnumerateArray().ToArray();
        JsonElement[] levelsAfter = resultAfter.GetProperty("levels").EnumerateArray().ToArray();

        Assert.Equal(levelsBefore.Length, levelsAfter.Length);
        for (int i = 0; i < levelsBefore.Length; i++)
        {
            Assert.Equal(levelsBefore[i].GetProperty("price").GetDecimal(), levelsAfter[i].GetProperty("price").GetDecimal());
            Assert.Equal(levelsBefore[i].GetProperty("kind").GetString(), levelsAfter[i].GetProperty("kind").GetString());
            Assert.Equal(levelsBefore[i].GetProperty("hits").GetInt32(), levelsAfter[i].GetProperty("hits").GetInt32());
        }
    }

    [Fact]
    public async Task FindSupportResistance_InvalidArguments_ThrowsToolException()
    {
        var dispatcher = await StartAsync();

        // invalid mode
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"mode\":\"UnknownMode\"")));
        // embeddingDimension < 2
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"embeddingDimension\":1")));
        // maxLevels < 1
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"maxLevels\":0")));
        // maxLevels > 20 (standard limit)
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"maxLevels\":21")));
        // embeddingDimension > max
        Assert.Equal("LIMIT_EXCEEDED", ErrorCode(await CallAsync(dispatcher, ",\"embeddingDimension\":10001")));
        // clusterTolerance <= 0
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"clusterTolerance\":0.0")));
        // multiplier <= 0
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await CallAsync(dispatcher, ",\"multiplier\":0.0")));
        // timeFrame required
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(await McpTestSupport.CallAsync(dispatcher, "sa_find_support_resistance", $"{{\"symbol\":\"{Symbol}\"}}")!));
    }
}
