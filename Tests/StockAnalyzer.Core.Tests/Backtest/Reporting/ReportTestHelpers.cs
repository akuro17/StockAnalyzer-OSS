using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>Shared real-instance builders for P2 report tests (mocking BacktestResult/BacktestTrade/EquityPoint is prohibited).</summary>
internal static class ReportTestHelpers
{
    public static readonly DateTime BaseUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BacktestConfiguration Config(decimal initialCapital) => new()
    {
        InitialCapital = initialCapital,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
    };

    /// <summary>Builds a BacktestResult whose EquityPoints (index 0..) hold exactly `postWarmupEquity`, i.e. HistoryStartIndex=0 reproduces E[1..m] unchanged.</summary>
    public static BacktestResult BuildResult(decimal initialCapital, decimal[] postWarmupEquity, BacktestTrade[]? trades = null)
    {
        var points = ImmutableArray.CreateBuilder<EquityPoint>(postWarmupEquity.Length);
        for (int i = 0; i < postWarmupEquity.Length; i++)
        {
            points.Add(new EquityPoint(i, BaseUtc.AddDays(i), postWarmupEquity[i], postWarmupEquity[i], 0m, 0m));
        }

        return new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            trades is null ? ImmutableArray<BacktestTrade>.Empty : ImmutableArray.Create(trades),
            points.MoveToImmutable(),
            ImmutableArray<BacktestSignal>.Empty,
            Config(initialCapital),
            RunStatus.Completed,
            strategyName: "test",
            reproducibilityHash: new byte[32],
            isInsufficientData: false);
    }

    public static BacktestReportOptions Options(int historyStartIndex = 0, DateTime? startUtc = null, DateTime? endUtc = null, int annualPeriods = 252, int? tradingStartIndex = null) =>
        new BacktestReportOptions(TimeFrame.D1, historyStartIndex, startUtc ?? BaseUtc, endUtc ?? BaseUtc.AddDays(365))
        {
            AnnualPeriods = annualPeriods,
            TradingStartIndex = tradingStartIndex,
        };

    /// <summary>Names of the optional extended-metric report members (absent from reports persisted before they existed).</summary>
    public static readonly string[] ExtendedMetricJsonNames =
    {
        "GrossProfit", "GrossLoss", "AverageWin", "AverageLoss", "PayoffRatio", "LargestWin", "LargestLoss",
        "AverageHoldingPeriod", "MaxConsecutiveWins", "MaxConsecutiveLosses",
        "MaxDepthDrawdownDuration", "LongestDrawdownDuration", "TimeInMarket", "Exposure", "ExposureAdjustedCAGR",
    };

    /// <summary>Names of the optional right-censoring flags that qualify the two drawdown-duration metrics.</summary>
    public static readonly string[] ExtendedFlagJsonNames =
    {
        "MaxDepthDrawdownDurationRightCensored", "LongestDrawdownDurationRightCensored",
    };

    /// <summary>Round-trips a report through JSON with the extended members removed, i.e. what a pre-extension export loads as.</summary>
    public static BacktestReport ToLegacyReport(BacktestReport report)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(report))!.AsObject();
        foreach (string name in ExtendedMetricJsonNames) node.Remove(name);
        foreach (string name in ExtendedFlagJsonNames) node.Remove(name);
        return System.Text.Json.JsonSerializer.Deserialize<BacktestReport>(node.ToJsonString())!;
    }

    public static BacktestTrade Trade(decimal closedNet, int entryBar = 0, int exitBar = 1) => new(
        TradeId: 1, Side: TradeSide.Long,
        EntryBar: entryBar, EntryTime: BaseUtc, EntryPrice: 100m,
        ExitBar: exitBar, ExitTime: BaseUtc.AddDays(1), ExitPrice: 100m,
        Quantity: 1m,
        ClosedGross: closedNet, ClosedNet: closedNet,
        EntryFee: 0m, ExitFee: 0m,
        HoldingBars: exitBar - entryBar,
        IsForcedLiquidation: false);
}
