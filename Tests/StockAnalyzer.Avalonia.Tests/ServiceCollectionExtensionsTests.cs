using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCommonServices_RegistersIIndicatorFactory()
    {
        // Arrange
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder().Build();

        // Act
        services.AddCommonServices(configuration);
        var provider = services.BuildServiceProvider();
        var factory = provider.GetService<IIndicatorFactory>();

        // Assert
        Assert.NotNull(factory);
        Assert.IsType<IndicatorFactory>(factory);
    }
}
