using StockAnalyzer.Core.Models.Portfolio;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>The lower bounds of the transaction dialog keep the values the dialog had as literals before they moved into <see cref="PortfolioInputLimits"/>.</summary>
public sealed class PortfolioInputLimitsTests
{
    [Fact]
    public void TheBoundsKeepTheirValues()
    {
        Assert.Equal(0.000001m, PortfolioInputLimits.MinTradeQuantity);
        Assert.Equal(0.000001m, PortfolioInputLimits.MinExchangeRate);
        Assert.Equal(0.01m, PortfolioInputLimits.MinCashAmount);
        Assert.Equal(0m, PortfolioInputLimits.MinNonNegativeAmount);
    }
}
