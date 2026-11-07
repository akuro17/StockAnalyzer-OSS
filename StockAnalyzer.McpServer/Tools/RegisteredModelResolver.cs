using System;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// SSoT helper for resolving and validating registered model identifiers in MCP tools.
/// Enforces path traversal prevention and uniform active/generation resolution.
/// </summary>
internal static class RegisteredModelResolver
{
    public const string ModelIdArgument = "modelId";

    public readonly record struct ResolvedModel(string RequestedId, string ResolvedId, ModelGenerationManifest Manifest);

    /// <summary>The time frame a model was trained on, as the prediction type the analysis services name it and as the core chart time frame the bars are read in.</summary>
    public readonly record struct ModelTimeFrame(TimeframeType Type, TimeFrame Frame);

    /// <summary>
    /// Reads the time frame the manifest names (<c>daily</c>, <c>weekly</c>, <c>monthly</c>; case does not matter). False for anything else,
    /// including an intraday name and a bare number (an enum parser would accept both). The supported set is the one of
    /// <see cref="MonitoringTimeframes"/>, so the mapping to the core time frame is not repeated here.
    /// </summary>
    public static bool TryGetTimeFrame(ModelGenerationManifest manifest, out ModelTimeFrame result)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        result = default;
        string? text = manifest.Timeframe?.Trim();
        if (string.IsNullOrEmpty(text)
            || !Enum.TryParse(text, ignoreCase: true, out TimeframeType type)
            || !string.Equals(type.ToString(), text, StringComparison.OrdinalIgnoreCase)
            || !MonitoringTimeframes.IsSupported(type))
        {
            return false;
        }

        result = new ModelTimeFrame(type, MonitoringTimeframes.ToCoreTimeFrame(type));
        return true;
    }

    /// <summary>Like <see cref="TryGetTimeFrame"/>, but a time frame the server cannot read is <c>UNSUPPORTED_TIMEFRAME</c> on <paramref name="parameterName"/>.</summary>
    public static ModelTimeFrame RequireTimeFrame(ModelGenerationManifest manifest, string parameterName = ModelIdArgument)
    {
        if (TryGetTimeFrame(manifest, out ModelTimeFrame frame))
        {
            return frame;
        }

        throw new ToolException(
            ToolErrorCodes.UnsupportedTimeFrame,
            "The model was trained on a time frame this server cannot read.",
            parameterName,
            "Use a model trained on daily, weekly or monthly bars.");
    }

    public static ResolvedModel Resolve(
        ReadOnlyModelGenerationRegistry registry,
        JsonElement arguments,
        string parameterName = ModelIdArgument)
    {
        ArgumentNullException.ThrowIfNull(registry);

        string? raw = ToolArgs.String(arguments, parameterName)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{parameterName}' is required.",
                parameterName);
        }

        if (!PlainFileName.IsValid(raw) || raw.StartsWith('.'))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{parameterName}' must be a valid model identifier without path separators or parent segments.",
                parameterName,
                "Use a valid model ID or 'active'.");
        }

        if (string.Equals(raw, "active", StringComparison.OrdinalIgnoreCase))
        {
            string? activeId = registry.ActiveId;
            if (string.IsNullOrWhiteSpace(activeId))
            {
                throw new ToolException(
                    ToolErrorCodes.ArtifactNotFound,
                    "No model is currently active in the registry.",
                    parameterName);
            }

            ModelGenerationManifest activeManifest = registry.TryGetManifest(activeId)
                ?? throw new ToolException(
                    ToolErrorCodes.ArtifactNotFound,
                    $"Active model '{activeId}' was not found in the registry.",
                    parameterName);

            return new ResolvedModel(raw, activeManifest.ModelId, activeManifest);
        }

        ModelGenerationManifest manifest = registry.TryGetManifest(raw)
            ?? throw new ToolException(
                ToolErrorCodes.ArtifactNotFound,
                $"Model '{raw}' was not found in the registry.",
                parameterName);

        return new ResolvedModel(raw, manifest.ModelId, manifest);
    }
}
