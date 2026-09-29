using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Real <see cref="BacktestConfigurationManager"/> save/load of a condition tree (MP-1 Phase 4): version 2 round trip, configured limits, legacy file.</summary>
public sealed class BacktestConfigurationManagerTreeTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"backtest_configuration_tree_{Guid.NewGuid():N}.json");

    private static BacktestConditionNodeDto Leaf(decimal value) => new()
    {
        Kind = BacktestConditionNodeKind.Leaf,
        Comparison = new BacktestConditionEntryDto { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, RightNumericValue = value },
    };

    private static BacktestConditionNodeDto Group(LogicalOperator op, params BacktestConditionNodeDto[] children)
        => new() { Kind = BacktestConditionNodeKind.Group, Operator = op, Children = children.ToList() };

    private static BacktestConfigurationDto Config(int version, BacktestConditionTreeDto? tree) => new()
    {
        SchemaVersion = version,
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
        ConditionTree = tree,
    };

    [Fact]
    public async Task SaveThenLoad_V2Tree_RoundTripsThroughTheRealManager()
    {
        string path = TempPath();
        try
        {
            var manager = new BacktestConfigurationManager(new MockStockAnalyzerSettings(), path);
            var tree = new BacktestConditionTreeDto
            {
                EntryLong = Group(LogicalOperator.Or, Group(LogicalOperator.And, Leaf(1m), Leaf(2m)), Leaf(3m)),
                ExitLong = Group(LogicalOperator.And, Leaf(4m)),
            };

            await manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion, tree));
            BacktestConfigurationLoadResult result = await manager.LoadAsync();

            Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
            BacktestConditionTree runtime = BacktestConditionTreeDtoMapper.ResolveTree(result.Configuration!, 500, 8, 64);
            Assert.Equal(LogicalOperator.Or, runtime.EntryLong.Operator);
            Assert.Equal(2, runtime.EntryLong.Children.Length);
            Assert.IsType<BacktestConditionGroup>(runtime.EntryLong.Children[0]);
            Assert.True(runtime.ExitLong.HasLeaf);
            Assert.False(runtime.ReverseShort.HasLeaf);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_TreeDeeperThanTheConfiguredLimit_IsRejectedAndWritesNothing()
    {
        string path = TempPath();
        try
        {
            var manager = new BacktestConfigurationManager(new MockStockAnalyzerSettings(), path);
            int limit = ((IStockAnalyzerSettings)new MockStockAnalyzerSettings()).BacktestMaxConditionTreeDepth;
            BacktestConditionNodeDto node = Group(LogicalOperator.And, Leaf(1m));
            for (int level = 1; level <= limit; level++) node = Group(LogicalOperator.Or, node);

            await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(Config(BacktestConfigurationDto.ConditionTreeSchemaVersion, new BacktestConditionTreeDto { EntryLong = node })));

            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Load_ExistingVersion1File_StillLoads()
    {
        string path = TempPath();
        try
        {
            var manager = new BacktestConfigurationManager(new MockStockAnalyzerSettings(), path);
            await manager.SaveAsync(Config(BacktestConfigurationDto.CurrentSchemaVersion, tree: null));

            BacktestConfigurationLoadResult result = await manager.LoadAsync();

            Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
            Assert.Equal(BacktestConfigurationDto.CurrentSchemaVersion, result.Configuration!.SchemaVersion);
            Assert.Null(result.Configuration.ConditionTree);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
