using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Composition root of the read-only gateway. It registers only types that read: no Python service, ticker
/// synchronisation, settings manager that saves, exporter, template service or UI service. Providers that create
/// folders or files in their constructors are built only when <see cref="ReadOnlyDataTree"/> says everything exists.
/// </summary>
public static class ReadOnlyCoreServiceCollectionExtensions
{
    public static IServiceCollection AddStockAnalyzerReadOnlyCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(configuration);
        services.AddStockAnalyzerSettingsOptions(configuration);
        services.AddSingleton<IStockAnalyzerSettings, StockAnalyzerSettings>();

        services.AddSingleton<DuckDBConnectionManager>();
        services.AddSingleton(sp => ReadOnlyDataTree.Probe(sp.GetRequiredService<IOptions<MarketDataSettings>>().Value));

        services.AddSingleton<IDataService>(sp => sp.GetRequiredService<ReadOnlyDataTree>().IsComplete
            ? new ParquetDataService(
                sp.GetRequiredService<DuckDBConnectionManager>(),
                sp.GetRequiredService<IOptions<MarketDataSettings>>(),
                sp.GetService<ILogger<ParquetDataService>>())
            : new EmptyDataService());

        services.AddSingleton<IReadOnlyMarketDataSource>(sp => sp.GetRequiredService<ReadOnlyDataTree>().IsComplete
            ? new ParquetReadOnlyMarketDataSource(new ParquetMarketDataProvider(
                sp.GetRequiredService<DuckDBConnectionManager>(),
                UnavailablePythonService.Instance,
                sp.GetRequiredService<IOptions<MarketDataSettings>>(),
                sp.GetService<ILogger<ParquetMarketDataProvider>>()))
            : new EmptyReadOnlyMarketDataSource());

        services.AddSingleton<IIndicatorFactory, IndicatorFactory>();
        services.AddSingleton<ISourceIndicatorService>(sp => new SourceIndicatorService(
            PathDiscovery.ResolveSourceIndicatorsPathWithoutCreating(SourceIndicatorService.FileName),
            sp.GetService<ILogger<SourceIndicatorService>>()));
        services.AddSingleton<IDynamicPeriodDriverService>(sp => new DynamicPeriodDriverService(
            PathDiscovery.ResolveDynamicPeriodDriversPathWithoutCreating(DynamicPeriodDriverService.FileName),
            sp.GetService<ILogger<DynamicPeriodDriverService>>()));
        services.AddSingleton<IAnalysisPipelineService>(sp => new AnalysisPipelineService(
            pythonService: null,
            indicatorFactory: sp.GetRequiredService<IIndicatorFactory>(),
            sourceIndicatorService: sp.GetRequiredService<ISourceIndicatorService>(),
            logger: sp.GetService<ILogger<AnalysisPipelineService>>(),
            dynamicPeriodDriverService: sp.GetRequiredService<IDynamicPeriodDriverService>()));

        // The Screener reads metadata through the read-only provider: the real provider's GetMetadataAsync can write.
        services.AddSingleton<IScreenerService>(sp => new ScreenerService(
            sp.GetRequiredService<IDataService>(),
            sp.GetRequiredService<IStockAnalyzerSettings>(),
            new IMarketDataProvider[] { new ReadOnlyMetadataMarketDataProvider(sp.GetRequiredService<IReadOnlyMarketDataSource>()) },
            sp.GetService<ILogger<ScreenerService>>()));

        services.AddSingleton<IScreenerTemplateEvaluator>(sp => new ScreenerTemplateEvaluator(sp.GetRequiredService<IScreenerService>()));

        // Templates are read from their folder as it is (never created); the folder is the application's own template folder.
        services.AddSingleton<IReadOnlyTemplateSource>(_ => new FileReadOnlyTemplateSource(
            PathDiscovery.ResolveTemplatesDirectoryWithoutCreating(TemplateType.Screener),
            PathDiscovery.ResolveTemplatesDirectoryWithoutCreating(TemplateType.BacktestCondition)));

        // Workspace settings (watchlists, portfolios, custom categories, filters) are read without creating.
        services.AddSingleton<IReadOnlyWorkspaceSource>(_ => new FileReadOnlyWorkspaceSource(
            PathDiscovery.ResolveConfigPathWithoutCreating(LayoutConstants.DefaultWorkspaceFileName)));

        // The backtest path of the Backtest window: preparation (reads stored bars), engine, report generator, evaluation. All pure in-memory
        // code (the engine and the report use memory streams only); exporters, run history and the configuration managers are not registered.
        services.AddSingleton<ITimeFrameManager>(sp => new TimeFrameManager(sp.GetRequiredService<IDataService>()));
        services.AddSingleton<IBacktestEngine, BacktestEngine>();
        services.AddSingleton<BacktestReportGenerator>();
        services.AddSingleton<IQualifiedBacktestReportGenerator>(sp => sp.GetRequiredService<BacktestReportGenerator>());
        services.AddSingleton<ISamplingEvidenceTrustPolicy, NoSamplingEvidenceTrustPolicy>();
        services.AddSingleton<IBacktestEvaluationService, BacktestEvaluationService>();
        services.AddSingleton<IBacktestRunPreparationService>(sp => new BacktestRunPreparationService(
            new AggregatingDataService(
                sp.GetRequiredService<IDataService>(),
                sp.GetRequiredService<ITimeFrameManager>()),
            sp.GetService<IOptions<MarketDataSettings>>()));
        services.AddSingleton<IChartSeriesCalculationService, ChartSeriesCalculationService>();

        services.AddSingleton<IReadOnlyStockAnalyzerGateway, ReadOnlyStockAnalyzerGateway>();
        services.AddSingleton<ITickerGroupResolver, TickerGroupResolver>();
        return services;
    }
}
