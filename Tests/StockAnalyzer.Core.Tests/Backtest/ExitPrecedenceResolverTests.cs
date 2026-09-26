using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class ExitPrecedenceResolverTests
{
    [Fact]
    public void NeitherTouches_ReturnsNone()
    {
        Assert.Equal(ExitWinner.None, ExitPrecedenceResolver.Resolve(null, null));
    }

    [Fact]
    public void OnlyPendingOrderTouches_PendingOrderWins()
    {
        Assert.Equal(ExitWinner.PendingOrder, ExitPrecedenceResolver.Resolve(1.5m, null));
    }

    [Fact]
    public void OnlyMarginLiquidationTouches_MarginLiquidationWins()
    {
        Assert.Equal(ExitWinner.MarginLiquidation, ExitPrecedenceResolver.Resolve(null, 1.5m));
    }

    [Fact]
    public void PendingOrderReachedFirst_PendingOrderWins()
    {
        Assert.Equal(ExitWinner.PendingOrder, ExitPrecedenceResolver.Resolve(0.5m, 1.8m));
    }

    [Fact]
    public void MarginLiquidationReachedFirst_MarginLiquidationWins()
    {
        Assert.Equal(ExitWinner.MarginLiquidation, ExitPrecedenceResolver.Resolve(1.8m, 0.5m));
    }

    [Fact]
    public void ExactTie_MarginLiquidationWins()
    {
        Assert.Equal(ExitWinner.MarginLiquidation, ExitPrecedenceResolver.Resolve(0m, 0m));
    }
}
