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
    public void AddCommonServices_GivesTheMainWindowViewModelTheRegisteredBacktestColumnSettingsManager()
    {
        // IoHydrationWebAiReview T7: the container builds the view model by constructor injection, so the optional
        // trailing parameter is filled from this singleton registration (and not left at its null default).
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        services.AddViewModels();

        var manager = Assert.Single(services, d => d.ServiceType == typeof(StockAnalyzer.Core.Services.Backtest.Configuration.IBacktestColumnSettingsManager));
        Assert.Equal(ServiceLifetime.Singleton, manager.Lifetime);
        Assert.Equal(typeof(StockAnalyzer.Core.Services.Backtest.Configuration.BacktestColumnSettingsManager), manager.ImplementationType);

        var viewModel = Assert.Single(services, d => d.ServiceType == typeof(StockAnalyzer.Avalonia.ViewModels.MainWindowViewModel));
        Assert.Equal(typeof(StockAnalyzer.Avalonia.ViewModels.MainWindowViewModel), viewModel.ImplementationType);
        var injected = Assert.Single(
            typeof(StockAnalyzer.Avalonia.ViewModels.MainWindowViewModel).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(StockAnalyzer.Core.Services.Backtest.Configuration.IBacktestColumnSettingsManager));
        Assert.True(injected.IsOptional);
    }

    [Fact]
    public void AddCommonServices_RegistersTheGraphConsumerSeparatelyFromTheSingleSymbolPrediction()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var graph = provider.GetRequiredService<StockAnalyzer.Core.Services.IGraphPredictionService>();
        var prediction = provider.GetRequiredService<StockAnalyzer.Core.Services.IPredictionService>();

        Assert.IsType<StockAnalyzer.Core.Services.GraphPredictionService>(graph);
        Assert.Same(graph, provider.GetRequiredService<StockAnalyzer.Core.Services.IGraphPredictionService>());
        Assert.IsType<StockAnalyzer.Core.Services.EnsemblePredictionService>(prediction); // the existing registration is unchanged
        Assert.NotSame(graph, prediction);
        Assert.Null(graph.Model); // nothing is loaded until a run folder is given explicitly
    }

    [Fact]
    public void AddCommonServices_RegistersTheAttentionService_AsASingleton()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var attention = provider.GetRequiredService<StockAnalyzer.Core.Services.IAttentionService>();

        Assert.IsType<StockAnalyzer.Core.Services.AttentionService>(attention);
        Assert.Same(attention, provider.GetRequiredService<StockAnalyzer.Core.Services.IAttentionService>());

        var ensemble = provider.GetRequiredService<StockAnalyzer.Core.Services.IEnsembleAttentionService>();
        Assert.IsType<StockAnalyzer.Core.Services.EnsembleAttentionService>(ensemble);
        Assert.Same(ensemble, provider.GetRequiredService<StockAnalyzer.Core.Services.IEnsembleAttentionService>());
    }

    [Fact]
    public void AddCommonServices_RegistersTheCurrentHealthService_AsTheSameSingletonAsThePredictionLog()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var log = provider.GetRequiredService<StockAnalyzer.Core.Services.IPredictionLogService>();
        var health = provider.GetRequiredService<StockAnalyzer.Core.Services.ICurrentPredictionHealthService>();

        Assert.IsType<StockAnalyzer.Core.Services.PredictionLogService>(log);
        Assert.Same(log, health); // one instance and one monitor gate behind both contracts

        // The default backend is the legacy JSON store: its outbox is inactive and no monitoring database is created.
        var outbox = provider.GetRequiredService<StockAnalyzer.Core.Services.IMonitoringNotificationOutbox>();
        Assert.False(outbox.IsActive);
        Assert.Same(outbox, provider.GetRequiredService<StockAnalyzer.Core.Services.IMonitoringNotificationOutbox>());
    }

    [Fact]
    public async Task AddCommonServices_AttestedEvidenceIsDormantByDefault_AndNeverTouchesALegacyBackend()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        // JSON backend: the diagnostics resolve to the null object (never a null, never a conflict) and the refresher declines before reading any source.
        var diagnostics = provider.GetRequiredService<StockAnalyzer.Core.Models.IAttestedEvidenceDiagnostics>();
        Assert.IsType<StockAnalyzer.Core.Models.NoAttestedEvidenceDiagnostics>(diagnostics);
        Assert.Empty(await diagnostics.GetConflictedStreamsAsync());
        var refresher = provider.GetRequiredService<StockAnalyzer.Avalonia.Services.IAttestedMonitoringRefresher>();
        Assert.False(await refresher.TryRefreshAsync("SYM", StockAnalyzer.Core.Models.TimeframeType.Daily, [], CancellationToken.None));
        Assert.Same(refresher, provider.GetRequiredService<StockAnalyzer.Avalonia.Services.IAttestedMonitoringRefresher>());
    }

    [Fact]
    public void AddCommonServices_RegistersTheStoredModelServiceNextToThePortfolioServiceOnTheSameSettings()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var portfolio = provider.GetRequiredService<StockAnalyzer.Core.Services.ReinforcementLearning.IRlPortfolioService>();
        var transfer = provider.GetRequiredService<StockAnalyzer.Core.Services.ReinforcementLearning.IRlPortfolioTransferService>();

        Assert.IsType<StockAnalyzer.Core.Services.ReinforcementLearning.RlPortfolioTransferService>(transfer);
        Assert.Same(transfer, provider.GetRequiredService<StockAnalyzer.Core.Services.ReinforcementLearning.IRlPortfolioTransferService>());
        Assert.Equal(portfolio.Settings.PartitionMode, ((StockAnalyzer.Core.Services.ReinforcementLearning.RlPortfolioTransferService)transfer).Settings.PartitionMode);
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

    [Fact]
    public async System.Threading.Tasks.Task AddCommonServices_ResolvesTheScatterMetricEvaluator_WithoutTouchingTheRegistry()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        // The registry is only looked up, never built: building it would read the user's real Data\Config\scatter_metrics.json.
        var evaluator = provider.GetRequiredService<StockAnalyzer.Core.Services.Analysis.IScatterMetricEvaluator>();

        Assert.IsType<StockAnalyzer.Core.Services.Analysis.ScatterMetricEvaluator>(evaluator);
        Assert.Same(evaluator, provider.GetRequiredService<StockAnalyzer.Core.Services.Analysis.IScatterMetricEvaluator>());
    }
}
