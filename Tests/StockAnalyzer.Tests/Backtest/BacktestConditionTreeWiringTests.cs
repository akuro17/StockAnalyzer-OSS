using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Wiring of the condition tree into the run, save and load paths of the Backtest window (MP-2 Phase 2, addenda A1/A3).</summary>
public class BacktestConditionTreeWiringTests
{
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<CandleData> BuildBars(int count)
    {
        var bars = new List<CandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (decimal)(10 * Math.Sin(i / 6.0)) + (i / 10m);
            bars.Add(new CandleData(Start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
        }
        return bars;
    }

    private static BacktestConditionEntry CloseVsSma(BacktestConditionRole role, TradeSide position, ComparisonOperator op = ComparisonOperator.GreaterThan) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = op,
        TargetMode = RightHandTargetMode.Indicator,
        Right = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = 5 } },
        Role = role,
        Position = position,
    };

    private static BacktestRunSnapshot Snapshot(List<CandleData> bars, ImmutableArray<BacktestConditionEntry> conditions, BacktestConditionTree? tree) => new(
        "TEST",
        TimeFrame.D1,
        bars[0].Timestamp,
        bars[^1].Timestamp,
        new BacktestConfiguration
        {
            ExecutionModel = ExecutionModel.Legacy,
            InitialCapital = 1_000_000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m,
            InitialMarginRatio = 1m,
            MaintenanceMarginRatio = 0.5m,
        },
        conditions,
        null,
        ImmutableArray<StrategyIndicatorRequest>.Empty,
        BacktestReportDefaults.BuiltIn,
        42,
        2000,
        tree);

    private static Task<PreparedBacktestRun> Prepare(BacktestRunSnapshot snapshot, List<CandleData> bars)
        => new BacktestRunPreparationService(new FakeDataService(bars)).PrepareAsync(snapshot, CancellationToken.None);

    [Fact]
    public async Task TreeWithALeaf_SelectsTheTreeStrategy_AndATreeSpecification()
    {
        List<CandleData> bars = BuildBars(60);
        BacktestConditionTree tree = BacktestConditionTreeMigrator.Migrate(new[] { CloseVsSma(BacktestConditionRole.EntryOnly, TradeSide.Long) });

        PreparedBacktestRun prepared = await Prepare(Snapshot(bars, ImmutableArray<BacktestConditionEntry>.Empty, tree), bars);

        var strategy = Assert.IsType<ConditionBasedBacktestStrategy>(prepared.Strategy);
        Assert.NotNull(strategy.Tree);
        Assert.Equal(BacktestStrategyKind.ConditionTree, prepared.StrategySpecification.Kind);
    }

    [Fact]
    public async Task TreeWithOnlyEmptyRoots_TakesTheNoOpPath_LikeAnEmptyList()
    {
        List<CandleData> bars = BuildBars(60);

        PreparedBacktestRun prepared = await Prepare(Snapshot(bars, ImmutableArray<BacktestConditionEntry>.Empty, BacktestConditionTree.Empty), bars);

        Assert.IsType<NoOpBacktestStrategy>(prepared.Strategy);
    }

    [Fact]
    public async Task NoTree_KeepsTheLegacyListPath()
    {
        List<CandleData> bars = BuildBars(60);
        ImmutableArray<BacktestConditionEntry> list = ImmutableArray.Create(CloseVsSma(BacktestConditionRole.EntryOnly, TradeSide.Long));

        PreparedBacktestRun prepared = await Prepare(Snapshot(bars, list, tree: null), bars);

        Assert.Null(Assert.IsType<ConditionBasedBacktestStrategy>(prepared.Strategy).Tree);
        Assert.Equal(BacktestStrategyKind.ConditionBased, prepared.StrategySpecification.Kind);
    }

    [Fact]
    public async Task MigratedLegacyList_RunsWithTheSameReproducibilityHashAsTheListRun()
    {
        List<CandleData> bars = BuildBars(200);
        BacktestConditionEntry[] entries =
        {
            CloseVsSma(BacktestConditionRole.EntryOnly, TradeSide.Long),
            CloseVsSma(BacktestConditionRole.ExitOnly, TradeSide.Long, ComparisonOperator.LessThan),
            CloseVsSma(BacktestConditionRole.Reversal, TradeSide.Short, ComparisonOperator.LessThan),
        };
        var engine = new BacktestEngine(new IndicatorFactory());

        PreparedBacktestRun legacy = await Prepare(Snapshot(bars, entries.ToImmutableArray(), tree: null), bars);
        PreparedBacktestRun tree = await Prepare(Snapshot(bars, ImmutableArray<BacktestConditionEntry>.Empty, BacktestConditionTreeMigrator.Migrate(entries)), bars);
        BacktestResult legacyResult = engine.Run(legacy.Input, legacy.Configuration, legacy.Strategy);
        BacktestResult treeResult = engine.Run(tree.Input, tree.Configuration, tree.Strategy);

        Assert.NotEmpty(legacyResult.Trades);
        Assert.Equal(legacyResult.ReproducibilityHash, treeResult.ReproducibilityHash);
    }

    [Fact]
    public async Task WindowRun_WithLeaves_UsesTheTreeStrategy_AndWithoutLeaves_TheNoOpStrategy()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        List<CandleData> bars = BuildBars(30);
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(engine: engine, dataService: new FakeDataService(bars));
        vm.Symbol = "TEST";
        vm.EvaluationStartUtc = bars[0].Timestamp;
        vm.EvaluationEndUtc = bars[^1].Timestamp;

        // Only groups, no leaf: the NoOp path (same as an empty list today).
        vm.IndicatorSelection.ConditionTree.TryAddGroup(vm.IndicatorSelection.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long), LogicalOperator.Or);
        vm.IndicatorSelection.ConditionTree.Delete(vm.IndicatorSelection.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long));
        await vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.IsType<NoOpBacktestStrategy>(engine.LastStrategy);

        vm.IndicatorSelection.ConditionTree.Load(BacktestConditionTreeMigrator.Migrate(new[] { CloseVsSma(BacktestConditionRole.EntryOnly, TradeSide.Long) }));
        await vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.NotNull(Assert.IsType<ConditionBasedBacktestStrategy>(engine.LastStrategy).Tree);
        // The Results presentation of a tree run lists the tree's leaves under their sections.
        Assert.Single(vm.Results.EntryConditionEntries);
        Assert.Contains("EntryLong=", vm.Results.Presentation!.ConditionExpression);
    }

    [Fact]
    public async Task WindowRun_WithAnEmptyNestedGroup_FailsWithTheCoreMessage_AndRunsNothing()
    {
        var engine = new ScriptableBacktestEngine();
        List<CandleData> bars = BuildBars(30);
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(engine: engine, dataService: new FakeDataService(bars));
        vm.Symbol = "TEST";
        vm.IndicatorSelection.ConditionTree.TryAddGroup(vm.IndicatorSelection.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long), LogicalOperator.And);

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.NotEqual(BacktestRunState.Completed, vm.State);
        Assert.Null(engine.LastStrategy);
    }

    // ---- save / load ----

    [Fact]
    public async Task Save_WritesAVersion2Tree_WithoutFlatEntries()
    {
        var configManager = new FakeBacktestConfigurationManager();
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(configurationManager: configManager);
        vm.IndicatorSelection.ConditionTree.Load(BacktestConditionTreeMigrator.Migrate(new[]
        {
            CloseVsSma(BacktestConditionRole.EntryOnly, TradeSide.Long),
            CloseVsSma(BacktestConditionRole.ExitOnly, TradeSide.Long, ComparisonOperator.LessThan),
        }));

        await vm.SaveConfigurationCommand.ExecuteAsync(null);

        BacktestConfigurationDto saved = Assert.IsType<BacktestConfigurationDto>(configManager.Saved);
        Assert.Equal(BacktestConfigurationDto.ConditionTreeSchemaVersion, saved.SchemaVersion);
        Assert.Empty(saved.ConditionEntries);
        Assert.NotNull(saved.ConditionTree);
    }
}
