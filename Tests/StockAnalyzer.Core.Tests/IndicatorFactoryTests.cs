using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Tests for IndicatorFactory to verify auto-registration mechanism.
/// </summary>
public class IndicatorFactoryTests
{
    public IndicatorFactoryTests()
    {
    }

    [Fact]
    public void IsRegistered_WithUnattributedType_ShouldReturnFalse()
    {
        // Arrange
        IIndicatorFactory factory = new IndicatorFactory();

        // Act - Use a value that is NOT defined in the enum
        var isRegistered = factory.IsRegistered((IndicatorType)9999);

        // Assert - should be false since no attribute is applied
        Assert.False(isRegistered);
    }

    [Fact]
    public void Create_WithUnregisteredType_ShouldReturnNull()
    {
        // Arrange
        IIndicatorFactory factory = new IndicatorFactory();

        // Act
        // Use a value that is NOT defined in the enum or at least not attributed.
        var indicator = factory.Create((IndicatorType)9999);

        // Assert
        Assert.Null(indicator);
    }

    [Fact]
    public void IsRegistered_WithSMA_ShouldReturnTrue()
    {
        // Arrange
        IIndicatorFactory factory = new IndicatorFactory();

        // Act - SMA is now attributed with [StockAnalyzerIndicator]
        var isRegistered = factory.IsRegistered(IndicatorType.SMA);

        // Assert
        Assert.True(isRegistered);
    }

    [Fact]
    public void GetRegisteredTypes_ShouldContainSMA()
    {
        // Arrange
        IIndicatorFactory factory = new IndicatorFactory();

        // Act
        var types = factory.GetRegisteredTypes();

        // Assert - SMA should be registered
        Assert.Contains(IndicatorType.SMA, types);
    }

    [Fact]
    public void Create_WithSMA_ShouldReturnCoreSmaIndicator()
    {
        // Arrange
        IIndicatorFactory factory = new IndicatorFactory();

        // Act
        var indicator = factory.Create(IndicatorType.SMA);

        // Assert
        Assert.NotNull(indicator);
        Assert.IsType<CoreSmaIndicator>(indicator);
    }

    [Fact]
    public void Create_WithSMAAndParameters_ShouldConfigureIndicator()
    {
        // Arrange
        IIndicatorFactory factory = new IndicatorFactory();
        var parameters = new CoreSmaParameter { Period = 50 };

        // Act
        var indicator = factory.Create(IndicatorType.SMA, parameters);

        // Assert
        Assert.NotNull(indicator);
        var smaIndicator = Assert.IsType<CoreSmaIndicator>(indicator);
        Assert.Equal(50, smaIndicator.Period);
    }

    [Fact]
    public void Create_SMA_CalculatesCorrectly()
    {
        // Arrange
        IIndicatorFactory factory = new IndicatorFactory();
        var parameters = new CoreSmaParameter { Period = 3 };
        var indicator = factory.Create(IndicatorType.SMA, parameters)!;
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Now, 10m, 10m, 10m, 10m, 100),
            new(DateTime.Now, 20m, 20m, 20m, 20m, 100),
            new(DateTime.Now, 30m, 30m, 30m, 30m, 100),
            new(DateTime.Now, 40m, 40m, 40m, 40m, 100),
            new(DateTime.Now, 50m, 50m, 50m, 50m, 100)
        };

        // Act
        indicator.Calculate(candles);

        // Assert
        Assert.Equal(5, indicator.Values.Count);
        Assert.Null(indicator.Values[0]); // Not enough data
        Assert.Null(indicator.Values[1]); // Not enough data
        Assert.Equal(20m, indicator.Values[2]); // (10+20+30)/3
        Assert.Equal(30m, indicator.Values[3]); // (20+30+40)/3
        Assert.Equal(40m, indicator.Values[4]); // (30+40+50)/3
    }
    
    [Fact]
    public void StaticFallbacks_ShouldWorkCorrectly()
    {
        var isRegistered = IndicatorFactory.IsRegisteredStatic(IndicatorType.SMA);
        var indicator = IndicatorFactory.CreateStatic(IndicatorType.SMA);

        Assert.True(isRegistered);
        Assert.NotNull(indicator);
        Assert.IsType<CoreSmaIndicator>(indicator);
    }

    [Fact]
#pragma warning disable CS0618 // Deliberately testing the deprecated (Obsolete) enum member
    public void Create_WithObsoleteHighLowBand_ShouldReturnNullNotThrow()
    {
        // HighLowBand was replaced by Highest/Lowest and de-registered from the factory,
        // but the enum member itself is kept (Obsolete, not deleted) so that existing
        // serialized templates referencing its ordinal do not shift other indicators' values.
        // Loading such a template must degrade gracefully (null result), never crash.
        IIndicatorFactory factory = new IndicatorFactory();

        Assert.False(factory.IsRegistered(IndicatorType.HighLowBand));
        Assert.Null(factory.Create(IndicatorType.HighLowBand));
    }
#pragma warning restore CS0618

    [Fact]
    public void Create_WithHighestAndLowest_ShouldReturnRegisteredIndicators()
    {
        IIndicatorFactory factory = new IndicatorFactory();

        Assert.True(factory.IsRegistered(IndicatorType.Highest));
        Assert.True(factory.IsRegistered(IndicatorType.Lowest));
        Assert.NotNull(factory.Create(IndicatorType.Highest));
        Assert.NotNull(factory.Create(IndicatorType.Lowest));
    }
}

