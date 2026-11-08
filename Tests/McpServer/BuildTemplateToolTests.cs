using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Tools;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class BuildTemplateToolTests
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

    private static (BuildTemplateTool Tool, TemplateTestGateway Gateway, ScreenerConditionBinder ScreenerBinder, BacktestConditionBinder BacktestBinder) CreateEnvironment()
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
        var templateAdvisor = new ScreenerTemplateAdvisor(catalog);
        var writer = new TemplateConditionWriter(catalog, screenerCatalog);
        var builder = new TemplateStructureBuilder(gateway, screenerBinder, backtestBinder, templateAdvisor, writer, settings);
        var tool = new BuildTemplateTool(builder);
        return (tool, gateway, screenerBinder, backtestBinder);
    }

    [Fact]
    public async Task Build_NewScreenerTemplate_ValidTree_ReturnsPersistedJsonAndRoundTrips()
    {
        var (tool, _, _, _) = CreateEnvironment();
        string args = """
        {
            "templateType": "screener",
            "name": "RSI Oversold Strategy",
            "description": "Screens for RSI below 30",
            "conditionTree": {
                "left": { "indicator": "RSI", "parameters": { "Period": 14 } },
                "comparison": "LessThan",
                "right": 30
            }
        }
        """;

        using var doc = JsonDocument.Parse(await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None));
        JsonElement root = doc.RootElement;

        Assert.Equal("screener", root.GetProperty("templateType").GetString());
        Assert.Equal("RSI Oversold Strategy", root.GetProperty("name").GetString());
        Assert.Equal("Screens for RSI below 30", root.GetProperty("description").GetString());
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("appendAnd", root.GetProperty("mergeMode").GetString());
        Assert.Equal(1, root.GetProperty("nodeCount").GetInt32());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());

        string templateJson = root.GetProperty("templateJson").GetString()!;
        Assert.NotNull(templateJson);

        // Verify round-trip deserialization via FileReadOnlyTemplateSource serializer options
        var deserialized = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(templateJson, FileReadOnlyTemplateSource.CreateJsonOptions());
        Assert.NotNull(deserialized);
        Assert.Equal("RSI Oversold Strategy", deserialized.Name);
        Assert.Equal(ScreenerIndicatorTemplate.CurrentSchemaVersion, deserialized.SchemaVersion);
        Assert.False(deserialized.IsDiscardedLegacy);
        Assert.Single(deserialized.Entries);
        Assert.Equal("A", deserialized.Entries[0].Label);
        Assert.Equal(LogicalOperator.And, deserialized.Entries[0].LogicalOperator);
        Assert.True(deserialized.Entries[0].IsEnabled);
        Assert.Equal(ComparisonOperator.LessThan, deserialized.Entries[0].Operator);
        Assert.Equal(30m, deserialized.Entries[0].RightNumericValue);
    }

    [Fact]
    public async Task Build_ScreenerTemplate_WithLeftAssociativeNestedTree_FlattensCorrectly()
    {
        var (tool, _, _, _) = CreateEnvironment();
        // (Close > 100 OR Open < 50) AND Volume > 1000
        string args = """
        {
            "templateType": "screener",
            "name": "Nested Strategy",
            "conditionTree": {
                "operator": "And",
                "children": [
                    {
                        "operator": "Or",
                        "children": [
                            { "left": { "price": "Close" }, "comparison": "GreaterThan", "right": 100 },
                            { "left": { "price": "Open" }, "comparison": "LessThan", "right": 50 }
                        ]
                    },
                    { "left": { "price": "High" }, "comparison": "GreaterThan", "right": 1000 }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None));
        string templateJson = doc.RootElement.GetProperty("templateJson").GetString()!;

        var deserialized = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(templateJson, FileReadOnlyTemplateSource.CreateJsonOptions());
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Entries.Count);

        Assert.Equal("A", deserialized.Entries[0].Label);
        Assert.Equal(LogicalOperator.Or, deserialized.Entries[0].LogicalOperator);

        Assert.Equal("B", deserialized.Entries[1].Label);
        Assert.Equal(LogicalOperator.And, deserialized.Entries[1].LogicalOperator);

        Assert.Equal("C", deserialized.Entries[2].Label);
        Assert.Equal(LogicalOperator.And, deserialized.Entries[2].LogicalOperator);
    }

    [Fact]
    public async Task Build_ScreenerTemplate_WithRightHandNestedGroupDifferentOperator_ThrowsInvalidArgument()
    {
        var (tool, _, _, _) = CreateEnvironment();
        // Close > 100 AND (Open < 50 OR Volume > 1000)
        string args = """
        {
            "templateType": "screener",
            "name": "Invalid Precedence Strategy",
            "conditionTree": {
                "operator": "And",
                "children": [
                    { "left": { "price": "Close" }, "comparison": "GreaterThan", "right": 100 },
                    {
                        "operator": "Or",
                        "children": [
                            { "left": { "price": "Open" }, "comparison": "LessThan", "right": 50 },
                            { "left": { "price": "High" }, "comparison": "GreaterThan", "right": 1000 }
                        ]
                    }
                ]
            }
        }
        """;

        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal("conditionTree", ex.Parameter);
        Assert.Contains("operator precedence", ex.Message);
    }

    [Fact]
    public async Task Build_ScreenerTemplate_MergeWithBaseTemplate_SupportsAppendAndAppendOrAndReplace()
    {
        var (tool, gateway, _, _) = CreateEnvironment();
        var baseId = Guid.NewGuid();
        var baseTemplate = new ScreenerIndicatorTemplate
        {
            Id = baseId,
            Name = "Base Screener",
            Description = "Base description",
            IsFavorite = true,
        };
        baseTemplate.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                Label = "A",
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close" },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 100m,
                LogicalOperator = LogicalOperator.And,
                IsEnabled = true,
            },
            new ScreenerIndicatorEntry
            {
                Label = "B",
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Volume" },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = 500m,
                LogicalOperator = LogicalOperator.And,
                IsEnabled = false, // disabled entry
            }
        });
        gateway.ScreenerTemplates[baseId] = baseTemplate;

        // 1. Test appendAnd
        string appendAndArgs = $$"""
        {
            "templateType": "screener",
            "baseTemplateId": "{{baseId}}",
            "mergeMode": "appendAnd",
            "conditionTree": {
                "left": { "indicator": "RSI", "parameters": { "Period": 14 } },
                "comparison": "LessThan",
                "right": 30
            }
        }
        """;

        using (var doc = JsonDocument.Parse(await tool.InvokeAsync(JsonDocument.Parse(appendAndArgs).RootElement, CancellationToken.None)))
        {
            var res = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(doc.RootElement.GetProperty("templateJson").GetString()!, FileReadOnlyTemplateSource.CreateJsonOptions())!;
            Assert.Equal(3, res.Entries.Count);
            Assert.Equal("A", res.Entries[0].Label);
            Assert.Equal(LogicalOperator.And, res.Entries[0].LogicalOperator); // connected to next via And
            Assert.False(res.Entries[1].IsEnabled);
            Assert.Equal("B", res.Entries[1].Label);
            Assert.Equal("C", res.Entries[2].Label);
            Assert.Equal(baseId, res.Id);
            Assert.Equal("Base Screener", res.Name);
            Assert.Equal("Base description", res.Description);
        }

        // 2. Test appendOr
        string appendOrArgs = $$"""
        {
            "templateType": "screener",
            "baseTemplateId": "{{baseId}}",
            "mergeMode": "appendOr",
            "conditionTree": {
                "left": { "indicator": "RSI", "parameters": { "Period": 14 } },
                "comparison": "LessThan",
                "right": 30
            }
        }
        """;

        using (var doc = JsonDocument.Parse(await tool.InvokeAsync(JsonDocument.Parse(appendOrArgs).RootElement, CancellationToken.None)))
        {
            var res = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(doc.RootElement.GetProperty("templateJson").GetString()!, FileReadOnlyTemplateSource.CreateJsonOptions())!;
            Assert.Equal(3, res.Entries.Count);
            Assert.Equal(LogicalOperator.Or, res.Entries[0].LogicalOperator); // last enabled base entry connected with Or
            Assert.Equal("C", res.Entries[2].Label);
        }

        // 3. Test replace
        string replaceArgs = $$"""
        {
            "templateType": "screener",
            "baseTemplateId": "{{baseId}}",
            "mergeMode": "replace",
            "name": "Replaced Screener",
            "conditionTree": {
                "left": { "indicator": "SMA", "parameters": { "Period": 20 } },
                "comparison": "GreaterThan",
                "right": 50
            }
        }
        """;

        using (var doc = JsonDocument.Parse(await tool.InvokeAsync(JsonDocument.Parse(replaceArgs).RootElement, CancellationToken.None)))
        {
            var res = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(doc.RootElement.GetProperty("templateJson").GetString()!, FileReadOnlyTemplateSource.CreateJsonOptions())!;
            Assert.Single(res.Entries);
            Assert.Equal("A", res.Entries[0].Label);
            Assert.Equal("Replaced Screener", res.Name);
        }
    }

    [Fact]
    public async Task Build_NewBacktestTemplate_ValidConditionsAndRisk_ReturnsPersistedJsonAndRoundTrips()
    {
        var (tool, _, _, _) = CreateEnvironment();
        string args = """
        {
            "templateType": "backtestCondition",
            "name": "Breakout Strategy",
            "description": "Long on breakout with 5% stop loss",
            "conditions": {
                "entryLong": {
                    "left": { "indicator": "RSI", "parameters": { "Period": 14 } },
                    "comparison": "LessThan",
                    "right": 30
                },
                "exitLong": {
                    "left": { "indicator": "RSI", "parameters": { "Period": 14 } },
                    "comparison": "GreaterThan",
                    "right": 70
                }
            },
            "riskManagement": {
                "stopLossRatio": 0.05,
                "takeProfitRatio": 0.15
            }
        }
        """;

        using var doc = JsonDocument.Parse(await tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None));
        JsonElement root = doc.RootElement;

        Assert.Equal("backtestCondition", root.GetProperty("templateType").GetString());
        Assert.Equal("Breakout Strategy", root.GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("conditions", out _));
        Assert.True(root.TryGetProperty("riskManagement", out _));

        string templateJson = root.GetProperty("templateJson").GetString()!;
        var deserialized = JsonSerializer.Deserialize<BacktestConditionTemplate>(templateJson, FileReadOnlyTemplateSource.CreateJsonOptions());
        Assert.NotNull(deserialized);
        Assert.Equal("Breakout Strategy", deserialized.Name);
        Assert.Equal(TemplateBase.DefaultSchemaVersion, deserialized.SchemaVersion);
        Assert.NotNull(deserialized.ConditionTree.EntryLong);
        Assert.NotNull(deserialized.ConditionTree.ExitLong);
        Assert.NotNull(deserialized.RiskManagement);
        Assert.Equal(0.05m, deserialized.RiskManagement.StopLossPercent);
        Assert.Equal(0.15m, deserialized.RiskManagement.TakeProfitPercent);
    }

    [Fact]
    public async Task Build_BacktestTemplate_MergeWithBaseTemplate_MergesRootsAndPreservesUnmodifiedRoots()
    {
        var (tool, gateway, _, _) = CreateEnvironment();
        var baseId = Guid.NewGuid();
        var baseTemplate = new BacktestConditionTemplate
        {
            Id = baseId,
            Name = "Base Backtest",
            ConditionTree = new BacktestConditionTreeDto
            {
                EntryLong = new BacktestConditionNodeDto
                {
                    Kind = BacktestConditionNodeKind.Group,
                    Operator = LogicalOperator.And,
                    Children = new List<BacktestConditionNodeDto>
                    {
                        new BacktestConditionNodeDto
                        {
                            Kind = BacktestConditionNodeKind.Leaf,
                            Comparison = new BacktestConditionEntryDto
                            {
                                Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, OutputName = "Close" },
                                Operator = ComparisonOperator.GreaterThan,
                                TargetMode = RightHandTargetMode.NumericValue,
                                RightNumericValue = 100m,
                            }
                        }
                    }
                },
                ExitLong = new BacktestConditionNodeDto
                {
                    Kind = BacktestConditionNodeKind.Group,
                    Operator = LogicalOperator.And,
                    Children = new List<BacktestConditionNodeDto>
                    {
                        new BacktestConditionNodeDto
                        {
                            Kind = BacktestConditionNodeKind.Leaf,
                            Comparison = new BacktestConditionEntryDto
                            {
                                Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, OutputName = "Close" },
                                Operator = ComparisonOperator.LessThan,
                                TargetMode = RightHandTargetMode.NumericValue,
                                RightNumericValue = 90m,
                            }
                        }
                    }
                }
            },
            RiskManagement = new BacktestRiskManagementSettingsDto
            {
                StopLossPercent = 0.05m,
            }
        };
        gateway.BacktestTemplates[baseId] = baseTemplate;

        // Merge new entryLong with appendAnd and add entryShort; exitLong should be preserved
        string mergeArgs = $$"""
        {
            "templateType": "backtestCondition",
            "baseTemplateId": "{{baseId}}",
            "mergeMode": "appendAnd",
            "conditions": {
                "entryLong": {
                    "left": { "indicator": "RSI", "parameters": { "Period": 14 } },
                    "comparison": "LessThan",
                    "right": 30
                },
                "entryShort": {
                    "left": { "indicator": "RSI", "parameters": { "Period": 14 } },
                    "comparison": "GreaterThan",
                    "right": 70
                }
            }
        }
        """;

        using var doc = JsonDocument.Parse(await tool.InvokeAsync(JsonDocument.Parse(mergeArgs).RootElement, CancellationToken.None));
        string templateJson = doc.RootElement.GetProperty("templateJson").GetString()!;
        var res = JsonSerializer.Deserialize<BacktestConditionTemplate>(templateJson, FileReadOnlyTemplateSource.CreateJsonOptions())!;

        Assert.Equal(baseId, res.Id);
        Assert.Equal("Base Backtest", res.Name);
        // EntryLong should have 2 children merged under AND
        Assert.Equal(2, res.ConditionTree.EntryLong!.Children.Count);
        // ExitLong should be preserved from baseTemplate
        Assert.NotNull(res.ConditionTree.ExitLong);
        Assert.Single(res.ConditionTree.ExitLong.Children);
        // EntryShort was added
        Assert.NotNull(res.ConditionTree.EntryShort);
        Assert.Single(res.ConditionTree.EntryShort.Children);
        // RiskManagement preserved
        Assert.NotNull(res.RiskManagement);
        Assert.Equal(0.05m, res.RiskManagement.StopLossPercent);
    }

    [Fact]
    public async Task Build_BacktestTemplate_WithDynamicExits_ThrowsInvalidArgument()
    {
        var (tool, _, _, _) = CreateEnvironment();
        string args = """
        {
            "templateType": "backtestCondition",
            "name": "Dynamic Exit Strategy",
            "conditions": {
                "entryLong": {
                    "left": { "price": "Close" },
                    "comparison": "GreaterThan",
                    "right": 100
                }
            },
            "riskManagement": {
                "stopLossRatio": 0.05,
                "dynamicExits": {
                    "maxHoldingBars": 10
                }
            }
        }
        """;

        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(JsonDocument.Parse(args).RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal("riskManagement.dynamicExits", ex.Parameter);
    }

    [Fact]
    public async Task Build_ErrorCases_ValidationFailsAsExpected()
    {
        var (tool, gateway, _, _) = CreateEnvironment();

        // 1. Missing name when creating new template
        string noName = """{ "templateType": "screener", "conditionTree": { "left": { "price": "Close" }, "comparison": "GreaterThan", "right": 100 } }""";
        var ex1 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(JsonDocument.Parse(noName).RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex1.Code);
        Assert.Equal("name", ex1.Parameter);

        // 2. Invalid templateType
        string badType = """{ "templateType": "unknown", "name": "Test" }""";
        var ex2 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(JsonDocument.Parse(badType).RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex2.Code);
        Assert.Equal("templateType", ex2.Parameter);

        // 3. Base template not found
        string notFound = $$"""{ "templateType": "screener", "baseTemplateId": "{{Guid.NewGuid()}}", "conditionTree": { "left": { "price": "Close" }, "comparison": "GreaterThan", "right": 100 } }""";
        var ex3 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(JsonDocument.Parse(notFound).RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.TemplateNotFound, ex3.Code);
        Assert.Equal("baseTemplateId", ex3.Parameter);

        // 4. Mismatched condition arguments
        string screenerWithConditions = """{ "templateType": "screener", "name": "Bad", "conditions": {} }""";
        var ex4 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(JsonDocument.Parse(screenerWithConditions).RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex4.Code);
        Assert.Equal("conditions", ex4.Parameter);

        string backtestWithTree = """{ "templateType": "backtestCondition", "name": "Bad", "conditionTree": {} }""";
        var ex5 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(JsonDocument.Parse(backtestWithTree).RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex5.Code);
        Assert.Equal("conditionTree", ex5.Parameter);
    }
}
