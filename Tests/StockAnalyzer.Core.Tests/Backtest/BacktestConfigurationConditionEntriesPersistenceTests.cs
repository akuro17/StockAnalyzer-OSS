using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Task 4 persistence proof (plan section 3.3/Task 4 of
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md): a pre-existing saved
/// backtest_configuration.json with no "conditionEntries" property still loads unchanged (the safe-
/// extension guarantee), and a file that DOES carry condition entries round-trips every field exactly.
/// Uses a temp file + <see cref="AtomicJsonFile"/> directly (mirroring the exact
/// <see cref="JsonSerializerOptions"/> shape <see cref="BacktestConfigurationManager"/> itself uses) so
/// this never touches the real, fixed backtest_configuration.json path under the user's Data/Config
/// folder, per that class's own doc-comment warning.
/// </summary>
public class BacktestConfigurationConditionEntriesPersistenceTests : IDisposable
{
    private readonly string _tempFilePath = Path.Combine(Path.GetTempPath(), $"backtest_configuration_test_{Guid.NewGuid():N}.json");

    private static JsonSerializerOptions MakeOptions() => new()
    {
        WriteIndented = true,
        TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Dispose()
    {
        if (File.Exists(_tempFilePath)) File.Delete(_tempFilePath);
    }

    [Fact]
    public async Task OldFileWithNoConditionEntriesProperty_StillLoads_WithEmptyConditionEntries()
    {
        // Hand-written JSON in the exact pre-Task-4 shape: no "conditionEntries" property at all.
        const string oldFormatJson = """
        {
            "schemaVersion": 1,
            "symbol": "AAPL",
            "frame": 6,
            "evaluationStartUtc": "2020-01-01T00:00:00Z",
            "evaluationEndUtc": "2021-01-01T00:00:00Z",
            "initialCapital": 1000000,
            "commissionFlat": 0,
            "commissionPerUnit": 0,
            "slippageRatio": 0,
            "sizingModel": 0,
            "sizingParameter": 1,
            "initialMarginRatio": 0.3,
            "maintenanceMarginRatio": 0.2,
            "liquidationPenaltyRatio": 0,
            "bootstrapSeed": 42,
            "bootstrapIterations": 2000,
            "selectedIndicators": []
        }
        """;
        await File.WriteAllTextAsync(_tempFilePath, oldFormatJson);

        BacktestConfigurationDto? dto = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        Assert.NotNull(dto);
        Assert.Equal("AAPL", dto!.Symbol);
        Assert.NotNull(dto.ConditionEntries);
        Assert.Empty(dto.ConditionEntries);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public async Task ConditionEntries_RoundTripThroughRealFile_EveryFieldMatches()
    {
        var original = new BacktestConfigurationDto
        {
            SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
            Symbol = "MSFT",
            Frame = TimeFrame.D1,
            EvaluationStartUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EvaluationEndUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            InitialCapital = 1_000_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
            InitialMarginRatio = 0.30m,
            MaintenanceMarginRatio = 0.20m,
            LiquidationPenaltyRatio = 0.005m,
            ConditionEntries = new System.Collections.Generic.List<BacktestConditionEntryDto>
            {
                new()
                {
                    Left = new BacktestConditionSideDto
                    {
                        IndicatorType = IndicatorType.SMA,
                        Parameters = new CoreZigZagParameter { Threshold = 3.5m },
                        Offset = 1,
                        Frame = TimeFrame.W1,
                    },
                    Operator = ComparisonOperator.LessThanOrEqual,
                    TargetMode = RightHandTargetMode.Indicator,
                    Right = new BacktestConditionSideDto { IndicatorType = IndicatorType.RSI, Offset = 0, Frame = null },
                    LogicalOperator = LogicalOperator.Or,
                    Role = BacktestConditionRole.ExitOnly,
                },
                new()
                {
                    Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.RSI },
                    Operator = ComparisonOperator.GreaterThan,
                    TargetMode = RightHandTargetMode.NumericValue,
                    RightNumericValue = 70m,
                    Role = BacktestConditionRole.EntryOnly,
                },
            },
        };

        await AtomicJsonFile.SaveAsync(_tempFilePath, original, MakeOptions());
        BacktestConfigurationDto? restored = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.ConditionEntries.Count);

        BacktestConditionEntryDto entry0 = restored.ConditionEntries[0];
        Assert.Equal(IndicatorType.SMA, entry0.Left.IndicatorType);
        Assert.IsType<CoreZigZagParameter>(entry0.Left.Parameters);
        Assert.Equal(3.5m, ((CoreZigZagParameter)entry0.Left.Parameters!).Threshold);
        Assert.Equal(1, entry0.Left.Offset);
        Assert.Equal(TimeFrame.W1, entry0.Left.Frame);
        Assert.Equal(ComparisonOperator.LessThanOrEqual, entry0.Operator);
        Assert.Equal(RightHandTargetMode.Indicator, entry0.TargetMode);
        Assert.NotNull(entry0.Right);
        Assert.Equal(IndicatorType.RSI, entry0.Right!.IndicatorType);
        Assert.Null(entry0.Right.Frame);
        Assert.Equal(LogicalOperator.Or, entry0.LogicalOperator);
        Assert.Equal(BacktestConditionRole.ExitOnly, entry0.Role);

        BacktestConditionEntryDto entry1 = restored.ConditionEntries[1];
        Assert.Equal(IndicatorType.RSI, entry1.Left.IndicatorType);
        Assert.Null(entry1.Left.Parameters);
        Assert.Equal(70m, entry1.RightNumericValue);
        Assert.Equal(BacktestConditionRole.EntryOnly, entry1.Role);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(restored);
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public async Task OldConditionEntryWithNoPriceSourceProperty_StillLoads_WithNullPriceSource()
    {
        // SAで改善 (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): a pre-existing saved
        // condition entry with no "priceSource" property on its Left/Right side (the exact pre-fix shape)
        // must still load unchanged - the safe-extension guarantee. Built by serializing a real DTO and
        // then stripping the "priceSource" property textually, rather than hand-writing a full JSON
        // document, so this never depends on guessing IndicatorType's underlying numeric value.
        var original = new BacktestConfigurationDto
        {
            SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
            Symbol = "AAPL",
            Frame = TimeFrame.D1,
            EvaluationStartUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EvaluationEndUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            InitialCapital = 1_000_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
            InitialMarginRatio = 0.30m,
            MaintenanceMarginRatio = 0.20m,
            LiquidationPenaltyRatio = 0.005m,
            ConditionEntries = new System.Collections.Generic.List<BacktestConditionEntryDto>
            {
                new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, PriceSource = PriceType.High } },
            },
        };
        // Parsed into a JsonNode tree and the property removed structurally (rather than via regex text
        // surgery) so this never has to reason about WriteIndented whitespace/trailing-comma placement.
        string json = System.Text.Json.JsonSerializer.Serialize(original, MakeOptions());
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        System.Text.Json.Nodes.JsonObject leftSide = root["ConditionEntries"]![0]!["Left"]!.AsObject();
        Assert.True(leftSide.Remove("PriceSource"));
        string oldFormatJson = root.ToJsonString();
        Assert.DoesNotContain("priceSource", oldFormatJson, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(_tempFilePath, oldFormatJson);

        BacktestConfigurationDto? dto = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        Assert.NotNull(dto);
        BacktestConditionEntryDto entry = Assert.Single(dto!.ConditionEntries);
        Assert.Null(entry.Left.PriceSource);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public async Task PriceSource_RoundTripsThroughRealFile()
    {
        var original = new BacktestConfigurationDto
        {
            SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
            Symbol = "AAPL",
            Frame = TimeFrame.D1,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
            ConditionEntries = new System.Collections.Generic.List<BacktestConditionEntryDto>
            {
                new()
                {
                    Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, PriceSource = PriceType.High },
                    Operator = ComparisonOperator.GreaterThan,
                    TargetMode = RightHandTargetMode.Indicator,
                    Right = new BacktestConditionSideDto { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Low },
                },
            },
        };

        await AtomicJsonFile.SaveAsync(_tempFilePath, original, MakeOptions());
        BacktestConfigurationDto? restored = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        BacktestConditionEntryDto entry = Assert.Single(restored!.ConditionEntries);
        Assert.Equal(PriceType.High, entry.Left.PriceSource);
        Assert.Equal(PriceType.Low, entry.Right!.PriceSource);
    }
}
