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

public sealed class RunScreenerToolGroupIdTests : IDisposable
{
    private const string SimpleCondition = """{"conditionTree":{"left":{"price":"Close"},"comparison":"GreaterThan","right":0}}""";
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(10, "MSFT", "AAPL", "NVDA");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly string _workspacePath;

    public RunScreenerToolGroupIdTests()
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

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string argumentsJson)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_run_screener", argumentsJson))!;

    [Fact]
    public async Task WithoutGroupIdOrSymbols_ReportsAllAvailableAndNullGroupId()
    {
        var dispatcher = await StartAsync();
        using var response = await CallAsync(dispatcher, SimpleCondition);

        Assert.False(McpTestSupport.IsError(response));
        JsonElement root = McpTestSupport.Structured(response);

        Assert.Equal("AllAvailable", root.GetProperty("universe").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("groupId").ValueKind);
        Assert.Equal(3, root.GetProperty("evaluatedSymbolCount").GetInt32());
    }

    [Fact]
    public async Task WithBothSymbolsAndGroupId_ReturnsInvalidArgument()
    {
        var dispatcher = await StartAsync();
        var groupId = Guid.NewGuid().ToString();
        var arguments = $$"""
        {
            "conditionTree": {"left":{"price":"Close"},"comparison":"GreaterThan","right":0},
            "symbols": ["MSFT"],
            "groupId": "{{groupId}}"
        }
        """;

        using var response = await CallAsync(dispatcher, arguments);

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("groupId", error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task WithInvalidGroupId_ReturnsInvalidArgument()
    {
        var dispatcher = await StartAsync();
        var arguments = """
        {
            "conditionTree": {"left":{"price":"Close"},"comparison":"GreaterThan","right":0},
            "groupId": "not-a-guid"
        }
        """;

        using var response = await CallAsync(dispatcher, arguments);

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
        var arguments = $$"""
        {
            "conditionTree": {"left":{"price":"Close"},"comparison":"GreaterThan","right":0},
            "groupId": "{{unknownId}}"
        }
        """;

        using var response = await CallAsync(dispatcher, arguments);

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("GROUP_NOT_FOUND", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("groupId", error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task WithValidStaticGroupId_ScreensGroupMembersOnly()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Tech", IndicatorColor.FromRgb(0, 0, 255), false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("NVDA", DateTimeOffset.UtcNow)
                })
            }
        };
        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(workspace, _workspacePath);

        var dispatcher = await StartAsync();
        var arguments = $$"""
        {
            "conditionTree": {"left":{"price":"Close"},"comparison":"GreaterThan","right":0},
            "groupId": "{{watchlistId}}"
        }
        """;

        using var response = await CallAsync(dispatcher, arguments);

        Assert.False(McpTestSupport.IsError(response));
        JsonElement root = McpTestSupport.Structured(response);

        Assert.Equal("Custom", root.GetProperty("universe").GetString());
        Assert.Equal(watchlistId.ToString("D").ToLowerInvariant(), root.GetProperty("groupId").GetString());
        Assert.Equal(2, root.GetProperty("evaluatedSymbolCount").GetInt32());
    }

    [Fact]
    public async Task WithGroupSymbolsExceedingMaxScreenerSymbols_ReturnsLimitExceeded()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Large Group", IndicatorColor.FromRgb(0, 0, 255), false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
                    new WatchlistItem("NVDA", DateTimeOffset.UtcNow)
                })
            }
        };
        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(workspace, _workspacePath);

        // Limit MaxScreenerSymbols to 2, group has 3
        var dispatcher = await StartAsync(("MaxScreenerSymbols", "2"));
        var arguments = $$"""
        {
            "conditionTree": {"left":{"price":"Close"},"comparison":"GreaterThan","right":0},
            "groupId": "{{watchlistId}}"
        }
        """;

        using var response = await CallAsync(dispatcher, arguments);

        Assert.True(McpTestSupport.IsError(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("groupId", error.GetProperty("param").GetString());
    }
}
