using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_check_rl_playback_preflight: inspects feasibility and anchor date range for reinforcement learning playback.</summary>
internal sealed class CheckRlPlaybackPreflightTool : IMcpTool
{
    private const string ModelIdArgument = "modelId";
    private const string SymbolArgument = "symbol";

    private readonly SymbolResolver _symbols;
    private readonly IRlModelGenerationRegistry _registry;
    private readonly RlDialogDataService? _dataService;
    private readonly RlLocalSourceFolders? _sourceFolders;

    public CheckRlPlaybackPreflightTool(
        SymbolResolver symbols,
        IRlModelGenerationRegistry? registry = null,
        RlDialogDataService? dataService = null,
        RlLocalSourceFolders? sourceFolders = null)
    {
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _registry = registry ?? new ReadOnlyModelGenerationRegistry();
        _dataService = dataService;
        _sourceFolders = sourceFolders;
    }

    private RlDialogDataService GetDataService()
        => _dataService ?? new RlDialogDataService(_registry, new QValueService(_registry, dataRoot: EphemeralSandbox.Root, sourceFolders: _sourceFolders), EphemeralSandbox.Root, _sourceFolders);

    public string Name => "sa_check_rl_playback_preflight";

    public string Description =>
        "Checks whether a registered reinforcement learning model can be replayed on a symbol's stored daily bars, and reports the valid anchor date range ([earliestAnchorDate, latestAnchorDate]), " +
        "episode horizon (128 bars), observation window (64 bars), and model metadata. " +
        "Never writes the Data tree or the model registry; it may create and delete a transient working folder under the operating system's temporary folder while it reads the stored bars.";

    public bool IsHeavy => false;

    public bool IsExecution => false;

    public string? ExampleArgumentsJson => "{\"modelId\":\"active\",\"symbol\":\"7203-T\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(ModelIdArgument, "The identifier of a registered RL model generation, or 'active' for the current active RL model.", required: true)
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable. Default: the model's trained symbol.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string? modelIdArg = ToolArgs.String(arguments, ModelIdArgument);
        if (string.IsNullOrWhiteSpace(modelIdArg))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ModelIdArgument}' is required.", ModelIdArgument);
        }

        string? rawSymbol = ToolArgs.String(arguments, SymbolArgument);
        string? resolvedSymbol = null;
        if (rawSymbol is not null)
        {
            resolvedSymbol = await _symbols.ResolveAsync(rawSymbol, SymbolArgument, cancellationToken).ConfigureAwait(false);
        }

        string resolvedModelId = modelIdArg;
        if (string.Equals(modelIdArg, "active", StringComparison.OrdinalIgnoreCase))
        {
            if (_registry.RlActiveId is { } activeId)
            {
                resolvedModelId = activeId;
            }
            else
            {
                return BuildReplayReport(modelIdArg, resolvedSymbol ?? "", null, null, canReplay: false, "No RL model is currently active in the registry.", null, null);
            }
        }

        RlGeneration? generation = _registry.GetRlGeneration(resolvedModelId);
        if (generation is null)
        {
            return BuildReplayReport(modelIdArg, resolvedSymbol ?? "", null, null, canReplay: false, $"RL model '{modelIdArg}' was not found in the registry.", null, null, resolvedModelId);
        }

        string symbol = resolvedSymbol ?? generation.Symbol;

        bool canReplay = false;
        string? reason = null;
        DateOnly? earliestAnchor = null;
        DateOnly? latestAnchor = null;

        try
        {
            RlAnchorRange range = await GetDataService().GetAnchorRangeAsync(resolvedModelId, symbol, cancellationToken).ConfigureAwait(false);
            earliestAnchor = range.EarliestAnchor;
            latestAnchor = range.LatestAnchor;
            canReplay = true;
        }
        catch (RlExternalReplayException ex)
        {
            canReplay = false;
            reason = ex.Message;
            earliestAnchor = ex.EarliestAnchor;
            latestAnchor = ex.LatestAnchor;
        }
        catch (Exception ex)
        {
            canReplay = false;
            reason = ex.Message;
        }

        return BuildReplayReport(modelIdArg, symbol, generation.Symbol, generation.Target, canReplay, reason, earliestAnchor, latestAnchor, resolvedModelId);
    }

    private static string BuildReplayReport(
        string modelId,
        string symbol,
        string? trainedSymbol,
        string? targetPolicy,
        bool canReplay,
        string? reason,
        DateOnly? earliestAnchor,
        DateOnly? latestAnchor,
        string? resolvedModelId = null)
    {
        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("canReplay", canReplay);
            writer.WriteString(ModelIdArgument, modelId);
            if (resolvedModelId is not null)
            {
                writer.WriteString("resolvedModelId", resolvedModelId);
            }
            writer.WriteString("symbol", symbol);
            if (trainedSymbol is not null)
            {
                writer.WriteString("trainedSymbol", trainedSymbol);
            }
            else
            {
                writer.WriteNull("trainedSymbol");
            }

            if (targetPolicy is not null)
            {
                writer.WriteString("targetPolicy", targetPolicy);
            }
            else
            {
                writer.WriteNull("targetPolicy");
            }

            writer.WriteNumber("episodeHorizon", RlContract.EpisodeHorizon);
            writer.WriteNumber("observationWindow", RlContract.WindowLength);
            WriteOptionalDate(writer, "earliestAnchorDate", earliestAnchor);
            WriteOptionalDate(writer, "latestAnchorDate", latestAnchor);
            if (reason is not null)
            {
                writer.WriteString("reason", reason);
            }
            else
            {
                writer.WriteNull("reason");
            }

            writer.WriteEndObject();
        });
    }

    private static void WriteOptionalDate(Utf8JsonWriter writer, string name, DateOnly? date)
    {
        if (date is { } value)
        {
            writer.WriteString(name, value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
