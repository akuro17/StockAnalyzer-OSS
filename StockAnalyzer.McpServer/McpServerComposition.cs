using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Resources;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer;

/// <summary>Everything the host needs, built once at start-up; disposing it releases the services behind the gateway.</summary>
internal sealed class McpServerRuntime : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly Action? _prepareExecution;

    public McpServerRuntime(ServiceProvider services, McpLimits limits, McpMessageDispatcher dispatcher, McpToolRegistry tools, Action? prepareExecution = null)
    {
        _services = services;
        Limits = limits;
        Dispatcher = dispatcher;
        Tools = tools;
        _prepareExecution = prepareExecution;
    }

    /// <summary>
    /// Builds the execution gateway (start-up clean-up of old sandbox sessions) when the execution tools are on; nothing otherwise. A server run calls it
    /// before the first request; the help command never does.
    /// </summary>
    public void PrepareExecution() => _prepareExecution?.Invoke();

    public McpLimits Limits { get; }

    /// <summary>The tools the dispatcher offers; the help command lists them.</summary>
    public McpToolRegistry Tools { get; }

    public McpMessageDispatcher Dispatcher { get; }

    public void Dispose() => _services.Dispose();
}

/// <summary>The composition root of the server: configuration, limits, the read-only gateway, the tools.</summary>
internal static class McpServerComposition
{
    public const string ConfigurationFileName = "appsettings.json";

    /// <summary>The same file the GUI reads, next to the executable; it is optional, every value has a default.</summary>
    public static IConfiguration LoadConfiguration(string baseDirectory)
        => new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(ConfigurationFileName, optional: true, reloadOnChange: false)
            .Build();

    /// <exception cref="McpLimitsException">A configured limit is outside its hard band.</exception>
    /// <exception cref="ExecutionLimitsException">A configured execution setting is outside its band (only when <paramref name="enableExecution"/>).</exception>
    /// <param name="templatesDirectory">Only for hosts and tests that keep the Screener templates elsewhere; null reads the application's own template folder.</param>
    /// <param name="backtestTemplatesDirectory">The same for the backtest-condition templates. When only <paramref name="templatesDirectory"/> is given, there are no backtest templates (the application's folder is not read).</param>
    /// <param name="enableExecution">Offer the execution tools (<c>--enable-execution</c>); without it they are neither registered nor built, and the sandbox folder is never touched.</param>
    /// <param name="executionGateway">Only for tests: the gateway the execution tools use instead of the real one.</param>
    /// <param name="workspaceSource">Only for tests: the workspace source used instead of reading the application's default workspace file.</param>
    public static McpServerRuntime Create(
        IConfiguration configuration,
        LogSink log,
        string? templatesDirectory = null,
        string? backtestTemplatesDirectory = null,
        bool enableExecution = false,
        ISandboxedExecutionGateway? executionGateway = null,
        IReadOnlyWorkspaceSource? workspaceSource = null)
    {
        McpLimits limits = configuration.GetSection(McpLimits.SectionName).Get<McpLimits>() ?? new McpLimits();
        McpLimitsContract.Validate(limits);

        var collection = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration);
        if (enableExecution)
        {
            // The gateway's information log (job ids and states) goes to standard error; the orchestrator's own log is not routed anywhere.
            collection.AddSingleton<ILogger<SandboxedExecutionGateway>>(new LogSinkLogger<SandboxedExecutionGateway>(log));
            collection.AddStockAnalyzerSandboxedExecution(configuration);
        }

        if (templatesDirectory is not null || backtestTemplatesDirectory is not null)
        {
            collection.Replace(ServiceDescriptor.Singleton<IReadOnlyTemplateSource>(new FileReadOnlyTemplateSource(
                templatesDirectory ?? PathDiscovery.ResolveTemplatesDirectoryWithoutCreating(TemplateType.Screener),
                backtestTemplatesDirectory ?? (templatesDirectory is null ? PathDiscovery.ResolveTemplatesDirectoryWithoutCreating(TemplateType.BacktestCondition) : null))));
        }

        if (workspaceSource is not null)
        {
            collection.Replace(ServiceDescriptor.Singleton<IReadOnlyWorkspaceSource>(workspaceSource));
        }

