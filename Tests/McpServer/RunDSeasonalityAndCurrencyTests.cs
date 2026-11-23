using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

public sealed class RunDSeasonalityAndCurrencyTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(
        30,
        "midnov",
        "midfeb",
        "monthend",
        "usd1",
        "usd2",
        "jpy1",
        "nometa",
        "nameta");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartMcpAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture))
            .Build();
        McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private void SeedRange(string symbol, DateOnly start, DateOnly end, decimal basePrice = 100m)
    {
        var rows = new List<string>();
        int idx = 0;
        for (DateOnly d = start; d <= end; d = d.AddDays(1), idx++)
        {
            decimal close = basePrice + (idx % 17) * 0.5m + (idx * 0.02m);
            decimal open = close - 0.2m;
            decimal high = close + 0.5m;
            decimal low = close - 0.5m;
            rows.Add(FormattableString.Invariant(
                $"(DATE '{d:yyyy-MM-dd}', CAST({open} AS DOUBLE), CAST({high} AS DOUBLE), CAST({low} AS DOUBLE), CAST({close} AS DOUBLE), CAST(1000 AS BIGINT))"));
        }

        string query = $"""
            SELECT * FROM (VALUES
                {string.Join(",\n                ", rows)}
            ) AS t(date, open, high, low, close, volume)
            """;
        McpTestSupport.WriteParquet(Path.Combine(_fixture.DailyDirectory, symbol + ".parquet"), query);
    }

    [Theory]
    [InlineData("D1")]
    [InlineData("W1")]
    [InlineData("MN1")]
    public async Task Seasonality_MidMonthFixture_MarksOnlyAffectedMonthAndAnnualCellsAsPartial(string timeFrame)
    {
        SeedRange("MIDNOV", new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 13));
        McpMessageDispatcher dispatcher = await StartMcpAsync();

        using JsonDocument resp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_get_seasonality",
            $$"""{"symbol":"MIDNOV","timeFrame":"{{timeFrame}}","years":5,"startMonth":1}"""))!;
        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());

        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal("2026-11-13", root.GetProperty("referenceDate").GetString());
        Assert.Equal("2026-11-30", root.GetProperty("lastBarPeriodEnd").GetString());
        Assert.True(root.GetProperty("lastBarPartial").GetBoolean());
        Assert.False(root.GetProperty("droppedPartialBar").GetBoolean());

        var partialPairs = new List<(string Label, int Year)>();
        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            string label = row.GetProperty("label").GetString()!;
            foreach (JsonElement cell in row.GetProperty("cells").EnumerateArray())
            {
                int year = cell.GetProperty("year").GetInt32();
                bool hasPartial = cell.TryGetProperty("partial", out JsonElement partialProp);
                if (hasPartial)
                {
                    Assert.True(partialProp.GetBoolean());
                    Assert.NotEqual(JsonValueKind.Null, cell.GetProperty("returnRatio").ValueKind);
                    partialPairs.Add((label, year));
                }

                if (label == "Dec" && year == 2026)
                {
                    Assert.Equal(JsonValueKind.Null, cell.GetProperty("returnRatio").ValueKind);
                    Assert.False(hasPartial);
                }
            }
        }

        Assert.Equal(new[] { ("Nov", 2026), ("Annual", 2026) }, partialPairs);
    }

    [Fact]
    public async Task Seasonality_NonJanuaryStartMonth_MapsCycleYearCorrectlyForPartialCells()
    {
        // With startMonth=4, 2026-02-13 belongs to fiscal cycle year 2025 (2025-04 .. 2026-03).
        SeedRange("MIDFEB", new DateOnly(2024, 4, 1), new DateOnly(2026, 2, 13));
        McpMessageDispatcher dispatcher = await StartMcpAsync();

        using JsonDocument resp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_get_seasonality",
            """{"symbol":"MIDFEB","timeFrame":"D1","years":5,"startMonth":4}"""))!;
        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());

        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal("2026-02-13", root.GetProperty("referenceDate").GetString());
        Assert.Equal("2026-02-28", root.GetProperty("lastBarPeriodEnd").GetString());
        Assert.True(root.GetProperty("lastBarPartial").GetBoolean());

        var partialPairs = new List<(string Label, int Year)>();
        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            string label = row.GetProperty("label").GetString()!;
            foreach (JsonElement cell in row.GetProperty("cells").EnumerateArray())
            {
                if (cell.TryGetProperty("partial", out _))
                {
                    partialPairs.Add((label, cell.GetProperty("year").GetInt32()));
                }
            }
        }

        Assert.Equal(new[] { ("Feb", 2025), ("Annual", 2025) }, partialPairs);
    }

    [Theory]
    [InlineData("D1")]
    [InlineData("MN1")]
    public async Task Seasonality_CompleteMonthEndFixture_HasNoPartialKeyAnywhere(string timeFrame)
    {
        SeedRange("MONTHEND", new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 31));
        McpMessageDispatcher dispatcher = await StartMcpAsync();

        using JsonDocument resp = (await McpTestSupport.CallAsync(
            dispatcher,
            "sa_get_seasonality",
            $$"""{"symbol":"MONTHEND","timeFrame":"{{timeFrame}}","years":5,"startMonth":1}"""))!;
        Assert.False(McpTestSupport.IsError(resp), McpTestSupport.Structured(resp).ToString());

        JsonElement root = McpTestSupport.Structured(resp);
        Assert.Equal("2026-10-31", root.GetProperty("referenceDate").GetString());
        Assert.Equal("2026-10-31", root.GetProperty("lastBarPeriodEnd").GetString());
        Assert.False(root.GetProperty("lastBarPartial").GetBoolean());
        Assert.False(root.GetProperty("droppedPartialBar").GetBoolean());

        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            foreach (JsonElement cell in row.GetProperty("cells").EnumerateArray())
            {
                Assert.False(cell.TryGetProperty("partial", out _), "Complete month-end fixture must omit 'partial' on all cells.");
            }
        }
    }

    [Fact]
    public async Task RelativePerformance_CurrencyWarnings_ApplyOnlyToRatioAndSpread()
    {
        DateOnly start = new(2024, 1, 1);
        DateOnly end = new(2024, 6, 30);
        SeedRange("USD1", start, end, 100m);
        SeedRange("USD2", start, end, 200m);
        SeedRange("JPY1", start, end, 3800m);
        SeedRange("NOMETA", start, end, 150m);
        SeedRange("NAMETA", start, end, 150m);

        await _fixture.SeedMetadataAsync("USD1", TickerMetadata.Unknown with { Currency = "USD" });
        await _fixture.SeedMetadataAsync("USD2", TickerMetadata.Unknown with { Currency = "usd" });
        await _fixture.SeedMetadataAsync("JPY1", TickerMetadata.Unknown with { Currency = "JPY" });
        await _fixture.SeedMetadataAsync("NAMETA", TickerMetadata.Unknown with { Currency = "N/A" });

        McpMessageDispatcher dispatcher = await StartMcpAsync();

        static string[] Warnings(JsonDocument doc)
            => McpTestSupport.Structured(doc).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();

        // 1) Matching currencies (USD vs USD) -> NO currency warning in Ratio or Spread
        foreach (string mode in new[] { "Ratio", "Spread" })
        {
            using JsonDocument matchDoc = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_relative_performance",
                $$"""{"symbol":"USD1","timeFrame":"D1","comparisonSymbols":["USD2"],"mode":"{{mode}}"}"""))!;
            Assert.False(McpTestSupport.IsError(matchDoc));
            Assert.Empty(Warnings(matchDoc));
        }

        // 2) Mismatched currencies (JPY vs USD) -> CURRENCY_MISMATCH in Ratio and Spread; NONE in Performance or ZScore
        foreach (string mode in new[] { "Ratio", "Spread" })
        {
            using JsonDocument mismatchDoc = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_relative_performance",
                $$"""{"symbol":"JPY1","timeFrame":"D1","comparisonSymbols":["USD1"],"mode":"{{mode}}"}"""))!;
            Assert.False(McpTestSupport.IsError(mismatchDoc));
            string[] warnings = Warnings(mismatchDoc);
            Assert.Single(warnings);
            Assert.Equal($"CURRENCY_MISMATCH: JPY1 (JPY) vs USD1 (USD): {mode} mixes price units.", warnings[0]);
        }

        foreach (string mode in new[] { "Performance", "ZScore" })
        {
            using JsonDocument nonMixingDoc = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_relative_performance",
                $$"""{"symbol":"JPY1","timeFrame":"D1","comparisonSymbols":["USD1","NOMETA"],"mode":"{{mode}}"}"""))!;
            Assert.False(McpTestSupport.IsError(nonMixingDoc));
            Assert.Empty(Warnings(nonMixingDoc));
        }

        // 3) Missing or N/A currency -> CURRENCY_UNKNOWN for each unknown side (distinct from MISMATCH)
        using JsonDocument unknownPrimary = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_relative_performance",
            """{"symbol":"NOMETA","timeFrame":"D1","comparisonSymbols":["USD1"],"mode":"Ratio"}"""))!;
        Assert.Equal(
            new[] { "CURRENCY_UNKNOWN: NOMETA has no stored currency; the unit of Ratio cannot be confirmed." },
            Warnings(unknownPrimary));

        using JsonDocument unknownBoth = (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_relative_performance",
            """{"symbol":"USD1","timeFrame":"D1","comparisonSymbols":["NAMETA","NOMETA"],"mode":"Spread"}"""))!;
        Assert.Equal(
            new[]
            {
                "CURRENCY_UNKNOWN: NAMETA has no stored currency; the unit of Spread cannot be confirmed.",
                "CURRENCY_UNKNOWN: NOMETA has no stored currency; the unit of Spread cannot be confirmed.",
            },
            Warnings(unknownBoth));
    }
}
