using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_model_attention: computes the attention weights of the newest prediction for a registered model generation.
/// Execution tool (opt-in under --enable-execution). Zero-mutation.
/// </summary>
internal sealed class GetModelAttentionTool : IMcpTool
{
    private const string ModelIdArgument = "modelId";
    private const string SymbolArgument = "symbol";
    private const string AsOfDateArgument = "asOfDate";

    private readonly ReadOnlyModelGenerationRegistry _registry;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly IAttentionService _attentionService;
    private readonly McpLimits _limits;
    private readonly ErrorMessageSanitizer _sanitizer;

    public GetModelAttentionTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        IStockAnalyzerSettings? settings = null,
        ReadOnlyModelGenerationRegistry? registry = null,
        IAttentionService? attentionService = null,
        McpLimits? limits = null,
        ErrorMessageSanitizer? sanitizer = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _registry = registry ?? new ReadOnlyModelGenerationRegistry();
        _limits = limits ?? new McpLimits();
        _sanitizer = sanitizer ?? new ErrorMessageSanitizer(Array.Empty<string?>());
        _attentionService = attentionService ?? new AttentionService(
            _registry,
            settings ?? FallbackSettings(),
            new MLDataProcessor());
    }

    private static IStockAnalyzerSettings FallbackSettings()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddStockAnalyzerSettingsOptions(configuration);
        services.AddSingleton<IStockAnalyzerSettings, StockAnalyzerSettings>();
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IStockAnalyzerSettings>();
    }

    public string Name => "sa_get_model_attention";

    public string Description =>
        "Computes attention weights of the prediction over historical bars for a registered model generation. " +
        "Requires --enable-execution. Zero-mutation; results are not persisted.";

    public bool IsHeavy => false;

    public bool IsExecution => true;

    public string? ExampleArgumentsJson => "{\"modelId\":\"active\",\"symbol\":\"7203-T\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(ModelIdArgument, "The identifier of a registered model generation, or 'active' for the current active model.", required: true)
        .String(SymbolArgument, "Canonical ticker symbol (e.g. 7203-T) whose bars feed the model.", required: true)
        .String(AsOfDateArgument, $"Optional evaluation date ({ToolJson.DateFormat}); defaults to the latest stored bar.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (modelIdRaw, resolvedModelId, manifest) = RegisteredModelResolver.Resolve(_registry, arguments, ModelIdArgument);

        string? symbolRaw = ToolArgs.String(arguments, SymbolArgument)?.Trim();
        if (string.IsNullOrWhiteSpace(symbolRaw))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{SymbolArgument}' is required.", SymbolArgument);
        }
        string resolvedSymbol = await _symbols.ResolveAsync(symbolRaw, SymbolArgument, cancellationToken).ConfigureAwait(false);

        int requestedBars = Math.Min(_limits.MaxCandlesPerRequest, AttentionContract.MaxWindow);
        DateOnly? asOf = ToolArgs.Date(arguments, AsOfDateArgument);
        IReadOnlyList<CandleData> candles = asOf.HasValue
            ? await _gateway.GetCandlesUpToAsync(resolvedSymbol, TimeFrame.D1, asOf.Value, requestedBars, cancellationToken).ConfigureAwait(false)
            : await _gateway.GetCandlesAsync(resolvedSymbol, TimeFrame.D1, requestedBars, cancellationToken).ConfigureAwait(false);

        if (candles.Count < AttentionContract.MinWindow)
        {
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                $"Insufficient price history for symbol '{resolvedSymbol}' (got {candles.Count} bars, need at least {AttentionContract.MinWindow}).",
                SymbolArgument);
        }

        var key = new AttentionRequestKey(
            resolvedModelId,
            resolvedSymbol,
            manifest.Timeframe,
            candles[^1].Timestamp,
            0);

        AttentionResult result = await _attentionService.ComputeMemberAsync(key, candles, cancellationToken).ConfigureAwait(false);

        if (result.Status == AttentionStatus.Failed)
        {
            string detail = _sanitizer.Sanitize(result.Detail ?? "Attention computation failed.");
            if (result.Detail is not null && result.Detail.Contains("history is shorter than the model window", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ToolErrorCodes.InsufficientMarketData, detail, SymbolArgument);
            }
            throw new ToolException(ToolErrorCodes.InternalError, detail);
        }

        return ToolJson.Build(writer => Write(
            writer,
            modelIdRaw,
            resolvedModelId,
            resolvedSymbol,
            ToolJson.FormatDate(candles[^1].Timestamp),
            result,
            _sanitizer));
    }

    private static void Write(
        Utf8JsonWriter writer,
        string requestedModelId,
        string resolvedModelId,
        string symbol,
        string asOfDate,
        AttentionResult result,
        ErrorMessageSanitizer sanitizer)
    {
        writer.WriteStartObject();
        writer.WriteString("modelId", requestedModelId);
        writer.WriteString("resolvedModelId", resolvedModelId);
        writer.WriteString("symbol", symbol);
        writer.WriteString("asOfDate", asOfDate);
        writer.WriteString("status", result.Status.ToString().ToLowerInvariant());

        if (result.Reason is not null)
        {
            writer.WriteString("reason", sanitizer.Sanitize(result.Reason));
        }
        if (result.Detail is not null)
        {
            writer.WriteString("detail", sanitizer.Sanitize(result.Detail));
        }

        if (result.Status == AttentionStatus.Ready && result.Snapshot is { } snapshot)
        {
            writer.WriteStartObject("modelContract");
            writer.WriteNumber("windowSize", snapshot.Timestamps.Length);
            writer.WriteNumber("heads", snapshot.Heads);
            writer.WriteString("architecture", AttentionContract.Architecture);
            writer.WriteString("layout", AttentionContract.Layout);
            writer.WriteString("unit", AttentionContract.Unit);
            writer.WriteEndObject();

            writer.WriteStartArray("attention");
            int total = snapshot.Timestamps.Length;
            for (int i = 0; i < total; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("barOffset", -(total - 1 - i));
                writer.WriteString("date", ToolJson.FormatDate(snapshot.Timestamps[i]));
                writer.WriteNumber("weight", snapshot.Weights[i]);
                writer.WriteNumber("relative", snapshot.Relative[i]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteStartArray("attention");
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
