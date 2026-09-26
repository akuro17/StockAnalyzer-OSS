using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Task 8b persistence proof (plan section 4.5 of Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md):
/// a pre-existing saved backtest_configuration.json with no "riskManagement" property still loads
/// unchanged (the safe-extension guarantee), and a file that DOES carry risk-management settings
/// round-trips every field exactly. Same temp-file + <see cref="AtomicJsonFile"/> approach as
/// <see cref="BacktestConfigurationConditionEntriesPersistenceTests"/> (Task 4's own equivalent test), for
/// the same reason - never touch the real, fixed backtest_configuration.json path.
/// </summary>
public class BacktestConfigurationRiskManagementPersistenceTests : IDisposable
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
    public async Task OldFileWithNoRiskManagementProperty_StillLoads_WithNullRiskManagement()
    {
        // Hand-written JSON in the exact pre-Task-8b shape: no "riskManagement" property at all
        // (this is also the exact pre-Task-4 shape plus a "conditionEntries": [] property, proving the
        // two safe extensions compose without interfering with each other).
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
            "selectedIndicators": [],
            "conditionEntries": []
        }
        """;
        await File.WriteAllTextAsync(_tempFilePath, oldFormatJson);

        BacktestConfigurationDto? dto = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        Assert.NotNull(dto);
        Assert.Equal("AAPL", dto!.Symbol);
        Assert.Null(dto.RiskManagement);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public async Task RiskManagement_RoundTripThroughRealFile_BothFieldsMatch()
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
            RiskManagement = new BacktestRiskManagementSettingsDto { StopLossPercent = 0.05m, TakeProfitPercent = 0.10m },
        };

        await AtomicJsonFile.SaveAsync(_tempFilePath, original, MakeOptions());
        BacktestConfigurationDto? restored = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        Assert.NotNull(restored);
        Assert.NotNull(restored!.RiskManagement);
        Assert.Equal(0.05m, restored.RiskManagement!.StopLossPercent);
        Assert.Equal(0.10m, restored.RiskManagement.TakeProfitPercent);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(restored);
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public async Task RiskManagement_OnlyOneFieldSet_TheOtherStaysNullThroughRoundTrip()
    {
        var original = new BacktestConfigurationDto
        {
            SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
            Symbol = "MSFT",
            Frame = TimeFrame.D1,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
            RiskManagement = new BacktestRiskManagementSettingsDto { StopLossPercent = 0.05m, TakeProfitPercent = null },
        };

        await AtomicJsonFile.SaveAsync(_tempFilePath, original, MakeOptions());
        BacktestConfigurationDto? restored = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_tempFilePath, MakeOptions());

        Assert.NotNull(restored!.RiskManagement);
        Assert.Equal(0.05m, restored.RiskManagement!.StopLossPercent);
        Assert.Null(restored.RiskManagement.TakeProfitPercent);
    }
}