        ServiceProvider services = collection.BuildServiceProvider();
        try
        {
            ReadOnlyDataTree tree = services.GetRequiredService<ReadOnlyDataTree>();
            if (!tree.IsComplete)
            {
                log.Info($"The data folder is incomplete ({string.Join(", ", tree.MissingItems)}); data tools return empty results until it is complete.");
            }

            var sanitizer = new ErrorMessageSanitizer(KnownRoots(services.GetRequiredService<IOptions<MarketDataSettings>>().Value));
            var gateway = services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
            var factory = services.GetRequiredService<IIndicatorFactory>();
            var catalogProvider = new ScreenerCatalogProvider();
            var catalog = new IndicatorCatalog(factory, catalogProvider);
            var symbols = new SymbolResolver(gateway);
            var settings = services.GetRequiredService<IStockAnalyzerSettings>();
            var screenerCatalog = new ScreenerCatalog(catalogProvider, factory);
            var indicatorBinder = new IndicatorSettingsBinder(catalogProvider, factory);
            var screenerBinder = new ScreenerConditionBinder(catalog, indicatorBinder, screenerCatalog, settings);
            var backtestBinder = new BacktestConditionBinder(catalog, indicatorBinder, settings);
            var templateAdvisor = new ScreenerTemplateAdvisor(catalog);
            var resolvedWorkspaceSource = services.GetRequiredService<IReadOnlyWorkspaceSource>();
            var groupResolver = services.GetService<ITickerGroupResolver>() ?? new TickerGroupResolver(gateway, resolvedWorkspaceSource);
            SandboxedExecutionSettings? executionSettings = enableExecution ? services.GetRequiredService<SandboxedExecutionSettings>() : null;
            if (executionSettings is not null && executionSettings.MaxPredictionBars > limits.MaxCandlesPerRequest)
            {
                throw new ExecutionLimitsException(
                    $"{SandboxedExecutionSettings.SectionName}:{nameof(SandboxedExecutionSettings.MaxPredictionBars)} is {executionSettings.MaxPredictionBars}; it must be at most {McpLimits.SectionName}:{nameof(McpLimits.MaxCandlesPerRequest)} ({limits.MaxCandlesPerRequest}).");
            }

            var capabilitiesTool = new GetCapabilitiesTool(catalog, screenerCatalog, executionSettings);
            var settingsTool = new GetSettingsTool(settings);
            var tools = new List<IMcpTool>
            {
                new ListTickersTool(gateway, groupResolver, limits),
                new ListTickerGroupsTool(gateway, resolvedWorkspaceSource, limits),
                new GetCandlesTool(gateway, symbols, limits),
                new GetTickerMetadataTool(gateway, limits),
                capabilitiesTool,
                settingsTool,
                new CalculateIndicatorsTool(gateway, symbols, catalog, indicatorBinder, new IndicatorChainPlan(factory), sanitizer, limits),
                new RunScreenerTool(gateway, groupResolver, screenerBinder, ScreenerValueExtractor.Default, templateAdvisor, settings, limits),
                new ListTemplatesTool(gateway, templateAdvisor, backtestBinder),
                new RunBacktestTool(gateway, symbols, backtestBinder, limits),
                new CalculateReverseWatchTool(gateway, symbols, limits),
                new CalculateChartSeriesTool(gateway, symbols, limits),
            };

            Action? prepareExecution = null;
            if (executionSettings is not null)
            {
                ISandboxedExecutionGateway execution;
                if (executionGateway is not null)
                {
                    execution = executionGateway;
                }
                else
                {
                    var deferred = new DeferredSandboxedExecutionGateway(services);
                    prepareExecution = deferred.Prepare;
                    execution = deferred;
                }

                tools.Add(new StartTrainingTool(execution, symbols, limits));
                tools.Add(new GetTrainingStatusTool(execution, sanitizer));
                tools.Add(new CancelTrainingTool(execution));
                tools.Add(new StartRlTrainingTool(execution, symbols));
                tools.Add(new RunOnnxPredictionTool(execution, gateway, symbols, new MLDataProcessor(), limits, executionSettings));
            }

            var resources = new McpResourceRegistry(new[]
            {
                new McpResource(
                    $"{McpResourceRegistry.UriScheme}catalog/indicators",
                    "Indicator catalogue",
                    "Every indicator type with its parameters (default, range), output series and flags; the same content as sa_get_capabilities with includeDetails, except its execution block.",
                    McpResourceRegistry.JsonMimeType,
                    capabilitiesTool.IndicatorCatalogJson),
                new McpResource(
                    $"{McpResourceRegistry.UriScheme}catalog/screener",
                    "Screener catalogue",
                    "The columns, patterns, price sources and operators sa_run_screener accepts, and the columns it cannot read.",
                    McpResourceRegistry.JsonMimeType,
                    capabilitiesTool.ScreenerCatalogJson),
                new McpResource(
                    $"{McpResourceRegistry.UriScheme}settings/current",
                    "Current settings",
                    "The allow-listed application settings (limits and defaults); the same content as sa_get_settings. File locations and credentials are never included.",
                    McpResourceRegistry.JsonMimeType,
                    () => settingsTool.Build()),
            });

            var registry = new McpToolRegistry(tools);
            var dispatcher = new McpMessageDispatcher(log, registry, new ToolExecutor(limits, sanitizer, log), resources);
            return new McpServerRuntime(services, limits, dispatcher, registry, prepareExecution);
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    private static IEnumerable<string?> KnownRoots(MarketDataSettings settings)
    {
        yield return AppContext.BaseDirectory;
        yield return Environment.CurrentDirectory;
        yield return Path.GetTempPath();
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return PathDiscovery.ResolveDataPathWithoutCreating(null, "Data");
        yield return settings.DailyDataPath;
        yield return settings.WeeklyDataPath;
        yield return settings.MonthlyDataPath;
        yield return settings.MetadataPath;
        yield return settings.TickerListPath;
    }
}
