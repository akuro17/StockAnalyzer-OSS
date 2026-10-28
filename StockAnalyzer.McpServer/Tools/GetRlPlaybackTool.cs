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

/// <summary>sa_get_rl_playback: replays a registered reinforcement learning model over stored daily bars to query Q-values and action outputs.</summary>
internal sealed class GetRlPlaybackTool : IMcpTool
{
    private const string ModelIdArgument = "modelId";
    private const string SymbolArgument = "symbol";
    private const string AsOfDateArgument = "asOfDate";
    private const string DecisionPositionArgument = "decisionPosition";
    private const string PolicyIdArgument = "policyId";

    private readonly SymbolResolver _symbols;
    private readonly IRlModelGenerationRegistry _registry;
    private readonly RlDialogDataService _dataService;

    public GetRlPlaybackTool(
        SymbolResolver symbols,
        IRlModelGenerationRegistry? registry = null,
        RlDialogDataService? dataService = null,
        RlLocalSourceFolders? sourceFolders = null)
    {
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _registry = registry ?? new ReadOnlyModelGenerationRegistry();
        _dataService = dataService ?? new RlDialogDataService(_registry, new QValueService(_registry, dataRoot: EphemeralSandbox.Root, sourceFolders: sourceFolders), EphemeralSandbox.Root, sourceFolders);
    }

    public string Name => "sa_get_rl_playback";

    public string Description =>
        "Replays a registered reinforcement learning model over the stored daily bars of a symbol to evaluate action values (Q-values for Hold, Buy, Sell with discount factor 0.99), " +
        "the selected action, and data continuity/price scale warnings at a given decision position (0 to 127).";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string? ExampleArgumentsJson => "{\"modelId\":\"active\",\"symbol\":\"7203-T\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(ModelIdArgument, "The identifier of a registered RL model generation, or 'active' for the current active RL model.", required: true)
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable. Default: the model's trained symbol.")
        .String(AsOfDateArgument, $"Anchor date ({ToolJson.DateFormat}) of the observation window. Default: the latest valid anchor date.")
        .Integer(DecisionPositionArgument, $"Decision step within the episode horizon (0 to {RlContract.EpisodeHorizon - 1}). Default: 0.", defaultValue: 0, minimum: 0, maximum: RlContract.EpisodeHorizon - 1)
        .String(PolicyIdArgument, "The evaluation policy ID. Default: the model's target policy.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string? modelIdArg = ToolArgs.String(arguments, ModelIdArgument);
        if (string.IsNullOrWhiteSpace(modelIdArg))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ModelIdArgument}' is required.", ModelIdArgument);
        }

        string resolvedModelId = modelIdArg;
        if (string.Equals(modelIdArg, "active", StringComparison.OrdinalIgnoreCase))
        {
            resolvedModelId = _registry.RlActiveId
                ?? throw new ToolException(ToolErrorCodes.InvalidArgument, "No RL model is currently active in the registry.", ModelIdArgument, "Specify an explicit registered modelId.");
        }

        RlGeneration generation = _registry.GetRlGeneration(resolvedModelId)
            ?? throw new ToolException(ToolErrorCodes.ArtifactNotFound, $"RL model '{modelIdArg}' was not found in the registry.", ModelIdArgument, "Use a valid registered RL modelId or 'active'.");

        string rawSymbol = ToolArgs.String(arguments, SymbolArgument) ?? generation.Symbol;
        string symbol = await _symbols.ResolveAsync(rawSymbol, SymbolArgument, cancellationToken).ConfigureAwait(false);

        int decisionPosition = ToolArgs.NonNegativeWithin(arguments, DecisionPositionArgument, 0, RlContract.EpisodeHorizon - 1);

        DateOnly? asOfDate = ToolArgs.Date(arguments, AsOfDateArgument);
        if (asOfDate is null)
        {
            try
            {
                RlAnchorRange range = await _dataService.GetAnchorRangeAsync(resolvedModelId, symbol, cancellationToken).ConfigureAwait(false);
                asOfDate = range.LatestAnchor;
            }
            catch (RlExternalReplayException ex)
            {
                throw MapReplayException(ex);
            }
        }

        string policyId = ToolArgs.String(arguments, PolicyIdArgument) ?? generation.Target;

        var request = new RlExternalReplayRequest(resolvedModelId, symbol, asOfDate.Value, decisionPosition, policyId);
        RlExternalReplayResult result;
        try
        {
            result = await _dataService.ReplayExternalAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (RlExternalReplayException ex)
        {
            throw MapReplayException(ex);
        }
        catch (InvalidDataException ex)
        {
            throw new ToolException(ToolErrorCodes.DataUnavailable, ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, ex.Message, DecisionPositionArgument);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, ex.Message);
        }

        return ToolJson.Build(writer => Write(writer, result, decisionPosition, modelIdArg, resolvedModelId));
    }

    private static ToolException MapReplayException(RlExternalReplayException ex)
    {
        return ex.Reason switch
        {
            RlExternalReplayRejection.NotEnoughBarsBefore => new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                ex.Message,
                AsOfDateArgument,
                ex.EarliestAnchor is not null ? $"Earliest valid anchor date is {ex.EarliestAnchor.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}." : null),
            RlExternalReplayRejection.NotEnoughBarsAfter => new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                ex.Message,
                AsOfDateArgument,
                ex.LatestAnchor is not null ? $"Latest valid anchor date is {ex.LatestAnchor.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}." : null),
            RlExternalReplayRejection.NoLocalData => new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                ex.Message,
                SymbolArgument),
            RlExternalReplayRejection.ContinuousNotSupported => new ToolException(
                ToolErrorCodes.InvalidArgument,
                ex.Message,
                ModelIdArgument),
            RlExternalReplayRejection.PolicyNotAvailable => new ToolException(
                ToolErrorCodes.InvalidArgument,
                ex.Message,
                PolicyIdArgument),
            RlExternalReplayRejection.SourceUnavailable => new ToolException(
                ToolErrorCodes.DataUnavailable,
                ex.Message),
            _ => new ToolException(ToolErrorCodes.InternalError, ex.Message),
        };
    }

    private static void Write(
        Utf8JsonWriter writer,
        RlExternalReplayResult result,
        int decisionPosition,
        string requestedModelId,
        string resolvedModelId)
    {
        RlQSnapshot snapshot = result.Snapshot;
        writer.WriteStartObject();
        writer.WriteString(ModelIdArgument, requestedModelId);
        writer.WriteString("resolvedModelId", resolvedModelId);
        writer.WriteString("symbol", snapshot.Symbol);
        writer.WriteString("anchorDate", result.ResolvedAnchorDate.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        writer.WriteNumber(DecisionPositionArgument, decisionPosition);

        writer.WriteStartObject("qValues");
        writer.WriteNumber("hold", snapshot.QValues.Length > 0 ? snapshot.QValues[0] : 0f);
        writer.WriteNumber("buy", snapshot.QValues.Length > 1 ? snapshot.QValues[1] : 0f);
        writer.WriteNumber("sell", snapshot.QValues.Length > 2 ? snapshot.QValues[2] : 0f);
        writer.WriteEndObject();

        writer.WriteString("selectedAction", snapshot.SelectedAction.ToString());
        writer.WriteNumber("discountFactor", (double)RlContract.Gamma);
        writer.WriteString("policyId", snapshot.PolicyId);
        writer.WriteBoolean("hasPriceScaleWarning", snapshot.ScalerFindings.Length > 0);
        writer.WriteBoolean("hasDataGapWarning", snapshot.ContinuityFindings.Length > 0);
        writer.WriteEndObject();
    }
}
