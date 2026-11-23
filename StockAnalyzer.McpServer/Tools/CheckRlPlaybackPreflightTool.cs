using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_check_rl_playback_preflight: inspects feasibility and anchor date range for reinforcement learning playback.</summary>
internal sealed class CheckRlPlaybackPreflightTool : IMcpTool
{
    private const string ModelIdArgument = "modelId";
    private const string SymbolArgument = "symbol";
    private const string GenericPreflightFailureReason = "RL playback preflight check failed.";

    private readonly SymbolResolver _symbols;
    private readonly IRlModelGenerationRegistry _registry;
    private readonly RlDialogDataService? _dataService;
    private readonly RlLocalSourceFolders? _sourceFolders;
    private readonly ErrorMessageSanitizer _sanitizer;
    private readonly IReadOnlyStockAnalyzerGateway? _gateway;

    public CheckRlPlaybackPreflightTool(
        SymbolResolver symbols,
        IRlModelGenerationRegistry? registry = null,
        RlDialogDataService? dataService = null,
        RlLocalSourceFolders? sourceFolders = null,
        ErrorMessageSanitizer? sanitizer = null,
        IReadOnlyStockAnalyzerGateway? gateway = null)
    {
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _registry = registry ?? new ReadOnlyModelGenerationRegistry();
        _dataService = dataService;
        _sourceFolders = sourceFolders;
        _sanitizer = sanitizer ?? new ErrorMessageSanitizer(Array.Empty<string?>());
        _gateway = gateway;
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
            resolvedSymbol = await _symbols.ResolveAsync(rawSymbol, SymbolArgument, cancellationToken, allowSynthetic: false).ConfigureAwait(false);
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
            reason = await TryEnrichInvalidOhlcvReasonAsync(symbol, ex.Message, ex.Reason == RlExternalReplayRejection.SourceUnavailable, cancellationToken).ConfigureAwait(false);
            earliestAnchor = ex.EarliestAnchor;
            latestAnchor = ex.LatestAnchor;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            canReplay = false;
            reason = await TryEnrichInvalidOhlcvReasonAsync(symbol, ex.Message, isSourceUnavailable: false, cancellationToken).ConfigureAwait(false);
        }

        return BuildReplayReport(modelIdArg, symbol, generation.Symbol, generation.Target, canReplay, reason, earliestAnchor, latestAnchor, resolvedModelId);
    }

    private string BuildReplayReport(
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
                string sanitizedReason = _sanitizer.Sanitize(reason);
                writer.WriteString("reason", string.IsNullOrWhiteSpace(sanitizedReason) ? GenericPreflightFailureReason : sanitizedReason);
            }
            else
            {
                writer.WriteNull("reason");
            }

            writer.WriteEndObject();
        });
    }

    private async Task<string> TryEnrichInvalidOhlcvReasonAsync(string symbol, string baseReason, bool isSourceUnavailable, CancellationToken cancellationToken)
    {
        if (_gateway is null
            || string.IsNullOrWhiteSpace(symbol)
            || (!isSourceUnavailable
                && !baseReason.Contains("OHLCV", StringComparison.OrdinalIgnoreCase)
                && !baseReason.Contains("source bar", StringComparison.OrdinalIgnoreCase)))
        {
            return baseReason;
        }

        try
        {
            IReadOnlyList<CandleData> stored = await _gateway.GetCandlesAsync(symbol, TimeFrame.D1, int.MaxValue, cancellationToken).ConfigureAwait(false);
            if (CandleInvariant.TryFindFirstViolation(stored, out CandleData badBar, out IReadOnlyList<CandleViolationCode> codes))
            {
                return $"{baseReason} First invalid bar: {CandleInvariant.FormatBarViolation(symbol, badBar, codes)}.";
            }
        }
        catch (DataUnavailableException)
        {
            // Keep the original reason when the stored series cannot be read.
        }

        return baseReason;
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
