using System.Text.Json;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// MCP integration tests for the RS Rating column in sa_get_capabilities and sa_run_screener
/// (filtering, ranking, includeMatchedValues, and snapshot boundary rejections).
/// </summary>
public sealed class ScreenerRsRatingMcpTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private ScreenerTestWorld Trend()
    {
        ScreenerTestWorld world = ScreenerTestWorld.Trend();
        _disposables.Add(world);
        return world;
    }

    [Fact]
    public async Task CapabilitiesCatalog_ListsRsRatingAsEvaluableNumericColumn()
    {
        using JsonDocument catalogue = JsonDocument.Parse(
            await Trend().CallAsync("sa_get_capabilities", """{"includeScreenerCatalog":true,"indicatorTypeFilter":["SMA"]}"""));

        JsonElement columns = catalogue.RootElement.GetProperty("screener").GetProperty("columns");
        JsonElement? rsColumn = null;
        foreach (JsonElement col in columns.EnumerateArray())
        {
            if (col.GetProperty("name").GetString() == "RS Rating")
            {
                rsColumn = col;
                break;
            }
        }

        Assert.True(rsColumn.HasValue, "Expected 'RS Rating' to be exposed in screener.columns");
        bool isText = rsColumn!.Value.TryGetProperty("isText", out JsonElement isTextEl) && isTextEl.GetBoolean();
        Assert.False(isText);
    }

    [Fact]
    public async Task RunScreener_FilterSortByAndIncludeMatchedValues_EvaluateRsRatingAcrossParquetUniverse()
    {
        ScreenerTestWorld world = Trend();

        // Filter: RS Rating > 50 (only UP = 99 matches; DOWN = 1 and LATE = null do not match).
        using JsonDocument filtered = JsonDocument.Parse(await world.CallAsync("sa_run_screener", """
            {
              "conditionTree": {
                "left": { "column": "RS Rating" },
                "comparison": "GreaterThan",
                "right": 50
              },
              "includeMatchedValues": true
            }
            """));

        JsonElement filteredRoot = filtered.RootElement;
        Assert.Equal(new[] { "UP" }, filteredRoot.GetProperty("matchedSymbols").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(99m, filteredRoot.GetProperty("matchedValues").GetProperty("UP").GetProperty("L0").GetProperty("left").GetDecimal());

        // SortBy RS Rating Descending across all 3 symbols (Match all Close > 0):
        // UP = 99, DOWN = 1, LATE = null (only 65 bars < 253 required closes; null sorts last).
        using JsonDocument sorted = JsonDocument.Parse(await world.CallAsync("sa_run_screener", """
            {
              "conditionTree": {
                "left": { "price": "Close" },
                "comparison": "GreaterThan",
                "right": 0
              },
              "sortBy": { "column": "RS Rating" },
              "sortDirection": "Descending"
            }
            """));

        JsonElement ranking = sorted.RootElement.GetProperty("ranking");
        Assert.Equal(new[] { "UP", "DOWN", "LATE" }, ranking.EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!).ToArray());
        Assert.Equal(99m, ranking[0].GetProperty("sortValue").GetDecimal());
        Assert.Equal(1m, ranking[1].GetProperty("sortValue").GetDecimal());
        Assert.Equal(JsonValueKind.Null, ranking[2].GetProperty("sortValue").ValueKind);
    }

    [Fact]
    public async Task RunScreener_RejectsOffsetTimeFrameAndAsOfDateOnRsRating()
    {
        ScreenerTestWorld world = Trend();

        using (JsonDocument offsetResp = await world.CallRawAsync("sa_run_screener", """
            {
              "conditionTree": {
                "left": { "column": "RS Rating", "offset": 1 },
                "comparison": "GreaterThan",
                "right": 50
              }
            }
            """))
        {
            Assert.True(McpTestSupport.IsError(offsetResp));
            Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(offsetResp));
            Assert.Equal("conditionTree.left.offset", McpTestSupport.Structured(offsetResp).GetProperty("param").GetString());
        }

        using (JsonDocument frameResp = await world.CallRawAsync("sa_run_screener", """
            {
              "conditionTree": {
                "left": { "column": "RS Rating", "timeFrame": "D1" },
                "comparison": "GreaterThan",
                "right": 50
              }
            }
            """))
        {
            Assert.True(McpTestSupport.IsError(frameResp));
            Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(frameResp));
            Assert.Equal("conditionTree.left.timeFrame", McpTestSupport.Structured(frameResp).GetProperty("param").GetString());
        }

        using (JsonDocument asOfResp = await world.CallRawAsync("sa_run_screener", """
            {
              "asOfDate": "2025-01-15",
              "conditionTree": {
                "left": { "column": "RS Rating" },
                "comparison": "GreaterThan",
                "right": 50
              }
            }
            """))
        {
            Assert.True(McpTestSupport.IsError(asOfResp));
            Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(asOfResp));
            Assert.Equal("conditionTree.left.column", McpTestSupport.Structured(asOfResp).GetProperty("param").GetString());
        }

        using (JsonDocument sortOffsetResp = await world.CallRawAsync("sa_run_screener", """
            {
              "conditionTree": {
                "left": { "price": "Close" },
                "comparison": "GreaterThan",
                "right": 0
              },
              "sortBy": { "column": "RS Rating", "offset": 1 }
            }
            """))
        {
            Assert.True(McpTestSupport.IsError(sortOffsetResp));
            Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(sortOffsetResp));
            Assert.Equal("sortBy.offset", McpTestSupport.Structured(sortOffsetResp).GetProperty("param").GetString());
        }
    }
}
