using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

public sealed class TradeHistoryExpandedColumnsTests
{
    [Fact]
    public void KnownColumns_ContainsAllExpandedColumns()
    {
        var expectedColumns = new[]
        {
            "TradeId", "Side", "EntryTime", "EntryPrice", "ExitTime", "ExitPrice", "Quantity",
            "EntryValue", "ExitValue", "Commission", "Slippage", "ClosedNet", "PnLPercent",
            "MfePrice", "MfePercent", "MaePrice", "MaePercent",
            "HoldingBars", "Duration", "EntrySignal", "ExitReason", "ForcedLiquidation"
        };

        var actualColumnIds = BacktestTradeHistoryColumnChooserViewModel.KnownColumns
            .Select(c => c.Id)
            .ToList();

        Assert.Equal(expectedColumns.Length, actualColumnIds.Count);
        foreach (var col in expectedColumns)
        {
            Assert.Contains(col, actualColumnIds);
        }
    }

    [Fact]
    public void BacktestTradeRow_HoldsAllExpandedProperties()
    {
        var row = new BacktestTradeRow
        {
            TradeId = 10,
            SideKind = TradeSide.Long,
            EntryTime = new DateTime(2025, 1, 1, 9, 30, 0, DateTimeKind.Utc),
            EntryPrice = 150m,
            ExitTime = new DateTime(2025, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            ExitPrice = 160m,
            Quantity = 100m,
            ClosedNet = 980m,
            PnLSemantic = BacktestMetricSemantic.Plus,
            IsForcedLiquidation = false,
            EntryValue = 15000m,
            ExitValue = 16000m,
            Commission = 20m,
            Slippage = 5m,
            PnLPercent = 6.53m,
            PnLPercentSemantic = BacktestMetricSemantic.Plus,
            MfePrice = 12m,
            MfePercent = 8.0m,
            MaePrice = 2m,
            MaePercent = 1.33m,
            HoldingBars = 5,
            Duration = TimeSpan.FromHours(24.5),
            EntrySignal = "RSI Oversold",
            ExitReason = "SMA Cross"
        };

        Assert.Equal(15000m, row.EntryValue);
        Assert.Equal(16000m, row.ExitValue);
        Assert.Equal(20m, row.Commission);
        Assert.Equal(5m, row.Slippage);
        Assert.Equal(6.53m, row.PnLPercent);
        Assert.Equal(12m, row.MfePrice);
        Assert.Equal(8.0m, row.MfePercent);
        Assert.Equal(2m, row.MaePrice);
        Assert.Equal(1.33m, row.MaePercent);
        Assert.Equal(5, row.HoldingBars);
        Assert.Equal("RSI Oversold", row.EntrySignal);
        Assert.Equal("SMA Cross", row.ExitReason);
    }
}
