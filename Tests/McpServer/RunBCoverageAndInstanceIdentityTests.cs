using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

public sealed class RunBCoverageAndInstanceIdentityTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(30, "msft", "8035-t");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private McpMessageDispatcher StartMcp(
        IReadOnlyDictionary<string, string?> configValues,
        bool enableDiscussion = false,
        Action<IReadOnlyStockAnalyzerGateway>? captureGateway = null)
        => StartMcp(
            new ConfigurationBuilder().AddInMemoryCollection(configValues).Build(),
            enableDiscussion,
            captureGateway);

    private McpMessageDispatcher StartMcp(
        IConfiguration configuration,
        bool enableDiscussion = false,
        Action<IReadOnlyStockAnalyzerGateway>? captureGateway = null)
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            configuration,
            new LogSink(new StringWriter()),
            enableDiscussion: enableDiscussion,
            gatewayWrapper: captureGateway is null ? null : g =>
            {
                captureGateway(g);
                return g;
            });
        _runtimes.Add(runtime);
        runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None).GetAwaiter().GetResult();
        return runtime.Dispatcher;
    }

    private void WriteSeries8035TWithInvalidBars()
    {
        // Calendar weeks (Monday-based):
        // Week 1: 2022-05-09 (Mon) .. 2022-05-13 (Fri) -> clean
        // Week 2: 2022-05-16 (Mon) .. 2022-05-20 (Fri) -> 2022-05-17 invalid (Close > High), 2022-05-20 ALSO Close = 3950 > Week High 3900
        // Week 3: 2022-06-06 (Mon) .. 2022-06-10 (Fri) -> 2022-06-08 invalid (Low > Open)
        var rows = new List<string>();
        DateOnly start = new(2022, 5, 2);
        for (int i = 0; i < 45; i++)
        {
            DateOnly day = start.AddDays(i);
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            if (day == new DateOnly(2022, 5, 17))
            {
                rows.Add("(DATE '2022-05-17', CAST(3821.333 AS DOUBLE), CAST(3866.667 AS DOUBLE), CAST(3770.667 AS DOUBLE), CAST(3869.333 AS DOUBLE), CAST(1000 AS BIGINT))");
            }
            else if (day == new DateOnly(2022, 5, 20))
            {
                // Non-positive Low breaks W1 and MN1 independently of D1 aggregation rules.
                rows.Add("(DATE '2022-05-20', CAST(3850.0 AS DOUBLE), CAST(3890.0 AS DOUBLE), CAST(0.0 AS DOUBLE), CAST(3860.0 AS DOUBLE), CAST(1000 AS BIGINT))");
            }
            else if (day == new DateOnly(2022, 6, 8))
            {
                rows.Add("(DATE '2022-06-08', CAST(3810.0 AS DOUBLE), CAST(3850.0 AS DOUBLE), CAST(3830.0 AS DOUBLE), CAST(3840.0 AS DOUBLE), CAST(1000 AS BIGINT))");
            }
            else
            {
                rows.Add(FormattableString.Invariant(
                    $"(DATE '{day:yyyy-MM-dd}', CAST(3810.0 AS DOUBLE), CAST(3850.0 AS DOUBLE), CAST(3790.0 AS DOUBLE), CAST(3820.0 AS DOUBLE), CAST(1000 AS BIGINT))"));
            }
        }

        string query = $"""
            SELECT * FROM (VALUES
                {string.Join(",\n                ", rows)}
            ) AS t(date, open, high, low, close, volume)
            """;
        McpTestSupport.WriteParquet(Path.Combine(_fixture.DailyDirectory, "8035-T.parquet"), query);
    }

    [Fact]
    public async Task Coverage_ReportsZeroWithoutDateKeyForCleanBars_AndOldestInvalidDatePerTimeFrame()
    {
        WriteSeries8035TWithInvalidBars();
        IReadOnlyStockAnalyzerGateway? gateway = null;
        McpMessageDispatcher dispatcher = StartMcp(McpTestSupport.DataTreeConfiguration(_fixture), captureGateway: g => gateway = g);
        Assert.NotNull(gateway);

        using JsonDocument resp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_get_symbol_coverage",
            """{"symbols":["MSFT","8035-T"]}"""))!;
        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());

        JsonElement results = McpTestSupport.Structured(resp).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());

        // Clean symbol: invalidBarCount == 0 and firstInvalidBarDate is OMITTED on all timeframes.
        JsonElement clean = results[0];
        Assert.Equal("MSFT", clean.GetProperty("symbol").GetString());
        foreach (JsonElement tf in clean.GetProperty("timeFrames").EnumerateArray())
        {
            Assert.Equal(0, tf.GetProperty("invalidBarCount").GetInt32());
            Assert.False(tf.TryGetProperty("firstInvalidBarDate", out _), "Clean timeframe must omit firstInvalidBarDate.");
        }

        // Corrupt symbol: compare each timeframe against a brute-force scan over that timeframe's loaded bars.
        JsonElement corrupt = results[1];
        Assert.Equal("8035-T", corrupt.GetProperty("symbol").GetString());
        foreach (JsonElement tfEntry in corrupt.GetProperty("timeFrames").EnumerateArray())
        {
            TimeFrame tf = Enum.Parse<TimeFrame>(tfEntry.GetProperty("timeFrame").GetString()!);
            IReadOnlyList<CandleData> bars = await gateway!.GetCandlesAsync("8035-T", tf, int.MaxValue);
            CandleData[] expectedInvalid = bars.Where(b => !CandleInvariant.IsValidBar(b)).OrderBy(b => b.Timestamp).ToArray();

            int reportedCount = tfEntry.GetProperty("invalidBarCount").GetInt32();
            Assert.Equal(expectedInvalid.Length, reportedCount);
            if (expectedInvalid.Length == 0)
            {
                Assert.False(tfEntry.TryGetProperty("firstInvalidBarDate", out _));
            }
            else
            {
                string expectedFirstDate = expectedInvalid[0].Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Assert.Equal(expectedFirstDate, tfEntry.GetProperty("firstInvalidBarDate").GetString());
            }
        }

        // Explicitly confirm D1 and W1/MN1 all saw invalid bars in our fixture (D1 oldest = 2022-05-17).
        JsonElement d1 = corrupt.GetProperty("timeFrames").EnumerateArray().Single(e => e.GetProperty("timeFrame").GetString() == "D1");
        Assert.Equal(3, d1.GetProperty("invalidBarCount").GetInt32());
        Assert.Equal("2022-05-17", d1.GetProperty("firstInvalidBarDate").GetString());
        JsonElement w1 = corrupt.GetProperty("timeFrames").EnumerateArray().Single(e => e.GetProperty("timeFrame").GetString() == "W1");
        Assert.True(w1.GetProperty("invalidBarCount").GetInt32() >= 1);
        JsonElement mn1 = corrupt.GetProperty("timeFrames").EnumerateArray().Single(e => e.GetProperty("timeFrame").GetString() == "MN1");
        Assert.True(mn1.GetProperty("invalidBarCount").GetInt32() >= 1);
    }

    [Theory]
    [InlineData(@"C:\Users\TestUser\Data")]
    [InlineData(@"\\server\share\StockData")]
    public void DataSetId_MatchesLowercaseHex8WithoutPaths_AndDiffersAcrossFolders(string folderA)
    {
        var identityA = new InstanceDataIdentity(new EmptyReadOnlyStockAnalyzerGateway(), folderA);
        var identityB = new InstanceDataIdentity(new EmptyReadOnlyStockAnalyzerGateway(), folderA + "_other");

        string idA = identityA.ComputeDataSetId();
        string idB = identityB.ComputeDataSetId();

        Assert.Matches("^[0-9a-f]{8}$", idA);
        Assert.Matches("^[0-9a-f]{8}$", idB);
        Assert.NotEqual(idA, idB);
        Assert.DoesNotContain(":", idA, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\", idA, StringComparison.Ordinal);
        Assert.DoesNotContain("/", idA, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsAndCoverage_ShareIdentityFields_MatchDiscussionSharingKey_AndHandleLatestBarDate()
    {
        WriteSeries8035TWithInvalidBars(); // latest 2022-07-01 (45 calendar days from 2022-05-02), MSFT has 30 days from 2024-01-01
        McpMessageDispatcher dispatcher = StartMcp(McpTestSupport.DataTreeConfiguration(_fixture), enableDiscussion: true);

        using JsonDocument settingsResp = (await McpTestSupport.CallAsync(dispatcher, "sa_get_settings"))!;
        using JsonDocument coverageBothResp = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["8035-T","MSFT"]}"""))!;
        using JsonDocument coverageOnly8035Resp = (await McpTestSupport.CallAsync(dispatcher, "sa_get_symbol_coverage", """{"symbols":["8035-T"]}"""))!;
        using JsonDocument tickersResp = (await McpTestSupport.CallAsync(dispatcher, "sa_list_tickers"))!;
        using JsonDocument discussionResp = (await McpTestSupport.CallAsync(dispatcher, "sa_discussion_status"))!;

        Assert.False(McpTestSupport.IsError(settingsResp), McpTestSupport.Structured(settingsResp).ToString());
        Assert.False(McpTestSupport.IsError(coverageBothResp), McpTestSupport.Structured(coverageBothResp).ToString());
        Assert.False(McpTestSupport.IsError(coverageOnly8035Resp), McpTestSupport.Structured(coverageOnly8035Resp).ToString());
        Assert.False(McpTestSupport.IsError(tickersResp));
        Assert.False(McpTestSupport.IsError(discussionResp), McpTestSupport.Structured(discussionResp).ToString());

        JsonElement settingsJson = McpTestSupport.Structured(settingsResp);
        JsonElement covBothJson = McpTestSupport.Structured(coverageBothResp);
        JsonElement cov8035Json = McpTestSupport.Structured(coverageOnly8035Resp);
        int expectedTickerCount = McpTestSupport.Structured(tickersResp).GetProperty("totalCount").GetInt32();
        string sharingKey = McpTestSupport.Structured(discussionResp).GetProperty("sharingKey").GetString()!;

        string settingsId = settingsJson.GetProperty("dataSetId").GetString()!;
        Assert.Matches("^[0-9a-f]{8}$", settingsId);
        Assert.Equal(sharingKey, settingsId);
        Assert.Equal(settingsId, covBothJson.GetProperty("dataSetId").GetString());
        Assert.Equal(settingsId, cov8035Json.GetProperty("dataSetId").GetString());

        Assert.Equal(expectedTickerCount, settingsJson.GetProperty("tickerCount").GetInt32());
        Assert.Equal(expectedTickerCount, covBothJson.GetProperty("tickerCount").GetInt32());
        Assert.Equal(expectedTickerCount, cov8035Json.GetProperty("tickerCount").GetInt32());

        // MSFT latest bar (2024-02-09) is newer than 8035-T (2022-07-01):
        // settings and full coverage see MSFT's date, while 8035-T-only coverage sees 8035-T's date.
        string expectedMsftLatest = covBothJson.GetProperty("results")[1]
            .GetProperty("timeFrames").EnumerateArray().Single(tf => tf.GetProperty("timeFrame").GetString() == "D1")
            .GetProperty("latestTimestamp").GetString()!;
        string expected8035Latest = covBothJson.GetProperty("results")[0]
            .GetProperty("timeFrames").EnumerateArray().Single(tf => tf.GetProperty("timeFrame").GetString() == "D1")
            .GetProperty("latestTimestamp").GetString()!;

        Assert.Equal(expectedMsftLatest, settingsJson.GetProperty("latestBarDate").GetString());
        Assert.Equal(expectedMsftLatest, covBothJson.GetProperty("latestBarDate").GetString());
        Assert.Equal(expected8035Latest, cov8035Json.GetProperty("latestBarDate").GetString());
    }

    [Fact]
    public async Task SettingsAndCoverage_ZeroCases_WriteNullLatestBarDateAndKeepKey()
    {
        // 1) Coverage zero-case: non-empty instance, but requested symbol is not found -> latestBarDate: null, tickerCount: real count.
        McpMessageDispatcher populated = StartMcp(McpTestSupport.DataTreeConfiguration(_fixture));
        using JsonDocument unfoundCovResp = (await McpTestSupport.CallAsync(populated, "sa_get_symbol_coverage", """{"symbols":["UNKNOWN999"]}"""))!;
        Assert.False(McpTestSupport.IsError(unfoundCovResp));

        JsonElement unfoundCov = McpTestSupport.Structured(unfoundCovResp);
        Assert.Equal(2, unfoundCov.GetProperty("tickerCount").GetInt32());
        Assert.True(unfoundCov.TryGetProperty("latestBarDate", out JsonElement covLatest));
        Assert.Equal(JsonValueKind.Null, covLatest.ValueKind);

        // 2) Settings zero-case: empty data tree with 0 registered tickers -> tickerCount: 0, latestBarDate: null.
        string emptyRoot = Path.Combine(Path.GetTempPath(), "sa_empty_identity_" + Guid.NewGuid().ToString("N"));
        try
        {
            McpMessageDispatcher emptyDispatcher = StartMcp(McpTestSupport.EmptyDataConfiguration(emptyRoot));
            using JsonDocument emptySettingsResp = (await McpTestSupport.CallAsync(emptyDispatcher, "sa_get_settings"))!;
            Assert.False(McpTestSupport.IsError(emptySettingsResp), McpTestSupport.Structured(emptySettingsResp).ToString());

            JsonElement emptySettings = McpTestSupport.Structured(emptySettingsResp);
            Assert.Matches("^[0-9a-f]{8}$", emptySettings.GetProperty("dataSetId").GetString()!);
            Assert.Equal(0, emptySettings.GetProperty("tickerCount").GetInt32());
            Assert.True(emptySettings.TryGetProperty("latestBarDate", out JsonElement settingsLatest));
            Assert.Equal(JsonValueKind.Null, settingsLatest.ValueKind);
        }
        finally
        {
            if (Directory.Exists(emptyRoot))
            {
                Directory.Delete(emptyRoot, recursive: true);
            }
        }
    }
}
