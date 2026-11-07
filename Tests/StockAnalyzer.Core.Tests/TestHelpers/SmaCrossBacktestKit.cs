#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// The single definition of the two pieces every gateway-level backtest test needs: a real <see cref="ReadOnlyStockAnalyzerGateway"/> over a parquet fixture, and the snapshot of the
/// "close above its SMA(period) enters, below exits" strategy. Shared by RunBacktestBatchTests (batch equals individual runs) and BacktestSweepCostMeasurementTests (opt-in cost probe).
/// </summary>
internal static class SmaCrossBacktestKit
{
    /// <summary>A gateway over the fixture's parquet folders; <paramref name="data"/> is the data service the runs read through (a counting wrapper in some tests).</summary>
    public static ReadOnlyStockAnalyzerGateway CreateGateway(IDataService data, DuckDBConnectionManager db, ReadOnlyDataTreeFixture fixture)
    {
        var source = new ParquetReadOnlyMarketDataSource(new ParquetMarketDataProvider(db, UnavailablePythonService.Instance, Options.Create(fixture.Settings)));
        var screener = new ScreenerService(data, new PythonIpcTestSettingsBase(), new IMarketDataProvider[] { new ReadOnlyMetadataMarketDataProvider(source) });
        return new ReadOnlyStockAnalyzerGateway(
            source,
            data,
            new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default),
            screener,
            new FileReadOnlyTemplateSource(Path.Combine(fixture.Root, "Templates", "Screener")),
            new ScreenerTemplateEvaluator(screener),
            new BacktestRunPreparationService(data),
            new BacktestEvaluationService(new BacktestEngine(IndicatorFactory.Default), new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy()));
    }

    /// <summary>Close above its SMA enters long, close below exits; the factory account and report defaults, the default bootstrap seed.</summary>
    public static BacktestRunSnapshot Snapshot(
        string symbol, int smaPeriod, DateTime startUtc, DateTime endUtc, int bootstrapIterations, ExecutionModel model = ExecutionModel.Legacy)
    {
        BacktestConditionSide Close() => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close, OutputName = IndicatorResult.MainSeriesName };
        BacktestConditionSide Sma() => new() { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = smaPeriod }, OutputName = IndicatorResult.MainSeriesName };
        BacktestConditionGroup Group(ComparisonOperator op) => new(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(new BacktestConditionLeaf(
            new BacktestConditionEntry { Left = Close(), Operator = op, TargetMode = RightHandTargetMode.Indicator, Right = Sma() })));
        var tree = new BacktestConditionTree(
            Group(ComparisonOperator.GreaterThan), BacktestConditionGroup.EmptyAnd, Group(ComparisonOperator.LessThan),
            BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd, BacktestConditionGroup.EmptyAnd);
        return new BacktestRunSnapshot(
            symbol, TimeFrame.D1, startUtc, endUtc,
            new BacktestConfiguration
            {
                InitialCapital = BacktestDefaults.InitialCapital,
                SizingModel = BacktestDefaults.SizingModel,
                SizingParameter = BacktestDefaults.SizingParameter,
                ExecutionModel = model,
            },
            ImmutableArray<BacktestConditionEntry>.Empty, null, ImmutableArray<StrategyIndicatorRequest>.Empty, BacktestReportDefaults.BuiltIn,
            BacktestDefaults.BootstrapSeed, bootstrapIterations, tree);
    }
}
