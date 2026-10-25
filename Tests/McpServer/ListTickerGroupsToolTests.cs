using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class ListTickerGroupsToolTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(5, "MSFT", "AAPL");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly string _workspacePath;

    public ListTickerGroupsToolTests()
    {
        // Place workspace file in the config folder matching PathDiscovery convention
        string configDir = Path.Combine(_fixture.Root, "Config");
        Directory.CreateDirectory(configDir);
        _workspacePath = Path.Combine(configDir, LayoutConstants.DefaultWorkspaceFileName);
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
        {
            runtime.Dispose();
        }
        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        McpServerRuntime runtime = McpServerComposition.Create(
            configuration,
            new LogSink(new StringWriter()),
            workspaceSource: new FileReadOnlyWorkspaceSource(_workspacePath));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string argumentsJson = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_list_ticker_groups", argumentsJson))!;

    [Fact]
    public async Task WithoutWorkspaceFile_ReturnsThreeFixedRootsWithWorkspaceFoundFalse()
    {
        var dispatcher = await StartAsync();
        using var response = await CallAsync(dispatcher);

        Assert.False(McpTestSupport.IsError(response));
        JsonElement root = McpTestSupport.Structured(response);

        Assert.False(root.GetProperty("workspaceFound").GetBoolean());
        Assert.Equal(3, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, root.GetProperty("droppedFilterCount").GetInt32());
        Assert.False(root.GetProperty("treeTruncatedAtDepth").GetBoolean());

        var groups = root.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(3, groups.Length);
        Assert.Equal("allTickers", groups[0].GetProperty("kind").GetString());
        Assert.Equal(2, groups[0].GetProperty("memberCount").GetInt32()); // MSFT, AAPL
        Assert.Equal(0, groups[0].GetProperty("unregisteredMemberCount").GetInt32());
        Assert.Equal("watchlistCategory", groups[1].GetProperty("kind").GetString());
        Assert.Equal(0, groups[1].GetProperty("memberCount").GetInt32());
        Assert.Equal("portfolioCategory", groups[2].GetProperty("kind").GetString());
        Assert.Equal(0, groups[2].GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task WithWorkspaceFile_ReturnsGroupsInPreOrderWithAccurateCounts()
    {
        var watchlistId = Guid.NewGuid();
        var filterId = Guid.NewGuid();

        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Tech", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("UNREGISTERED_SYMBOL", DateTimeOffset.UtcNow)
                })
            },
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    Name = "High Close Filter",
                    ParentId = watchlistId,
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "Close", Operator = ">", Value = "150", IsCompareToField = false, CompareMode = "Numeric" }
                    }
                }
            }
        };

        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(workspace, _workspacePath);

        var dispatcher = await StartAsync();

        // 1. Without includeRules
        using var responseWithoutRules = await CallAsync(dispatcher);
        Assert.False(McpTestSupport.IsError(responseWithoutRules));
        JsonElement rootWithout = McpTestSupport.Structured(responseWithoutRules);
        var groupsWithout = rootWithout.GetProperty("groups").EnumerateArray().ToArray();

        var filterNodeWithout = groupsWithout.Single(g => g.GetProperty("kind").GetString() == "filter");
        Assert.Equal("High Close Filter", filterNodeWithout.GetProperty("name").GetString());
        Assert.Equal(1, filterNodeWithout.GetProperty("ruleCount").GetInt32());
        Assert.False(filterNodeWithout.TryGetProperty("rules", out _));
        Assert.Equal(JsonValueKind.Null, filterNodeWithout.GetProperty("memberCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, filterNodeWithout.GetProperty("unregisteredMemberCount").ValueKind);

        var watchlistNode = groupsWithout.Single(g => g.GetProperty("kind").GetString() == "watchlist");
        Assert.Equal("Tech", watchlistNode.GetProperty("name").GetString());
        Assert.Equal(1, watchlistNode.GetProperty("memberCount").GetInt32()); // MSFT
        Assert.Equal(1, watchlistNode.GetProperty("unregisteredMemberCount").GetInt32()); // UNREGISTERED_SYMBOL
        Assert.Equal(JsonValueKind.Null, watchlistNode.GetProperty("ruleCount").ValueKind);

        // 2. With includeRules: true
        using var responseWithRules = await CallAsync(dispatcher, """{"includeRules":true}""");
        Assert.False(McpTestSupport.IsError(responseWithRules));
        JsonElement rootWith = McpTestSupport.Structured(responseWithRules);
        var groupsWith = rootWith.GetProperty("groups").EnumerateArray().ToArray();

        var filterNodeWith = groupsWith.Single(g => g.GetProperty("kind").GetString() == "filter");
        Assert.True(filterNodeWith.TryGetProperty("rules", out var rulesElement));
        var rulesArray = rulesElement.EnumerateArray().ToArray();
        Assert.Single(rulesArray);
        Assert.Equal("Close", rulesArray[0].GetProperty("field").GetString());
        Assert.Equal(">", rulesArray[0].GetProperty("operator").GetString());
        Assert.Equal("150", rulesArray[0].GetProperty("value").GetString());
        Assert.False(rulesArray[0].GetProperty("isCompareToField").GetBoolean());
        Assert.Equal("Numeric", rulesArray[0].GetProperty("compareMode").GetString());
    }

    [Fact]
    public async Task WhenGroupCountExceedsMaxGroupNodes_ReturnsLimitExceeded()
    {
        // Override MaxGroupNodes to 2 (fewer than the 3 fixed roots)
        var dispatcher = await StartAsync(("MaxGroupNodes", "2"));
        using var response = await CallAsync(dispatcher);

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("LIMIT_EXCEEDED", error.GetProperty("code").GetString());
        Assert.Contains("Raise AiIntegration:MaxGroupNodes or remove groups", error.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task WhenWorkspaceFileIsCorrupt_ReturnsDataUnavailable()
    {
        await File.WriteAllTextAsync(_workspacePath, "{ this is invalid json }");
        var dispatcher = await StartAsync();
        using var response = await CallAsync(dispatcher);

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("DATA_UNAVAILABLE", error.GetProperty("code").GetString());
    }
}
