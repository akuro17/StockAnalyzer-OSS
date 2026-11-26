using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// After a partial exit the average cost of what stays open is the average of the lots still open (FIFO remaining lots, owner decision D-1=A),
/// so that realized PnL (FIFO lots) plus unrealized PnL (average cost) equals the economic result. The legacy method keeps the old average and stays pinned.
/// Every expected number below is a hand computation.
/// </summary>
public sealed class PortfolioCostBasisMethodTests
{
    private const decimal Deposited = 1_000_000m;
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Transaction Deposit(decimal amount) => new(Start, TransactionType.Deposit, null, 0m, 0m, amount);

    private static Transaction Trade(int day, TransactionType type, decimal quantity, decimal price, decimal fee = 0m, Guid? related = null) =>
        new(Start.AddDays(day), type, "AAPL", quantity, price, 0m, fee: fee, relatedTransactionId: related);

    private static Dictionary<string, decimal> At(decimal price) => new() { ["AAPL"] = price };

    private static Portfolio Rebuild(CostBasisMethod method, params Transaction[] history) =>
        new PortfolioManager().RebuildPortfolio(0m, history.Prepend(Deposit(Deposited)).ToList(), method);

    private static PortfolioMetrics Metrics(Portfolio portfolio, decimal price) =>
        new PortfolioManager().Evaluate(portfolio, At(price)).Metrics;

    // Buy 100@100, buy 100@200, sell 100@250: realized 100 x (250 - 100) = 15,000 (the first lot).
    private static Transaction[] LongHistory() => new[]
    {
        Trade(1, TransactionType.Long, 100m, 100m),
        Trade(2, TransactionType.Long, 100m, 200m),
        Trade(3, TransactionType.ExitLong, 100m, 250m),
    };

