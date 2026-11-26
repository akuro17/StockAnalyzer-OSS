using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Prompts;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Resources;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.McpServer.Tools.Discussion;

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
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(ConfigurationFileName, optional: true, reloadOnChange: false);

        string userSettingsPath = PathDiscovery.ResolveConfigPathWithoutCreating(Core.Models.Settings.McpServerSettings.UserConfigFileName);
        if (File.Exists(userSettingsPath))
        {
            builder.AddJsonFile(userSettingsPath, optional: true, reloadOnChange: false);
        }

        return builder.Build();
    }

    /// <summary>
    /// The "RlPortfolio" section (defaults when absent), checked at start-up against the same limits as the GUI. A value outside the standard band runs and is
    /// logged; one outside the hard band stops the start-up.
    /// </summary>
    /// <exception cref="ExecutionLimitsException">A value is outside its hard band.</exception>
    private static Core.Models.Settings.RlPortfolioSettings ReadPortfolioSettings(IConfiguration configuration, LogSink log)
    {
        var settings = configuration.GetSection(Core.Models.Settings.RlPortfolioSettings.SectionName).Get<Core.Models.Settings.RlPortfolioSettings>()
            ?? new Core.Models.Settings.RlPortfolioSettings();
        try
        {
            foreach (string warning in Core.Models.Settings.RlPortfolioSettingsValidator.Validate(
                settings, Core.Models.ReinforcementLearning.RlTrainingValidation.FreezeLimits(continuous: true)))
            {
                log.Info(warning);
            }
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new ExecutionLimitsException($"{Core.Models.Settings.RlPortfolioSettings.SectionName}:{error.ParamName} = {error.ActualValue}: {error.Message.ReplaceLineEndings(" ")}");
        }

        return settings;
    }

    /// <exception cref="McpLimitsException">A configured limit is outside its hard band.</exception>
    /// <exception cref="ExecutionLimitsException">A configured execution setting is outside its band (only when <paramref name="enableExecution"/>), or a configured <c>RlPortfolio</c> value is outside its hard band.</exception>
    /// <param name="templatesDirectory">Only for hosts and tests that keep the Screener templates elsewhere; null reads the application's own template folder.</param>
    /// <param name="backtestTemplatesDirectory">The same for the backtest-condition templates. When only <paramref name="templatesDirectory"/> is given, there are no backtest templates (the application's folder is not read).</param>
    /// <param name="enableExecution">Offer the execution tools (<c>--enable-execution</c>); without it they are neither registered nor built, and the execution sandbox folder is never touched (sa_check_rl_playback_preflight alone may create a transient RlReplay working folder below the operating system's temporary folder).</param>
    /// <param name="executionGateway">Only for tests: the gateway the execution tools use instead of the real one.</param>
    /// <param name="workspaceSource">Only for tests: the workspace source used instead of reading the application's default workspace file.</param>
    /// <param name="rlRegistry">Only for tests: the RL model registry used instead of the application's default registry.</param>
    /// <param name="portfolioSource">Only for tests: the portfolio source used instead of reading the application's saved portfolio files.</param>
    /// <param name="noteSource">Only for tests: the note source used instead of opening the application's notes database.</param>
    /// <param name="registeredIndicatorSource">Only for tests: the registered indicator source used instead of reading the application's source indicator and dynamic period driver files.</param>
    /// <param name="drawingSource">Only for tests: the drawing document source used instead of reading the application's Data/Drawings files.</param>
    /// <param name="gatewayWrapper">Only for tests: wraps the read-only gateway every data tool uses (a decorator that forwards the calls and changes a result).</param>
    /// <param name="enableDiscussion">Offer the discussion tools (<c>--enable-discussion</c>) and record every tool call in the command log; without it neither exists and the discussion settings are never read. The board itself is built when the first discussion tool is called.</param>
    /// <param name="discussionGateway">Only for tests: the gateway the discussion tools use instead of the real board.</param>
    /// <param name="timeProvider">Only for tests: the clock of the board and the command log.</param>
    /// <param name="commandLog">Only for tests: the command log instead of the real one.</param>
    /// <param name="toolsets">Optional CLI <c>--toolset</c> profile names restricting registered read-only tools; null registers all.</param>
    /// <param name="responseFormat">Selects whether <c>tools/call</c> emits <c>structuredContent</c> (<see cref="McpResponseFormat.Dual"/>) or omits it (<see cref="McpResponseFormat.TextOnly"/>).</param>
    /// <param name="compactDefaults">When true (<c>--compact-defaults</c>), allowlisted tools default to compact/rounded outputs when callers omit formatting parameters.</param>
    /// <param name="progressiveDiscovery">When true (<c>--progressive-discovery</c>), <c>tools/list</c> exposes only <c>sa_search_tools</c> and <c>sa_invoke_tool</c> (plus <c>sa_invoke_action</c> when non-read-only tools are active) while keeping all active tools callable on demand.</param>
    public static McpServerRuntime Create(
        IConfiguration configuration,
        LogSink log,
        string? templatesDirectory = null,
        string? backtestTemplatesDirectory = null,
        bool enableExecution = false,
        ISandboxedExecutionGateway? executionGateway = null,
        IReadOnlyWorkspaceSource? workspaceSource = null,
        IRlModelGenerationRegistry? rlRegistry = null,
        Func<IReadOnlyStockAnalyzerGateway, IReadOnlyStockAnalyzerGateway>? gatewayWrapper = null,
        bool enableDiscussion = false,
        IDiscussionGateway? discussionGateway = null,
        TimeProvider? timeProvider = null,
        ICommandLog? commandLog = null,
        IReadOnlyPortfolioSource? portfolioSource = null,
        IReadOnlyNoteSource? noteSource = null,
        IReadOnlyRegisteredIndicatorSource? registeredIndicatorSource = null,
        IReadOnlyDrawingDocumentSource? drawingSource = null,
        IReadOnlyList<string>? toolsets = null,
        McpResponseFormat responseFormat = McpResponseFormat.Dual,
        bool compactDefaults = false,
        bool progressiveDiscovery = false)
    {
        McpLimits limits = configuration.GetSection(McpLimits.SectionName).Get<McpLimits>() ?? new McpLimits();
        McpLimitsContract.Validate(limits);
        Core.Models.Settings.RlPortfolioSettings portfolioSettings = ReadPortfolioSettings(configuration, log);

        var collection = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration);
        // The portfolio preflight and the portfolio training run of the execution gateway use the settings the GUI reads from the same section.
        collection.AddSingleton(portfolioSettings);
        if (enableExecution)
        {
            // The gateway's information log (job ids and states) goes to standard error; the orchestrator's own log is not routed anywhere.
            collection.AddSingleton<ILogger<SandboxedExecutionGateway>>(new LogSinkLogger<SandboxedExecutionGateway>(log));
            collection.AddStockAnalyzerSandboxedExecution(configuration);
        }

        if (enableDiscussion)
        {
            // Test replacements are registered first: the registration below only fills what is still missing.
            if (timeProvider is not null)
            {
                collection.AddSingleton(timeProvider);
            }

            if (commandLog is not null)
            {
                collection.AddSingleton(commandLog);
            }

            if (discussionGateway is not null)
            {
                collection.AddSingleton(discussionGateway);
            }

            collection.AddStockAnalyzerDiscussion(configuration, log.Info, log.Error, McpDisclaimerConstants.ResearchNotAdvice);
        }

        // The portfolio preflight reads the same configured market-data folders as the other tools (not only the fixed Data folders).
        collection.AddSingleton<IRlPortfolioService>(sp => new RlPortfolioService(
            portfolioSettings,
            sourceFolders: RlLocalSourceFolders.FromSettings(sp.GetRequiredService<IOptions<MarketDataSettings>>().Value)));

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

        if (portfolioSource is not null)
        {
            collection.Replace(ServiceDescriptor.Singleton<IReadOnlyPortfolioSource>(portfolioSource));
        }

        if (registeredIndicatorSource is not null)
        {
            collection.Replace(ServiceDescriptor.Singleton<IReadOnlyRegisteredIndicatorSource>(registeredIndicatorSource));
        }

        // The saved drawing file is read under the byte limit of the contract (the registration of the Core library has the library default).
        // A factory, as the library registration is: the folder is resolved when the source is first needed, not while the composition is built.
        collection.Replace(ServiceDescriptor.Singleton<IReadOnlyDrawingDocumentSource>(_ => drawingSource ?? FileReadOnlyDrawingDocumentSource.ForApplicationData(limits.MaxDrawingFileBytes)));

        if (noteSource is not null)
        {
            collection.Replace(ServiceDescriptor.Singleton<IReadOnlyNoteSource>(noteSource));
        }

        if (rlRegistry is not null)
        {
            collection.Replace(ServiceDescriptor.Singleton<IRlModelGenerationRegistry>(rlRegistry));
        }

        ServiceProvider services = collection.BuildServiceProvider();
        try
        {
            ReadOnlyDataTree tree = services.GetRequiredService<ReadOnlyDataTree>();
            if (!tree.IsComplete)
            {
                log.Info($"The data folder is incomplete ({string.Join(", ", tree.MissingItems)}); data tools return empty results until it is complete.");
            }

            MarketDataSettings marketData = services.GetRequiredService<IOptions<MarketDataSettings>>().Value;
            var sanitizer = new ErrorMessageSanitizer(KnownRoots(marketData));
            // The replay tools read the same configured market-data folders as the other data tools (not only the fixed Data folders).
            RlLocalSourceFolders rlSourceFolders = RlLocalSourceFolders.FromSettings(marketData);
            IReadOnlyStockAnalyzerGateway gateway = services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
            if (gatewayWrapper is not null)
            {
                gateway = gatewayWrapper(gateway);
            }

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
            var templateWriter = new TemplateConditionWriter(catalog, screenerCatalog);
            var templateBuilder = new TemplateStructureBuilder(gateway, screenerBinder, backtestBinder, templateAdvisor, templateWriter, settings);
            var resolvedWorkspaceSource = services.GetRequiredService<IReadOnlyWorkspaceSource>();
            var groupResolver = services.GetService<ITickerGroupResolver>() ?? new TickerGroupResolver(gateway, resolvedWorkspaceSource);
            SandboxedExecutionSettings? executionSettings = enableExecution ? services.GetRequiredService<SandboxedExecutionSettings>() : null;
            if (executionSettings is not null && executionSettings.MaxPredictionBars > limits.MaxCandlesPerRequest)
            {
                throw new ExecutionLimitsException(
                    $"{SandboxedExecutionSettings.SectionName}:{nameof(SandboxedExecutionSettings.MaxPredictionBars)} is {executionSettings.MaxPredictionBars}; it must be at most {McpLimits.SectionName}:{nameof(McpLimits.MaxCandlesPerRequest)} ({limits.MaxCandlesPerRequest}).");
            }

            DiscussionSettings? discussionSettings = enableDiscussion ? services.GetRequiredService<DiscussionSettings>() : null;
            if (discussionSettings is not null)
            {
                DiscussionSettingsValidator.ValidateAgainstHost(discussionSettings, limits.MaxResponseBytes, limits.ToolTimeoutSeconds);
            }

            var capabilitiesTool = new GetCapabilitiesTool(catalog, screenerCatalog, executionSettings, compactDefaults);
            var dataIdentity = new InstanceDataIdentity(gateway, marketData.DailyDataPath);
            var settingsTool = new GetSettingsTool(settings, dataIdentity);
            var modelRegistry = services.GetService<IRlModelGenerationRegistry>() as ReadOnlyModelGenerationRegistry ?? new ReadOnlyModelGenerationRegistry();

            var calculateIndicators = new CalculateIndicatorsTool(gateway, symbols, catalog, indicatorBinder, new IndicatorChainPlan(factory), sanitizer, limits, compactDefaults);
            var activeWorkspaceTool = new GetActiveWorkspaceTool(resolvedWorkspaceSource, services.GetRequiredService<IReadOnlyRegisteredIndicatorSource>(), catalog, limits);
            var resultHandles = new McpResultHandleStore();
            var tools = new List<IMcpTool>
            {
                new ListTickersTool(gateway, groupResolver, limits),
                new ListTickerGroupsTool(gateway, resolvedWorkspaceSource, limits),
                new FilterTickersTool(gateway, groupResolver, screenerBinder, limits, resultHandles),
                new GetCandlesTool(gateway, symbols, limits, compactDefaults),
                new GetTickerMetadataTool(gateway, limits, resultHandles),
                capabilitiesTool,
                settingsTool,
                calculateIndicators,
                new CalculateIndicatorsBatchTool(calculateIndicators, symbols, groupResolver, sanitizer, limits, resultHandles, compactDefaults),
                new GetPortfolioSummaryTool(services.GetRequiredService<IReadOnlyPortfolioSource>(), gateway, limits),
                new AnalyzePortfolioRiskTool(services.GetRequiredService<IReadOnlyPortfolioSource>(), gateway, symbols, limits, compactDefaults),
                new SearchNotesTool(services.GetRequiredService<IReadOnlyNoteSource>(), limits, compactDefaults),
                new ListUpcomingCatalystsTool(services.GetRequiredService<IReadOnlyNoteSource>(), limits, timeProvider),
                activeWorkspaceTool,
                new RunScreenerTool(gateway, groupResolver, screenerBinder, ScreenerValueExtractor.Default, templateAdvisor, settings, limits, resultHandles),
                new ListTemplatesTool(gateway, templateAdvisor, backtestBinder),
                new RunBacktestTool(gateway, symbols, backtestBinder, limits, compactDefaults),
                new ExtractSignalEventsTool(gateway, symbols, backtestBinder, factory, limits, compactDefaults),
                new DetectDivergencesTool(gateway, symbols, catalog, indicatorBinder, factory, limits),
                new OptimizeBacktestTool(gateway, symbols, backtestBinder, limits, compactDefaults),
                new RunWalkForwardTool(gateway, symbols, backtestBinder, limits, compactDefaults),
                new RunPortfolioBacktestTool(gateway, groupResolver, symbols, backtestBinder, limits, resultHandles, compactDefaults),
                new CalculateReverseWatchTool(gateway, symbols, limits),
                new CalculateChartSeriesTool(gateway, symbols, limits, compactDefaults),
                new RenderChartImageTool(gateway, symbols, catalog, indicatorBinder, new IndicatorChainPlan(factory), limits, services.GetRequiredService<IReadOnlyDrawingDocumentSource>(), compactDefaults),
                new CalculateVolumeProfileTool(gateway, symbols, limits, compactDefaults),
                new AnalyzeMarketStructureTool(gateway, symbols, limits),
                new FindSupportResistanceTool(gateway, symbols, limits),
                new CalculateDrawingValuesTool(gateway, symbols, limits),
                new GetChartDrawingsTool(services.GetRequiredService<IReadOnlyDrawingDocumentSource>(), gateway, symbols, limits),
                new DetectCandlePatternsTool(gateway, symbols, limits),
                new DetectChartPatternsTool(gateway, symbols, limits),
                new AnalyzeCyclesTool(gateway, symbols, limits),
                new ProjectPricePathTool(gateway, symbols, limits, compactDefaults),
                new GetSeasonalityTool(gateway, symbols, limits, compactDefaults),
                new CalculateRelativePerformanceTool(gateway, symbols, sanitizer, limits, compactDefaults),
                new CalculateCrossAssetStatsTool(gateway, symbols, groupResolver, limits, resultHandles, compactDefaults),
                new CalculateMarketBreadthTool(gateway, symbols, groupResolver, limits, resultHandles),
                new CheckPortfolioRlPreflightTool(symbols, services.GetService<IRlPortfolioService>(), gateway, sanitizer),
                new CheckRlPlaybackPreflightTool(symbols, services.GetService<IRlModelGenerationRegistry>(), sourceFolders: rlSourceFolders, sanitizer: sanitizer, gateway: gateway),
                new ListModelsTool(symbols, modelRegistry, limits),
                new GetModelExplanationTool(gateway, symbols, modelRegistry),
                new GetSymbolCoverageTool(gateway, limits, dataIdentity),
                new ValidateConditionTreeTool(gateway, screenerBinder, backtestBinder, limits),
                new GetTemplateDetailTool(gateway, templateWriter),
                new BuildTemplateTool(templateBuilder),
                new EvaluateExpressionsTool(limits),
                new ListResultHandlesTool(resultHandles),
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
                tools.Add(new ListTrainingJobsTool(execution, sanitizer));
                tools.Add(new StartRlTrainingTool(execution, symbols));
                tools.Add(new StartPortfolioRlTrainingTool(execution, symbols));
                tools.Add(new RunOnnxPredictionTool(execution, gateway, symbols, new MLDataProcessor(), limits, executionSettings));
                tools.Add(new GetRlPlaybackTool(symbols, services.GetService<IRlModelGenerationRegistry>(), sourceFolders: rlSourceFolders));
                tools.Add(new GetModelAttentionTool(gateway, symbols, settings: settings, registry: modelRegistry, limits: limits, sanitizer: sanitizer));
            }

            ClientIdentity? identity = null;
            IToolCallObserver? observer = null;
            McpToolRegistry? registryOfTools = null; // the analysis snapshot tool looks tools up by name, and is itself one of them
            if (discussionSettings is not null)
            {
                identity = new ClientIdentity();
                observer = new CommandLogObserver(services.GetRequiredService<ICommandLog>());
                IDiscussionGateway board = new DeferredDiscussionGateway(services);
                tools.Add(new DiscussionCreateTool(board, identity));
                tools.Add(new DiscussionJoinTool(board, identity));
                tools.Add(new DiscussionLeaveTool(board, identity));
                tools.Add(new DiscussionPostTool(board, identity, discussionSettings));
                tools.Add(new DiscussionReadTool(board, identity, discussionSettings, compactDefaults));
                tools.Add(new DiscussionStatusTool(board, identity));
                tools.Add(new DiscussionCloseTool(board, identity));
                tools.Add(new DiscussionAttachAnalysisTool(board, identity, () => registryOfTools!, progressiveDiscovery));
                tools.Add(new DiscussionGetAnalysisTool(board, identity));
            }

            List<IMcpTool> activeTools = McpToolsetCatalog.Filter(tools, toolsets);
            McpToolRegistry registry;
            if (!progressiveDiscovery)
            {
                registry = new McpToolRegistry(activeTools);
            }
            else
            {
                var profilesByName = new Dictionary<string, string>(activeTools.Count, StringComparer.Ordinal);
                foreach (IMcpTool active in activeTools)
                {
                    profilesByName[active.Name] = McpToolsetCatalog.ProfileOf(active);
                }

                bool hasNonReadOnly = activeTools.Any(t => !t.IsReadOnly);
                var searchTool = new SearchToolsTool(() => registryOfTools!, profilesByName, responseFormat, hasNonReadOnly);
                var invokeReadOnlyTool = new InvokeToolProxyTool(readOnlyOnly: true);
                IMcpTool[] metaTools = hasNonReadOnly
                    ? new IMcpTool[] { invokeReadOnlyTool, new InvokeToolProxyTool(readOnlyOnly: false), searchTool }
                    : new IMcpTool[] { invokeReadOnlyTool, searchTool };
                registry = new McpToolRegistry(metaTools, activeTools.Concat(metaTools));
            }

            registryOfTools = registry;
            string toolDocPrefix = ToolJson.ToolDocPrefix;
            var resources = new McpResourceRegistry(
                new[]
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
                        cancellationToken => settingsTool.BuildAsync(null, cancellationToken)),
                    new McpResource(
                        GetActiveWorkspaceTool.ResourceUri,
                        "Active workspace",
                        "The chart set-up the user last saved (symbol, time frame, active indicators) and the same indicators as an sa_calculate_indicators request; the same content as sa_get_active_workspace. Unsaved edits are not included.",
                        McpResourceRegistry.JsonMimeType,
                        cancellationToken => ReadActiveWorkspaceAsync(activeWorkspaceTool, cancellationToken)),
                },
                new[]
                {
                    new McpResourceTemplate(
                        $"{toolDocPrefix}{{name}}",
                        "Tool documentation",
                        "Full Markdown reference for one active MCP tool.",
                        McpResourceRegistry.MarkdownMimeType,
                        uri => ResolveToolDocumentationResource(uri, toolDocPrefix, registry, limits.MaxResponseBytes)),
                });
            var dispatcher = new McpMessageDispatcher(
                log,
                registry,
                new ToolExecutor(limits, sanitizer, log, observer, responseFormat, registry, progressiveDiscovery),
                resources,
                identity,
                new McpPromptRegistry(limits, registry, progressiveDiscovery));
            return new McpServerRuntime(services, limits, dispatcher, registry, prepareExecution);
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    /// <summary>The workspace document for resources/read: a document that cannot be read now is a protocol error the client can retry, never a stale or empty document.</summary>
    private static async Task<string> ReadActiveWorkspaceAsync(GetActiveWorkspaceTool tool, CancellationToken cancellationToken)
    {
        try
        {
            return await tool.BuildAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ToolException error) when (error.Code == ToolErrorCodes.DataUnavailable)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InternalError, error.Message);
        }
    }

    private static McpResource? ResolveToolDocumentationResource(string uri, string prefix, McpToolRegistry registry, int maxResponseBytes)
    {
        if (!uri.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rawName = uri[prefix.Length..];
        if (string.IsNullOrEmpty(rawName)
            || rawName.IndexOfAny(new[] { '/', '\\' }) >= 0
            || rawName.Contains("..", StringComparison.Ordinal)
            || rawName.Any(char.IsWhiteSpace)
            || !registry.TryGetCallable(rawName, out RegisteredTool? registered))
        {
            return null;
        }

        string toolsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "docs", "MCP", "tools"));
        string docPath = Path.GetFullPath(Path.Combine(toolsDir, $"{registered.Tool.Name}.md"));
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!docPath.StartsWith(toolsDir + Path.DirectorySeparatorChar, pathComparison) || !File.Exists(docPath))
        {
            return null;
        }

        return new McpResource(
            uri,
            registered.Tool.Name,
            $"Full Markdown reference for {registered.Tool.Name}.",
            McpResourceRegistry.MarkdownMimeType,
            async cancellationToken =>
            {
                string text = await File.ReadAllTextAsync(docPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                if (Encoding.UTF8.GetByteCount(text) > maxResponseBytes)
                {
                    throw new JsonRpcException(JsonRpcErrorCodes.InternalError, "Resource exceeds response byte limit.");
                }

                return text;
            });
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
