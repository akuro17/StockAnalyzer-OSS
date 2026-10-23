using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Single place that binds the configuration sections read by <see cref="StockAnalyzerSettings"/> and
/// <see cref="MarketDataSettings"/>. Shared by the GUI and the read-only composition so the section names exist once.
/// </summary>
public static class StockAnalyzerOptionsServiceCollectionExtensions
{
    public static IServiceCollection AddStockAnalyzerSettingsOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions();
        services.Configure<PythonSettings>(configuration.GetSection("Python"));
        services.Configure<ChartDefaultSettings>(configuration.GetSection("Chart"));
        services.Configure<PredictionSettings>(configuration.GetSection("Prediction"));
        services.Configure<MarketDataSettings>(configuration.GetSection("MarketDataSettings"));
        services.Configure<ScreenerSettings>(configuration.GetSection("Screener"));
        services.Configure<ScatterMetricSettings>(configuration.GetSection(ScatterMetricSettingsValidator.SectionName));
        services.Configure<SmartScreenerSettings>(configuration.GetSection("SmartScreener"));
        services.Configure<InfrastructureSettings>(configuration.GetSection("Infrastructure"));
        services.Configure<MarketStructureSettings>(configuration.GetSection("MarketStructure"));
        services.Configure<PatternRecognitionSettings>(configuration.GetSection("PatternRecognition"));
        services.Configure<ResilienceSettings>(configuration.GetSection("Resilience"));
        services.Configure<LocalizationSettings>(configuration.GetSection("Localization"));
        services.Configure<BacktestSettings>(configuration.GetSection("Backtest"));
        services.Configure<PythonIpcSettings>(configuration.GetSection("PythonIpc"));
        services.Configure<PythonPackageCheckSettings>(configuration.GetSection(PythonPackageCheckSettings.SectionName));
        services.Configure<DrawingInteractionSettings>(configuration.GetSection("DrawingInteraction"));
        return services;
    }
}
