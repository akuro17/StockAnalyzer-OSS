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
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class ListTickersToolGroupIdTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(5, "MSFT", "AAPL", "NVDA");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly string _workspacePath;

    public ListTickersToolGroupIdTests()
    {
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
        => (await McpTestSupport.CallAsync(dispatcher, "sa_list_tickers", argumentsJson))!;

    [Fact]
    public async Task WithoutGroupId_DoesNotEmitGroupFields()
    {
        var dispatcher = await StartAsync();
        using var response = await CallAsync(dispatcher, "{}");

        Assert.False(McpTestSupport.IsError(response));
        JsonElement root = McpTestSupport.Structured(response);

        Assert.False(root.TryGetProperty("groupId", out _));
        Assert.False(root.TryGetProperty("groupKind", out _));
        Assert.False(root.TryGetProperty("groupUnregisteredMemberCount", out _));
        Assert.False(root.TryGetProperty("groupUnknownRuleFields", out _));
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task WithInvalidGroupId_ReturnsInvalidArgument()
    {
        var dispatcher = await StartAsync();
        using var response = await CallAsync(dispatcher, """{"groupId":"not-a-guid"}""");

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("groupId", error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task WithUnknownGroupId_ReturnsGroupNotFound()
    {
        var dispatcher = await StartAsync();
        var unknownId = Guid.NewGuid().ToString();
        using var response = await CallAsync(dispatcher, $$"""{"groupId":"{{unknownId}}"}""");

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("GROUP_NOT_FOUND", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("groupId", error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task WithStaticWatchlistGroupId_RestrictsMembersAndReportsMetadata()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Tech Watchlist", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("UNREGISTERED_SYM", DateTimeOffset.UtcNow)
                })
            }
        };
        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(workspace, _workspacePath);

        var dispatcher = await StartAsync();
        using var response = await CallAsync(dispatcher, $$"""{"groupId":"{{watchlistId}}"}""");

        Assert.False(McpTestSupport.IsError(response));
        JsonElement root = McpTestSupport.Structured(response);

        Assert.Equal(watchlistId.ToString("D").ToLowerInvariant(), root.GetProperty("groupId").GetString());
        Assert.Equal("watchlist", root.GetProperty("groupKind").GetString());
        Assert.Equal(1, root.GetProperty("groupUnregisteredMemberCount").GetInt32());
        Assert.Empty(root.GetProperty("groupUnknownRuleFields").EnumerateArray().ToArray());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());

        var tickers = root.GetProperty("tickers").EnumerateArray().Select(t => t.GetProperty("symbol").GetString()).ToArray();
        Assert.Equal(new[] { "MSFT" }, tickers);
    }

    [Fact]
    public async Task WithFilterGroupId_EvaluatesRulesAndReportsUnknownFields()
    {
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Sample Filter",
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "Symbol", Operator = "==", Value = "AAPL" },
                        new() { Field = "NonExistentColumn", Operator = ">", Value = "50" }
                    }
                }
            }
        };
        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(workspace, _workspacePath);

        var dispatcher = await StartAsync();
        using var response = await CallAsync(dispatcher, $$"""{"groupId":"{{filterId}}"}""");

        Assert.False(McpTestSupport.IsError(response));
        JsonElement root = McpTestSupport.Structured(response);

        Assert.Equal(filterId.ToString("D").ToLowerInvariant(), root.GetProperty("groupId").GetString());
        Assert.Equal("filter", root.GetProperty("groupKind").GetString());
        Assert.Equal(0, root.GetProperty("groupUnregisteredMemberCount").GetInt32());

        var unknownFields = root.GetProperty("groupUnknownRuleFields").EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Equal(new[] { "NonExistentColumn" }, unknownFields);

        // Rule with unknown column evaluated to false, so 0 matches
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task WithFilterGroupId_ExceedingLimit_ReturnsLimitExceeded()
    {
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Exceeding Filter",
                    Rules = new List<FilterRule>()
                }
            }
        };
        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(workspace, _workspacePath);

        // MaxFilterEvaluationSymbols set to 2, but registry has 3 symbols (MSFT, AAPL, NVDA)
        var dispatcher = await StartAsync(("MaxFilterEvaluationSymbols", "2"));
        using var response = await CallAsync(dispatcher, $$"""{"groupId":"{{filterId}}"}""");

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("groupId", error.GetProperty("param").GetString());
    }
}
