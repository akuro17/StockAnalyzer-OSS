using System.Text.Json;
using StockAnalyzer.Core.Models.Portfolio;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// GetCostBasis is Quantity x AverageCostPerUnit (in the currency of AverageCost). It is a method so that adding it
/// leaves the serialized shape of the Position record unchanged.
/// </summary>
public sealed class PositionCostBasisTests
{
    [Fact]
    public void GetCostBasis_IsQuantityTimesAverageCost() =>
        Assert.Equal(1502.5m, new Position("AAPL", 10m, 150.25m).GetCostBasis());

    [Fact]
    public void GetCostBasis_KeepsAFractionalQuantityExact() =>
        Assert.Equal(100m, new Position("AAPL", 0.5m, 200m).GetCostBasis());

    [Fact]
    public void GetCostBasis_IsZeroForAZeroAverageCost() =>
        Assert.Equal(0m, new Position("AAPL", 3m, 0m).GetCostBasis());

    [Fact]
    public void GetCostBasis_IsTheSameForAShortPosition()
    {
        var position = new Position("AAPL", 100m, 200m, isShort: true);

        Assert.Equal(20_000m, position.GetCostBasis());
    }

    [Fact]
    public void GetCostBasis_DoesNotAppearInTheSerializedShape()
    {
        var json = JsonSerializer.Serialize(new Position("AAPL", 10m, 150.25m));

        Assert.DoesNotContain("CostBasis", json);
    }
}
