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
    public void AddCommonServices_ResolvesLayeredChartImageExportService()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddCommonServices(configuration);
        var provider = services.BuildServiceProvider();

        var layered = provider.GetService<StockAnalyzer.Avalonia.Services.Export.ILayeredChartImageExportService>();
        var legacy = provider.GetService<StockAnalyzer.Avalonia.Services.Export.IChartImageExportService>();

        Assert.NotNull(layered);
        Assert.NotNull(legacy);
        Assert.IsType<StockAnalyzer.Avalonia.Services.Export.ChartImageExportService>(layered);
    }

    [Fact]
    public void AddCommonServices_RegistersTheDiagnosticEngine_AsTheSameSingletonAsTheBacktestEngine()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddCommonServices(configuration);
        var provider = services.BuildServiceProvider();

        var engine = provider.GetService<StockAnalyzer.Core.Services.Backtest.Engine.IBacktestEngine>();
        var diagnosticEngine = provider.GetService<StockAnalyzer.Core.Services.Backtest.Engine.IBacktestDiagnosticEngine>();

        Assert.NotNull(engine);
        Assert.NotNull(diagnosticEngine);
        Assert.IsType<StockAnalyzer.Core.Services.Backtest.Engine.BacktestEngine>(engine);
        Assert.Same(engine, diagnosticEngine); // one engine instance behind both interfaces, never a second one
        Assert.Same(diagnosticEngine, provider.GetService<StockAnalyzer.Core.Services.Backtest.Engine.IBacktestDiagnosticEngine>());
    }

    [Fact]
    public void AddCommonServices_ResolvesEvaluationService_WithSharedEngineAndQualifiedGenerator()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddCommonServices(configuration);
        var provider = services.BuildServiceProvider();

        var evaluation = provider.GetService<StockAnalyzer.Core.Services.Backtest.Evaluation.IBacktestEvaluationService>();
        var generator = provider.GetService<StockAnalyzer.Core.Services.Backtest.Reporting.IQualifiedBacktestReportGenerator>();
        var legacyGenerator = provider.GetService<StockAnalyzer.Core.Services.Backtest.Reporting.IBacktestReportGenerator>();

        Assert.IsType<StockAnalyzer.Core.Services.Backtest.Evaluation.BacktestEvaluationService>(evaluation);
        Assert.Same(evaluation, provider.GetService<StockAnalyzer.Core.Services.Backtest.Evaluation.IBacktestEvaluationService>());
        Assert.Same(generator, legacyGenerator); // one generator behind the legacy and the qualified interface
        Assert.IsType<StockAnalyzer.Core.Services.Backtest.Evaluation.NoSamplingEvidenceTrustPolicy>(
            provider.GetService<StockAnalyzer.Core.Services.Backtest.Evaluation.ISamplingEvidenceTrustPolicy>());
    }

    [Fact]
    public void AddCommonServices_RegistersStrictEvidenceEngine_AsIndependentSingleton()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddCommonServices(configuration);
        var provider = services.BuildServiceProvider();

        var first = provider.GetService<StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence.IStrictBacktestEngine>();
        var second = provider.GetService<StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence.IStrictBacktestEngine>();

        Assert.NotNull(first);
        Assert.IsType<StockAnalyzer.Core.Services.Backtest.Engine.StrictEvidence.StrictBacktestEngine>(first);
        Assert.Same(first, second);
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