    [Fact]
    public void Legacy_KeepsTheOldWeightedAverageAfterAPartialExit()
    {
        var portfolio = Rebuild(CostBasisMethod.LegacyAveragePreserve, LongHistory());
        var metrics = Metrics(portfolio, 250m);

        Assert.Equal(150m, portfolio.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(15_000m, metrics.TotalRealizedPL);
        Assert.Equal(10_000m, metrics.TotalUnrealizedPL);
        Assert.Equal(25_000m, metrics.TotalRealizedPL + metrics.TotalUnrealizedPL);
    }

    [Fact]
    public void TheTwoArgumentMethod_IsTheLegacyMethod()
    {
        var history = LongHistory().Prepend(Deposit(Deposited)).ToList();
        var manager = new PortfolioManager();

        var twoArguments = manager.RebuildPortfolio(0m, history);
        var legacy = manager.RebuildPortfolio(0m, history, CostBasisMethod.LegacyAveragePreserve);

        Assert.Equal(twoArguments.Positions["AAPL"], legacy.Positions["AAPL"]);
        Assert.Equal(twoArguments.TotalRealizedPnL, legacy.TotalRealizedPnL);
    }

    [Fact]
    public void FifoRemainingLots_LongPartialExit_UsesTheAverageOfTheOpenLot()
    {
        var portfolio = Rebuild(CostBasisMethod.FifoRemainingLots, LongHistory());
        var metrics = Metrics(portfolio, 250m);

        Assert.Equal(200m, portfolio.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(100m, portfolio.Positions["AAPL"].Quantity);
        Assert.Equal(15_000m, metrics.TotalRealizedPL);
        Assert.Equal(5_000m, metrics.TotalUnrealizedPL);
        Assert.Equal(20_000m, metrics.TotalRealizedPL + metrics.TotalUnrealizedPL);
        // The economic result: cash paid 10,000 + 20,000, received 25,000, 100 shares worth 25,000 -> +20,000.
        Assert.Equal(20_000m, metrics.TotalValue - Deposited);
    }

    [Fact]
    public void FifoRemainingLots_ShortPartialCover_UsesTheAverageOfTheOpenLot()
    {
        // Short 100@200 and 100@100 (weighted average 150), cover 100@80: realized = 100 x (200 - 80) = 12,000 (the first lot).
        var history = new[]
        {
            Trade(1, TransactionType.Short, 100m, 200m),
            Trade(2, TransactionType.Short, 100m, 100m),
            Trade(3, TransactionType.ExitShort, 100m, 80m),
        };

        var legacy = Rebuild(CostBasisMethod.LegacyAveragePreserve, history);
        var fifo = Rebuild(CostBasisMethod.FifoRemainingLots, history);
        var legacyMetrics = Metrics(legacy, 80m);
        var fifoMetrics = Metrics(fifo, 80m);

        Assert.Equal(150m, legacy.Positions["AAPL_Short"].AverageCostPerUnit);
        Assert.Equal(7_000m, legacyMetrics.TotalUnrealizedPL);
        Assert.Equal(19_000m, legacyMetrics.TotalRealizedPL + legacyMetrics.TotalUnrealizedPL);

        Assert.Equal(100m, fifo.Positions["AAPL_Short"].AverageCostPerUnit);
        Assert.True(fifo.Positions["AAPL_Short"].IsShort);
        Assert.Equal(12_000m, fifoMetrics.TotalRealizedPL);
        Assert.Equal(2_000m, fifoMetrics.TotalUnrealizedPL);
        Assert.Equal(14_000m, fifoMetrics.TotalRealizedPL + fifoMetrics.TotalUnrealizedPL);
        // The economic result: 20,000 + 10,000 received, 8,000 paid to cover, 100 shares owed at 80 -> +14,000.
        Assert.Equal(14_000m, fifoMetrics.TotalValue - Deposited);
    }

    [Fact]
    public void FifoRemainingLots_AnExitThatNamesTheSecondLot_LeavesTheFirstLotAsTheAverage()
    {
        var second = Trade(2, TransactionType.Long, 100m, 200m);
        var history = new[]
        {
            Trade(1, TransactionType.Long, 100m, 100m),
            second,
            Trade(3, TransactionType.ExitLong, 100m, 250m, related: second.Id),
        };

        var legacy = Rebuild(CostBasisMethod.LegacyAveragePreserve, history);
        var fifo = Rebuild(CostBasisMethod.FifoRemainingLots, history);

        Assert.Equal(150m, legacy.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(100m, fifo.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(5_000m, fifo.TotalRealizedPnL);
        Assert.Equal(15_000m, Metrics(fifo, 250m).TotalUnrealizedPL);
    }

    [Fact]
    public void FifoRemainingLots_AnExitInsideTheSecondLot_KeepsOnlyTheRemainderOfThatLot()
    {
        var history = new[]
        {
            Trade(1, TransactionType.Long, 100m, 100m),
            Trade(2, TransactionType.Long, 100m, 200m),
            Trade(3, TransactionType.ExitLong, 150m, 250m),
        };

        var portfolio = Rebuild(CostBasisMethod.FifoRemainingLots, history);

        Assert.Equal(50m, portfolio.Positions["AAPL"].Quantity);
        Assert.Equal(200m, portfolio.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(2, portfolio.ClosedPositions.Count);
    }

    [Fact]
    public void FifoRemainingLots_AFullExitRemovesThePositionAndItsLots_SoTheNextBuyStartsFresh()
    {
        var history = new[]
        {
            Trade(1, TransactionType.Long, 100m, 100m),
            Trade(2, TransactionType.Long, 100m, 200m),
            Trade(3, TransactionType.ExitLong, 200m, 250m),
            Trade(4, TransactionType.Long, 50m, 300m),
        };

        var portfolio = Rebuild(CostBasisMethod.FifoRemainingLots, history);

        Assert.Equal(50m, portfolio.Positions["AAPL"].Quantity);
        Assert.Equal(300m, portfolio.Positions["AAPL"].AverageCostPerUnit);
    }

    [Fact]
    public void FifoRemainingLots_ABuyAfterAPartialExit_AveragesTheOpenLotWithTheNewOne()
    {
        var history = LongHistory().Append(Trade(4, TransactionType.Long, 100m, 300m)).ToArray();

        var legacy = Rebuild(CostBasisMethod.LegacyAveragePreserve, history);
        var fifo = Rebuild(CostBasisMethod.FifoRemainingLots, history);

        Assert.Equal(225m, legacy.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(250m, fifo.Positions["AAPL"].AverageCostPerUnit);
        Assert.Equal(200m, fifo.Positions["AAPL"].Quantity);
    }

    [Fact]
    public void FifoRemainingLots_TheAverageDoesNotIncludeTheCommission()
    {
        // Owner decision D-7=A: the average cost is the price only; the commission is booked in cash and realized PnL.
        var history = new[]
        {
            Trade(1, TransactionType.Long, 100m, 100m, fee: 50m),
            Trade(2, TransactionType.Long, 100m, 200m, fee: 50m),
            Trade(3, TransactionType.ExitLong, 100m, 250m, fee: 10m),
        };

        var portfolio = Rebuild(CostBasisMethod.FifoRemainingLots, history);

        Assert.Equal(200m, portfolio.Positions["AAPL"].AverageCostPerUnit);
    }

    [Fact]
    public void FifoRemainingLots_KeepsTheCurrencyOfThePositionAverage()
    {
        Transaction Usd(int day, TransactionType type, decimal quantity, decimal price) =>
            new(Start.AddDays(day), type, "AAPL", quantity, price, 0m, price: new Money(price, CurrencyCode.USD), commission: new Money(0m, CurrencyCode.USD));

        var usdDeposit = new Transaction(Start, TransactionType.Deposit, null, 0m, 0m, 100_000m, price: new Money(100_000m, CurrencyCode.USD), commission: new Money(0m, CurrencyCode.USD));
        var history = new List<Transaction>
        {
            usdDeposit,
            Usd(1, TransactionType.Long, 100m, 100m),
            Usd(2, TransactionType.Long, 100m, 200m),
            Usd(3, TransactionType.ExitLong, 100m, 250m),
        };

        var position = new PortfolioManager().RebuildPortfolio(0m, history, CostBasisMethod.FifoRemainingLots).Positions["AAPL"];

        Assert.Equal(200m, position.AverageCostPerUnit);
        Assert.Equal(CurrencyCode.USD, position.AverageCost.Currency);
        Assert.Equal(200m, position.AverageCost.Amount);
    }

    [Fact]
    public void BothMethods_BookTheSameCashRealizedPnLAndClosedPositions()
    {
        var history = LongHistory().Concat(new[]
        {
            Trade(4, TransactionType.Short, 30m, 120m, fee: 3m),
            Trade(5, TransactionType.ExitShort, 10m, 110m, fee: 1m),
        }).ToArray();

        var legacy = Rebuild(CostBasisMethod.LegacyAveragePreserve, history);
        var fifo = Rebuild(CostBasisMethod.FifoRemainingLots, history);

        Assert.Equal(legacy.CashBalance, fifo.CashBalance);
        Assert.Equal(legacy.CashBalances, fifo.CashBalances);
        Assert.Equal(legacy.TotalRealizedPnL, fifo.TotalRealizedPnL);
        Assert.Equal(legacy.RealizedPnLByCurrency, fifo.RealizedPnLByCurrency);
        Assert.Equal(
            legacy.ClosedPositions.Select(c => (c.Ticker, c.Quantity, c.EntryPrice, c.ExitPrice, c.RealizedPnL, c.TotalFees)),
            fifo.ClosedPositions.Select(c => (c.Ticker, c.Quantity, c.EntryPrice, c.ExitPrice, c.RealizedPnL, c.TotalFees)));
        Assert.Equal(legacy.Positions.Keys.OrderBy(k => k), fifo.Positions.Keys.OrderBy(k => k));
        Assert.Equal(legacy.Positions.Values.Select(p => p.Quantity).OrderBy(q => q), fifo.Positions.Values.Select(p => p.Quantity).OrderBy(q => q));
    }

    [Fact]
    public void AnUnknownMethod_IsRefused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PortfolioManager().RebuildPortfolio(0m, new List<Transaction>(), (CostBasisMethod)99));

        Assert.Equal("costBasisMethod", error.ParamName);
    }

    [Fact]
    public void TheApplicationDefault_IsTheFifoRemainingLotMethod()
    {
        Assert.Equal(CostBasisMethod.FifoRemainingLots, PortfolioAccountingDefaults.CostBasis);
    }

    [Fact]
    public void TheStoredNumbersOfTheMethodsDoNotChange()
    {
        Assert.Equal(0, (int)CostBasisMethod.LegacyAveragePreserve);
        Assert.Equal(1, (int)CostBasisMethod.FifoRemainingLots);
        Assert.Equal(2, Enum.GetValues<CostBasisMethod>().Length);
    }
}
