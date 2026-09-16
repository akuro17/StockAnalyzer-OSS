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

    [Fact]
    public void AddCommonServices_RegistersDrawingDocumentServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        // Act
        services.AddCommonServices(configuration);
        var provider = services.BuildServiceProvider();

        var docRepo1 = provider.GetService<StockAnalyzer.Avalonia.Services.Drawing.IDrawingDocumentRepository>();
        var docRepo2 = provider.GetService<StockAnalyzer.Avalonia.Services.Drawing.IDrawingDocumentRepository>();
        var sessionStore1 = provider.GetService<StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSessionStore>();
        var sessionStore2 = provider.GetService<StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentSessionStore>();
        var chartRepo = provider.GetService<StockAnalyzer.Avalonia.Services.Drawing.IChartDrawingRepository>();

        // Assert
        Assert.NotNull(docRepo1);
        Assert.IsType<StockAnalyzer.Avalonia.Services.Drawing.DrawingDocumentRepository>(docRepo1);
        Assert.Same(docRepo1, docRepo2);
        Assert.NotNull(sessionStore1);
        Assert.Same(sessionStore1, sessionStore2);
        Assert.NotNull(chartRepo);

        var layerService = provider.GetService<StockAnalyzer.Avalonia.Services.Drawing.IDrawingLayerService>();
        Assert.NotNull(layerService);
        Assert.IsType<StockAnalyzer.Avalonia.Services.Drawing.DrawingLayerService>(layerService);
    }
}

