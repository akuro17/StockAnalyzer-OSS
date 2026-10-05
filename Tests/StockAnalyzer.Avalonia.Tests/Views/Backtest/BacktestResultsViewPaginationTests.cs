using System;
using System.Collections.Immutable;
using System.Linq;
using Avalonia.Headless.XUnit;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

[Collection("LocalizationSharedState")]
public class BacktestResultsViewPaginationTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    public BacktestResultsViewPaginationTests()
    {
        LocalizationManager.Instance.Initialize("ja");
    }

    private static (BacktestResult Result, BacktestReport Report) MakeRunWithTrades(params BacktestTrade[] trades)
    {
        var config = new BacktestConfiguration
        {
            InitialCapital = 1000m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 1m
        };
        var points = ImmutableArray.Create(
            new EquityPoint(0, Start, 1000m, 1000m, 0m, 0m),
            new EquityPoint(1, Start.AddDays(1), 1100m, 1100m, 0m, 0m));

        var result = new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            trades.ToImmutableArray(),
            points,
            ImmutableArray<BacktestSignal>.Empty,
            config,
            RunStatus.Completed,
            "TestStrategy",
            new byte[32],
            false);

        var options = new BacktestReportOptions(TimeFrame.D1, 0, Start, Start.AddDays(2))
        {
            AnnualPeriods = 252
        };
        return (result, new BacktestReportGenerator().Generate(result, options));
    }

    private static BacktestTrade CreateTrade(long id, decimal entryPrice, decimal closedNet, TradeSide side = TradeSide.Long)
    {
        return new BacktestTrade(
            id,
            side,
            0,
            Start,
            entryPrice,
            1,
            Start.AddHours(2),
            entryPrice + closedNet,
            1m,
            closedNet,
            closedNet,
            0m,
            0m,
            1,
            false);
    }

    [Fact]
    public void EmptyTrades_SetsPageInfoZeroAndDisablesNavigation()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var (result, report) = MakeRunWithTrades();
        vm.Update(result, report, 0);

        Assert.Equal(0, vm.TotalTradeCount);
        Assert.Equal(1, vm.TotalTradePages);
        Assert.Equal(1, vm.CurrentTradePage);
        Assert.Empty(vm.PagedTrades);
        Assert.False(vm.CanPreviousTradePage);
        Assert.False(vm.CanNextTradePage);
        Assert.Contains("0 - 0 of 0", vm.TradePaginationInfoText);

        Assert.Single(vm.TradePaginationTokens);
        Assert.Equal(1, vm.TradePaginationTokens[0].PageNumber);
        Assert.True(vm.TradePaginationTokens[0].IsActive);
        Assert.False(vm.TradePaginationTokens[0].IsEllipsis);
    }

    [Fact]
    public void SingleTrade_SetsPageInfoOneAndDisablesNavigation()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var (result, report) = MakeRunWithTrades(CreateTrade(1, 100m, 10m));
        vm.Update(result, report, 0);

        Assert.Equal(1, vm.TotalTradeCount);
        Assert.Equal(1, vm.TotalTradePages);
        Assert.Equal(1, vm.CurrentTradePage);
        Assert.Single(vm.PagedTrades);
        Assert.False(vm.CanPreviousTradePage);
        Assert.False(vm.CanNextTradePage);
        Assert.Contains("1 - 1 of 1", vm.TradePaginationInfoText);
    }

    [Fact]
    public void MultipleTrades_PaginatesCorrectlyAcrossPages()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var trades = Enumerable.Range(1, 25)
            .Select(i => CreateTrade(i, 100m + i, i * 2m))
            .ToArray();

        var (result, report) = MakeRunWithTrades(trades);
        vm.Update(result, report, 0);

        // Set page size to 10
        var opt10 = vm.TradePageSizeOptions.First(o => o.Value == 10);
        vm.SelectedTradePageSize = opt10;

        Assert.Equal(25, vm.TotalTradeCount);
        Assert.Equal(3, vm.TotalTradePages);
        Assert.Equal(1, vm.CurrentTradePage);
        Assert.Equal(10, vm.PagedTrades.Length);
        Assert.False(vm.CanPreviousTradePage);
        Assert.True(vm.CanNextTradePage);
        Assert.Contains("1 - 10 of 25", vm.TradePaginationInfoText);
        Assert.Equal(1, vm.PagedTrades[0].TradeId);
        Assert.Equal(10, vm.PagedTrades[9].TradeId);

        // Move to Page 2
        vm.NextTradePageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentTradePage);
        Assert.True(vm.CanPreviousTradePage);
        Assert.True(vm.CanNextTradePage);
        Assert.Contains("11 - 20 of 25", vm.TradePaginationInfoText);
        Assert.Equal(11, vm.PagedTrades[0].TradeId);
        Assert.Equal(20, vm.PagedTrades[9].TradeId);

        // Move to Page 3
        vm.NextTradePageCommand.Execute(null);
        Assert.Equal(3, vm.CurrentTradePage);
        Assert.True(vm.CanPreviousTradePage);
        Assert.False(vm.CanNextTradePage);
        Assert.Equal(5, vm.PagedTrades.Length);
        Assert.Contains("21 - 25 of 25", vm.TradePaginationInfoText);
        Assert.Equal(21, vm.PagedTrades[0].TradeId);
        Assert.Equal(25, vm.PagedTrades[4].TradeId);

        // Move back to Page 2 via Previous
        vm.PreviousTradePageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentTradePage);
        Assert.Equal(11, vm.PagedTrades[0].TradeId);

        // Direct jump to Page 1
        vm.GoToTradePageCommand.Execute(1);
        Assert.Equal(1, vm.CurrentTradePage);
        Assert.Equal(1, vm.PagedTrades[0].TradeId);
    }

    [Fact]
    public void ChangePageSize_ResetsCurrentPageToOneAndRecalculatesPages()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var trades = Enumerable.Range(1, 25)
            .Select(i => CreateTrade(i, 100m + i, i * 2m))
            .ToArray();

        var (result, report) = MakeRunWithTrades(trades);
        vm.Update(result, report, 0);

        vm.SelectedTradePageSize = vm.TradePageSizeOptions.First(o => o.Value == 10);
        vm.GoToTradePageCommand.Execute(2);
        Assert.Equal(2, vm.CurrentTradePage);

        // Change page size to 5
        vm.SelectedTradePageSize = vm.TradePageSizeOptions.First(o => o.Value == 5);
        Assert.Equal(1, vm.CurrentTradePage);
        Assert.Equal(5, vm.TotalTradePages);
        Assert.Equal(5, vm.PagedTrades.Length);
        Assert.Contains("1 - 5 of 25", vm.TradePaginationInfoText);
    }

    [Fact]
    public void SortTrades_ChangesSortOrderAscendingDescendingNone()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var trades = new[]
        {
            CreateTrade(1, 100m, 10m),
            CreateTrade(2, 100m, -5m),
            CreateTrade(3, 100m, 20m)
        };

        var (result, report) = MakeRunWithTrades(trades);
        vm.Update(result, report, 0);

        // Default initial order
        Assert.Equal(TradeSortOrder.None, vm.ActiveSortOrder);
        Assert.Null(vm.ActiveSortColumnId);
        Assert.Equal([1, 2, 3], vm.PagedTrades.Select(t => t.TradeId));

        // 1st click: Ascending
        vm.SortTradesCommand.Execute("ClosedNet");
        Assert.Equal(TradeSortOrder.Ascending, vm.ActiveSortOrder);
        Assert.Equal("ClosedNet", vm.ActiveSortColumnId);
        Assert.Equal([2, 1, 3], vm.PagedTrades.Select(t => t.TradeId)); // -5, 10, 20

        // 2nd click: Descending
        vm.SortTradesCommand.Execute("ClosedNet");
        Assert.Equal(TradeSortOrder.Descending, vm.ActiveSortOrder);
        Assert.Equal("ClosedNet", vm.ActiveSortColumnId);
        Assert.Equal([3, 1, 2], vm.PagedTrades.Select(t => t.TradeId)); // 20, 10, -5

        // 3rd click: Reset to None
        vm.SortTradesCommand.Execute("ClosedNet");
        Assert.Equal(TradeSortOrder.None, vm.ActiveSortOrder);
        Assert.Null(vm.ActiveSortColumnId);
        Assert.Equal([1, 2, 3], vm.PagedTrades.Select(t => t.TradeId)); // 1, 2, 3
    }

    [Fact]
    public void SortTrades_AppliesTieBreakingOnTradeId()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        // Same EntryPrice (100) for all trades, with different IDs
        var trades = new[]
        {
            CreateTrade(3, 100m, 5m),
            CreateTrade(1, 100m, 5m),
            CreateTrade(2, 100m, 5m)
        };

        var (result, report) = MakeRunWithTrades(trades);
        vm.Update(result, report, 0);

        // Sort by EntryPrice ascending -> Tie-breaking guarantees TradeId ascending order: 1, 2, 3
        vm.SortTradesCommand.Execute("EntryPrice");
        Assert.Equal([1, 2, 3], vm.PagedTrades.Select(t => t.TradeId));

        // Sort by EntryPrice descending -> Tie-breaking still applies TradeId ascending order: 1, 2, 3
        vm.SortTradesCommand.Execute("EntryPrice");
        Assert.Equal([1, 2, 3], vm.PagedTrades.Select(t => t.TradeId));
    }

    [Fact]
    public void ComputePaginationTokens_ProducesCorrectTokens()
    {
        // <= 7 pages: all visible
        var tokens5 = BacktestResultsViewModel.ComputePaginationTokens(5, 3);
        Assert.Equal([1, 2, 3, 4, 5], tokens5.Select(t => t.PageNumber));
        Assert.True(tokens5.First(t => t.PageNumber == 3).IsActive);

        // > 7 pages, near start (C <= 4)
        var tokens26Start = BacktestResultsViewModel.ComputePaginationTokens(26, 1);
        Assert.Equal(7, tokens26Start.Length);
        Assert.Equal([1, 2, 3, 4, 5, 0, 26], tokens26Start.Select(t => t.PageNumber));
        Assert.True(tokens26Start[5].IsEllipsis);
        Assert.True(tokens26Start[0].IsActive);

        // > 7 pages, in middle (C = 10)
        var tokens26Mid = BacktestResultsViewModel.ComputePaginationTokens(26, 10);
        Assert.Equal(7, tokens26Mid.Length);
        Assert.Equal([1, 0, 9, 10, 11, 0, 26], tokens26Mid.Select(t => t.PageNumber));
        Assert.True(tokens26Mid[1].IsEllipsis);
        Assert.True(tokens26Mid[5].IsEllipsis);
        Assert.True(tokens26Mid[3].IsActive);

        // > 7 pages, near end (C >= P - 3)
        var tokens26End = BacktestResultsViewModel.ComputePaginationTokens(26, 25);
        Assert.Equal(7, tokens26End.Length);
        Assert.Equal([1, 0, 22, 23, 24, 25, 26], tokens26End.Select(t => t.PageNumber));
        Assert.True(tokens26End[1].IsEllipsis);
        Assert.True(tokens26End[5].IsActive);
    }

    [Fact]
    public void ExistingTradesProperty_RemainsIdenticalToPresentationTrades()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var trades = Enumerable.Range(1, 30)
            .Select(i => CreateTrade(i, 100m + i, i * 2m))
            .ToArray();

        var (result, report) = MakeRunWithTrades(trades);
        vm.Update(result, report, 0);

        // Set page size to 5
        vm.SelectedTradePageSize = vm.TradePageSizeOptions.First(o => o.Value == 5);

        // PagedTrades should contain 5 items
        Assert.Equal(5, vm.PagedTrades.Length);

        // Existing Trades property MUST contain all 30 items
        Assert.Equal(30, vm.Trades.Length);
        Assert.Equal(trades.Select(t => t.TradeId), vm.Trades.Select(t => t.TradeId));
    }

    [Fact]
    public void SortTrades_IgnoresInvalidColumnId_AndCanonicalizesCasing()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var trades = new[]
        {
            CreateTrade(1, 100m, 10m),
            CreateTrade(2, 200m, -5m)
        };

        var (result, report) = MakeRunWithTrades(trades);
        vm.Update(result, report, 0);

        // Invalid column ID should be ignored
        vm.SortTradesCommand.Execute("InvalidColumn");
        Assert.Equal(TradeSortOrder.None, vm.ActiveSortOrder);
        Assert.Null(vm.ActiveSortColumnId);

        // Case-insensitive matching should canonicalize to KnownColumns casing
        vm.SortTradesCommand.Execute("closednet");
        Assert.Equal(TradeSortOrder.Ascending, vm.ActiveSortOrder);
        Assert.Equal("ClosedNet", vm.ActiveSortColumnId);

        vm.Dispose();
    }

    [Fact]
    public void LocaleChanged_DynamicallyUpdatesPageSizeOptionsAndInfoText()
    {
        var locService = new LocalizationService();
        var vm = new BacktestResultsViewModel(
            locService,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var trades = Enumerable.Range(1, 25)
            .Select(i => CreateTrade(i, 100m + i, i * 2m))
            .ToArray();

        var (result, report) = MakeRunWithTrades(trades);
        vm.Update(result, report, 0);

        // Switch to Japanese
        LocalizationManager.Instance.Initialize("ja");
        string expectedJaFmt = LocalizationManager.Instance["Backtest_TradeList_PageSize_Format"];
        string expectedJaOption = string.Format(System.Globalization.CultureInfo.CurrentCulture, expectedJaFmt, 20);
        Assert.Equal(expectedJaOption, vm.TradePageSizeOptions.First(o => o.Value == 20).DisplayText);

        string expectedJaInfoFmt = LocalizationManager.Instance["Backtest_TradeList_Pagination_Info"];
        string expectedJaInfo = string.Format(System.Globalization.CultureInfo.CurrentCulture, expectedJaInfoFmt, 1, 20, 25);
        Assert.Equal(expectedJaInfo, vm.TradePaginationInfoText);

        // Switch to English
        LocalizationManager.Instance.Initialize("en");
        string expectedEnFmt = LocalizationManager.Instance["Backtest_TradeList_PageSize_Format"];
        string expectedEnOption = string.Format(System.Globalization.CultureInfo.CurrentCulture, expectedEnFmt, 20);
        Assert.Equal(expectedEnOption, vm.TradePageSizeOptions.First(o => o.Value == 20).DisplayText);

        string expectedEnInfoFmt = LocalizationManager.Instance["Backtest_TradeList_Pagination_Info"];
        string expectedEnInfo = string.Format(System.Globalization.CultureInfo.CurrentCulture, expectedEnInfoFmt, 1, 20, 25);
        Assert.Equal(expectedEnInfo, vm.TradePaginationInfoText);

        vm.Dispose();
    }
}
