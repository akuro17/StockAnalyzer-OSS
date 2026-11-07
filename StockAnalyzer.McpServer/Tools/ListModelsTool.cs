using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_list_models: lists registered supervised and reinforcement learning model generations,
/// paged, optionally filtered by kind and symbol.
/// </summary>
internal sealed class ListModelsTool : IMcpTool
{
    private const string KindArgument = "kind";
    private const string SymbolArgument = "symbol";
    private const string OffsetArgument = "offset";
    private const string LimitArgument = "limit";

    private const string KindAll = "All";
    private const string KindSupervised = "Supervised";
    private const string KindRl = "ReinforcementLearning";

    private readonly ReadOnlyModelGenerationRegistry _registry;
    private readonly SymbolResolver _symbols;
    private readonly McpLimits _limits;

    public ListModelsTool(
        SymbolResolver symbols,
        ReadOnlyModelGenerationRegistry? registry = null,
        McpLimits? limits = null)
    {
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _registry = registry ?? new ReadOnlyModelGenerationRegistry();
        _limits = limits ?? new McpLimits();
    }

    public string Name => "sa_list_models";

    public string Description =>
        "Lists registered supervised and reinforcement learning model generations, sorted by creation date descending. " +
        "kind filters by 'All', 'Supervised', or 'ReinforcementLearning'. " +
        "symbol optionally filters to models applicable to or trained on a specific canonical symbol. " +
        "Contains zero file paths.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"kind\":\"All\",\"limit\":20}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(KindArgument, "Filter by model kind: 'All', 'Supervised', or 'ReinforcementLearning'. Default 'All'.", allowedValues: new[] { KindAll, KindSupervised, KindRl })
        .String(SymbolArgument, "Optional canonical ticker symbol (e.g. 7203-T) to filter models by.")
        .Integer(OffsetArgument, "Number of models to skip. Default 0.", defaultValue: 0, minimum: 0)
        .Integer(LimitArgument, $"Page size. Default {_limits.MaxListedModels}, at most {_limits.MaxListedModels}.", defaultValue: _limits.MaxListedModels, minimum: 1, maximum: _limits.MaxListedModels)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string kindRaw = ToolArgs.String(arguments, KindArgument)?.Trim() ?? KindAll;
        string kind;
        if (string.Equals(kindRaw, KindAll, StringComparison.OrdinalIgnoreCase))
        {
            kind = KindAll;
        }
        else if (string.Equals(kindRaw, KindSupervised, StringComparison.OrdinalIgnoreCase))
        {
            kind = KindSupervised;
        }
        else if (string.Equals(kindRaw, KindRl, StringComparison.OrdinalIgnoreCase))
        {
            kind = KindRl;
        }
        else
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{KindArgument}' must be '{KindAll}', '{KindSupervised}', or '{KindRl}'.",
                KindArgument,
                $"Use '{KindAll}', '{KindSupervised}', or '{KindRl}'.");
        }

        string? rawSymbol = ToolArgs.String(arguments, SymbolArgument);
        string? resolvedSymbol = null;
        if (rawSymbol is not null)
        {
            resolvedSymbol = await _symbols.ResolveAsync(rawSymbol, SymbolArgument, cancellationToken).ConfigureAwait(false);
        }

        long offsetLong = ToolArgs.Integer(arguments, OffsetArgument) ?? 0;
        if (offsetLong < 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{OffsetArgument}' must not be negative.", OffsetArgument);
        }
        // An offset past any list is an empty page, not an arithmetic failure: the echo keeps the requested value, the skip is clamped.
        int skip = (int)Math.Min(offsetLong, int.MaxValue);

        int limit = ToolArgs.PositiveWithin(arguments, LimitArgument, _limits.MaxListedModels, _limits.MaxListedModels);

        IReadOnlyList<ModelCatalogEntry> allEntries = _registry.EnumerateCatalogEntries();

        IEnumerable<ModelCatalogEntry> filtered = allEntries;
        if (kind != KindAll)
        {
            filtered = filtered.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
        }

        if (resolvedSymbol is not null)
        {
            filtered = filtered.Where(e => e.Symbols.Any(s => string.Equals(s, resolvedSymbol, StringComparison.OrdinalIgnoreCase)));
        }

        List<ModelCatalogEntry> matched = filtered.ToList();
        int totalCount = matched.Count;
        List<ModelCatalogEntry> page = matched.Skip(skip).Take(limit).ToList();
        int returnedCount = page.Count;
        bool hasMore = (long)skip + returnedCount < totalCount;

        string? activeSupervisedId = _registry.ActiveId;
        string? activeRlId = _registry.RlActiveId;

        return ToolJson.Build(writer => Write(
            writer,
            totalCount,
            offsetLong,
            limit,
            returnedCount,
            hasMore,
            activeSupervisedId,
            activeRlId,
            page));
    }

    private static void Write(
        Utf8JsonWriter writer,
        int totalCount,
        long offset,
        int limit,
        int returnedCount,
        bool hasMore,
        string? activeSupervisedId,
        string? activeRlId,
        IReadOnlyList<ModelCatalogEntry> page)
    {
        writer.WriteStartObject();
        writer.WriteNumber("totalCount", totalCount);
        writer.WriteNumber("offset", offset);
        writer.WriteNumber("limit", limit);
        writer.WriteNumber("returnedCount", returnedCount);
        writer.WriteBoolean("hasMore", hasMore);

        writer.WriteStartObject("activeModelId");
        if (activeSupervisedId is not null)
            writer.WriteString("supervised", activeSupervisedId);
        else
            writer.WriteNull("supervised");

        if (activeRlId is not null)
            writer.WriteString("reinforcementLearning", activeRlId);
        else
            writer.WriteNull("reinforcementLearning");
        writer.WriteEndObject();

        writer.WriteStartArray("models");
        foreach (var m in page)
        {
            writer.WriteStartObject();
            writer.WriteString("modelId", m.ModelId);
            writer.WriteString("kind", m.Kind);

            writer.WriteStartArray("symbols");
            foreach (var s in m.Symbols)
            {
                writer.WriteStringValue(s);
            }
            writer.WriteEndArray();

            writer.WriteString("timeFrame", m.Timeframe);
            writer.WriteString("createdAt", ToolJson.FormatTimestampUtc(m.CreatedAt));
            writer.WriteString("createdAtSource", m.CreatedAtSource);
            writer.WriteBoolean("isActive", m.IsActive);

            writer.WriteStartObject("contract");
            writer.WriteNumber("windowSize", m.WindowSize);
            writer.WriteNumber("horizonBars", m.HorizonBars);
            writer.WriteString("targetType", m.TargetType);
            writer.WriteNumber("classCount", m.ClassCount);
            // Descriptive attributes that do not apply (or are unknown) are left out, never null: trainingMode is supervised only, architecture reinforcement learning only.
            if (m.TrainingMode is not null)
            {
                writer.WriteString("trainingMode", m.TrainingMode);
            }

            if (m.Architecture is not null)
            {
                writer.WriteString("architecture", m.Architecture);
            }
            writer.WriteEndObject();

            writer.WriteStartObject("metrics");
            if (m.Score.HasValue)
            {
                writer.WriteString("metricName", string.Equals(m.TargetType, PredictionModelMetadata.TargetTypeRegression, StringComparison.OrdinalIgnoreCase) ? "RMSE" : "macro-F1");
                writer.WriteNumber("score", m.Score.Value);
            }
            if (m.EvaluationRevision is not null)
            {
                writer.WriteString("evaluationRevision", m.EvaluationRevision);
            }
            if (m.ValidationReturn.HasValue)
            {
                writer.WriteNumber("validationReturn", m.ValidationReturn.Value);
            }
            if (m.OosReturn.HasValue)
            {
                writer.WriteNumber("oosReturn", m.OosReturn.Value);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
