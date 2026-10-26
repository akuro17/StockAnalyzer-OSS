using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// Registers the sandboxed execution gateway. A separate call from <see cref="ReadOnlyCoreServiceCollectionExtensions.AddStockAnalyzerReadOnlyCore"/>,
/// which stays as it is: the read-only composition never gains the ability to run anything. Call it after the read-only composition (the orchestrator
/// needs its market-data source and indicator factory). The gateway is created only when something asks for it, so a host that never does
/// never touches the sandbox folder.
/// </summary>
public static class SandboxedExecutionServiceCollectionExtensions
{
    /// <exception cref="ExecutionLimitsException">A configured execution setting is outside its band.</exception>
    public static IServiceCollection AddStockAnalyzerSandboxedExecution(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        SandboxedExecutionSettings settings = configuration.GetSection(SandboxedExecutionSettings.SectionName).Get<SandboxedExecutionSettings>()
            ?? new SandboxedExecutionSettings();
        ExecutionLimits.Validate(settings);
        services.TryAddSingleton(settings);

        services.TryAddSingleton<IPythonExecutableResolver, EmbeddedPythonExecutableResolver>();
        services.TryAddSingleton<ITrainingOrchestrator>(sp => new TrainingOrchestrator(
            UnavailablePythonService.Instance,
            new IndicatorChannelExporter(
                new ReadOnlyMetadataMarketDataProvider(sp.GetRequiredService<IReadOnlyMarketDataSource>()),
                sp.GetRequiredService<IIndicatorFactory>()),
            sp.GetService<ILogger<TrainingOrchestrator>>(),
            executableResolver: sp.GetRequiredService<IPythonExecutableResolver>()));
        services.TryAddSingleton<ISandboxedExecutionGateway>(sp => new SandboxedExecutionGateway(
            sp.GetRequiredService<ITrainingOrchestrator>(),
            sp.GetRequiredService<IPythonExecutableResolver>(),
            sp.GetRequiredService<SandboxedExecutionSettings>(),
            sp.GetService<ILogger<SandboxedExecutionGateway>>(),
            sp.GetRequiredService<IOptions<MarketDataSettings>>().Value,
            modelRegistry: sp.GetService<IModelGenerationRegistry>()));
        return services;
    }
}
