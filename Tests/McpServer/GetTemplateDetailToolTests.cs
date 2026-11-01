using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Tools;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class GetTemplateDetailToolTests
{
    private sealed class TemplateTestGateway : EmptyReadOnlyStockAnalyzerGateway
    {
        public Dictionary<Guid, ScreenerIndicatorTemplate> ScreenerTemplates { get; } = new();
        public Dictionary<Guid, BacktestConditionTemplate> BacktestTemplates { get; } = new();

        public override Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(ScreenerTemplates.TryGetValue(id, out var template) ? template : null);

        public override Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(BacktestTemplates.TryGetValue(id, out var template) ? template : null);
    }

    private static IStockAnalyzerSettings Settings()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        return new ServiceCollection()
            .AddSingleton(configuration)
            .AddStockAnalyzerSettingsOptions(configuration)
            .AddSingleton<IStockAnalyzerSettings, StockAnalyzerSettings>()
            .BuildServiceProvider()
            .GetRequiredService<IStockAnalyzerSettings>();
    }

    private static (GetTemplateDetailTool Tool, TemplateTestGateway Gateway, ScreenerConditionBinder ScreenerBinder, BacktestConditionBinder BacktestBinder) CreateEnvironment()
    {
        var gateway = new TemplateTestGateway();
        var catalogProvider = new ScreenerCatalogProvider();
        IIndicatorFactory factory = IndicatorFactory.Default;
        var catalog = new IndicatorCatalog(factory, catalogProvider);
        IStockAnalyzerSettings settings = Settings();
        var screenerCatalog = new ScreenerCatalog(catalogProvider, factory);
        var indicatorBinder = new IndicatorSettingsBinder(catalogProvider, factory);
        var screenerBinder = new ScreenerConditionBinder(catalog, indicatorBinder, screenerCatalog, settings);
        var backtestBinder = new BacktestConditionBinder(catalog, indicatorBinder, settings);
        var writer = new TemplateConditionWriter(catalog, screenerCatalog);
        var tool = new GetTemplateDetailTool(gateway, writer);
        return (tool, gateway, screenerBinder, backtestBinder);
    }

    [Fact]
    public async Task RoundTrip_ScreenerTemplate_BoundConditionMatchesOriginal()
    {
        // Proof obligation (c): round-trip consistency for Screener template
        var (tool, gateway, screenerBinder, _) = CreateEnvironment();
        var templateId = Guid.NewGuid();

        var template = new ScreenerIndicatorTemplate
        {
            Id = templateId,
            Name = "MultiConditionStrategy",
            Description = "Test screener template description",
            IsFavorite = true,
        };

        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig
                {
                    IndicatorType = IndicatorType.SMA,
                    Parameters = new Dictionary<string, object> { ["Period"] = 10 },
                    Offset = 1,
                    TimeFrame = TimeFrame.D1,
                },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.Indicator,
                RightHand = new ScreenerIndicatorSideConfig
                {
                    IndicatorType = IndicatorType.EMA,
                    Parameters = new Dictionary<string, object> { ["Period"] = 20 },
                    Offset = 0,
                    TimeFrame = TimeFrame.D1,
                },
                LogicalOperator = LogicalOperator.And,
                IsEnabled = true,
            },
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig
                {
                    IndicatorType = IndicatorType.RSI,
                    Parameters = new Dictionary<string, object> { ["Period"] = 14 },
                    TimeFrame = TimeFrame.D1,
                },
                Operator = ComparisonOperator.LessThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 30m,
                LogicalOperator = LogicalOperator.Or,
                IsEnabled = true,
            },
        });

        gateway.ScreenerTemplates[templateId] = template;

        string args = JsonSerializer.Serialize(new
        {
            templateType = "screener",
            templateId = templateId.ToString(),
        });

        string responseJson = await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        Assert.Equal(templateId.ToString(), root.GetProperty("id").GetString());
        Assert.Equal("MultiConditionStrategy", root.GetProperty("name").GetString());
        Assert.Equal("Test screener template description", root.GetProperty("description").GetString());
        Assert.True(root.GetProperty("isFavorite").GetBoolean());
        Assert.True(root.TryGetProperty("conditionTree", out JsonElement conditionTree));

        // Bind the written MCP conditionTree using ScreenerConditionBinder
        BoundScreenerCondition bound = screenerBinder.Bind(conditionTree, "conditionTree");

        // Verify round-trip equality of leaves
        Assert.Equal(2, bound.Leaves.Count);

        // Leaf 0
        Assert.Equal(IndicatorType.SMA, bound.Leaves[0].Entry.LeftHand.IndicatorType);
        Assert.Equal(10, Convert.ToInt32(bound.Leaves[0].Entry.LeftHand.Parameters["Period"]));
        Assert.Equal(1, bound.Leaves[0].Entry.LeftHand.Offset);
        Assert.Equal(ComparisonOperator.GreaterThan, bound.Leaves[0].Entry.Operator);
        Assert.Equal(RightHandTargetMode.Indicator, bound.Leaves[0].Entry.TargetMode);
        Assert.Equal(IndicatorType.EMA, bound.Leaves[0].Entry.RightHand.IndicatorType);
        Assert.Equal(20, Convert.ToInt32(bound.Leaves[0].Entry.RightHand.Parameters["Period"]));

        // Leaf 1
        Assert.Equal(IndicatorType.RSI, bound.Leaves[1].Entry.LeftHand.IndicatorType);
        Assert.Equal(14, Convert.ToInt32(bound.Leaves[1].Entry.LeftHand.Parameters["Period"]));
        Assert.Equal(ComparisonOperator.LessThan, bound.Leaves[1].Entry.Operator);
        Assert.Equal(RightHandTargetMode.NumericValue, bound.Leaves[1].Entry.TargetMode);
        Assert.Equal(30m, bound.Leaves[1].Entry.RightNumericValue);
    }

    [Fact]
    public async Task RoundTrip_BacktestTemplate_BoundConditionMatchesOriginal()
    {
        // Proof obligation (c): round-trip consistency for Backtest template
        var (tool, gateway, _, backtestBinder) = CreateEnvironment();
        var templateId = Guid.NewGuid();

        var template = new BacktestConditionTemplate
        {
            Id = templateId,
            Name = "BacktestTrendStrategy",
            Description = "Trend following template",
            IsFavorite = false,
            RiskManagement = new BacktestRiskManagementSettingsDto
            {
                StopLossPercent = 0.05m,
                TakeProfitPercent = 0.15m,
            },
            ConditionTree = new BacktestConditionTreeDto
            {
                EntryLong = new BacktestConditionNodeDto
                {
                    Kind = BacktestConditionNodeKind.Group,
                    Operator = LogicalOperator.And,
                    Children = new List<BacktestConditionNodeDto>
                    {
                        new()
                        {
                            Kind = BacktestConditionNodeKind.Leaf,
                            Comparison = new BacktestConditionEntryDto
                            {
                                Left = new BacktestConditionSideDto
                                {
                                    IndicatorType = IndicatorType.Price,
                                    PriceSource = PriceType.Close,
                                },
                                Operator = ComparisonOperator.GreaterThan,
                                TargetMode = RightHandTargetMode.Indicator,
                                Right = new BacktestConditionSideDto
                                {
                                    IndicatorType = IndicatorType.SMA,
                                    Parameters = new CoreSmaParameter { Period = 20 },
                                },
                            },
                        },
                    },
                },
                ExitLong = new BacktestConditionNodeDto
                {
                    Kind = BacktestConditionNodeKind.Group,
                    Operator = LogicalOperator.And,
                    Children = new List<BacktestConditionNodeDto>
                    {
                        new()
                        {
                            Kind = BacktestConditionNodeKind.Leaf,
                            Comparison = new BacktestConditionEntryDto
                            {
                                Left = new BacktestConditionSideDto
                                {
                                    IndicatorType = IndicatorType.Price,
                                    PriceSource = PriceType.Close,
                                },
                                Operator = ComparisonOperator.LessThan,
                                TargetMode = RightHandTargetMode.NumericValue,
                                RightNumericValue = 100m,
                            },
                        },
                    },
                },
            },
        };

        gateway.BacktestTemplates[templateId] = template;

        string args = JsonSerializer.Serialize(new
        {
            templateType = "backtestCondition",
            templateId = templateId.ToString(),
        });

        string responseJson = await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        Assert.Equal(templateId.ToString(), root.GetProperty("id").GetString());
        Assert.Equal("BacktestTrendStrategy", root.GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("conditions", out JsonElement conditions));
        Assert.True(root.TryGetProperty("riskManagement", out JsonElement riskManagement));

        // Verify riskManagement
        Assert.Equal(0.05m, riskManagement.GetProperty("stopLossRatio").GetDecimal());
        Assert.Equal(0.15m, riskManagement.GetProperty("takeProfitRatio").GetDecimal());

        // Bind the written MCP conditions using BacktestConditionBinder
        BoundBacktestConditions bound = backtestBinder.Bind(conditions, "conditions");

        // Verify roots
        Assert.True(bound.Tree.EntryLong.HasLeaf);
        Assert.True(bound.Tree.ExitLong.HasLeaf);
        Assert.False(bound.Tree.EntryShort.HasLeaf);

        // EntryLong leaf check
        var entryLongLeaf = (BacktestConditionLeaf)bound.Tree.EntryLong.Children[0];
        Assert.Equal(IndicatorType.Price, entryLongLeaf.Comparison.Left.IndicatorType);
        Assert.Equal(PriceType.Close, entryLongLeaf.Comparison.Left.PriceSource);
        Assert.Equal(ComparisonOperator.GreaterThan, entryLongLeaf.Comparison.Operator);
        Assert.Equal(IndicatorType.SMA, entryLongLeaf.Comparison.Right!.IndicatorType);

        // ExitLong leaf check
        var exitLongLeaf = (BacktestConditionLeaf)bound.Tree.ExitLong.Children[0];
        Assert.Equal(PriceType.Close, exitLongLeaf.Comparison.Left.PriceSource);
        Assert.Equal(ComparisonOperator.LessThan, exitLongLeaf.Comparison.Operator);
        Assert.Equal(100m, exitLongLeaf.Comparison.RightNumericValue);
    }

    [Fact]
    public async Task ScreenerTemplate_WithDisabledEntries_ReportsUnconvertibleEntries()
    {
        var (tool, gateway, _, _) = CreateEnvironment();
        var templateId = Guid.NewGuid();

        var template = new ScreenerIndicatorTemplate
        {
            Id = templateId,
            Name = "DisabledEntryTest",
        };

        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close" },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 10m,
                IsEnabled = false, // Disabled!
            },
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close" },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 20m,
                IsEnabled = true, // Enabled
            },
        });

        gateway.ScreenerTemplates[templateId] = template;

        string args = JsonSerializer.Serialize(new
        {
            templateType = "screener",
            templateId = templateId.ToString(),
        });

        string responseJson = await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("unconvertibleEntries", out JsonElement unconvertible));
        Assert.Equal(1, unconvertible.GetArrayLength());
        Assert.Equal(0, unconvertible[0].GetProperty("index").GetInt32());
        Assert.Contains("disabled", unconvertible[0].GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("screener")]
    [InlineData("backtestCondition")]
    public async Task InvalidGuid_ThrowsInvalidArgument(string templateType)
    {
        var (tool, _, _, _) = CreateEnvironment();
        string args = JsonSerializer.Serialize(new
        {
            templateType = templateType,
            templateId = "not-a-guid",
        });

        ToolException ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None));

        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal("templateId", ex.Parameter);
    }

    [Theory]
    [InlineData("screener")]
    [InlineData("backtestCondition")]
    public async Task TemplateNotFound_ThrowsTemplateNotFound(string templateType)
    {
        var (tool, _, _, _) = CreateEnvironment();
        string args = JsonSerializer.Serialize(new
        {
            templateType = templateType,
            templateId = Guid.NewGuid().ToString(),
        });

        ToolException ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None));

        Assert.Equal(ToolErrorCodes.TemplateNotFound, ex.Code);
        Assert.Equal("templateId", ex.Parameter);
    }

    [Fact]
    public async Task RoundTrip_ScreenerTemplate_WithPriceSideOffsetAndTimeFrame_Preserved()
    {
        var (tool, gateway, screenerBinder, _) = CreateEnvironment();
        var templateId = Guid.NewGuid();

        var template = new ScreenerIndicatorTemplate
        {
            Id = templateId,
            Name = "PriceOffsetTemplate",
            IsFavorite = false,
        };

        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig
                {
                    IndicatorType = IndicatorType.Price,
                    OutputName = "Close",
                    Offset = 2,
                    TimeFrame = TimeFrame.W1,
                },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 100m,
                IsEnabled = true,
            },
        });

        gateway.ScreenerTemplates[templateId] = template;

        string args = JsonSerializer.Serialize(new
        {
            templateType = "screener",
            templateId = templateId.ToString(),
        });

        string responseJson = await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("conditionTree", out JsonElement conditionTree));

        BoundScreenerCondition bound = screenerBinder.Bind(conditionTree, "conditionTree");
        Assert.Equal(1, bound.Leaves.Count);
        Assert.Equal(IndicatorType.Price, bound.Leaves[0].Entry.LeftHand.IndicatorType);
        Assert.Equal(2, bound.Leaves[0].Entry.LeftHand.Offset);
        Assert.Equal(TimeFrame.W1, bound.Leaves[0].Entry.LeftHand.TimeFrame);
    }
}
